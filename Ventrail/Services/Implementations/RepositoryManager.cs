using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using Ventrail.Models;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Version control core built on LibGit2Sharp.
///
/// Design note: a LibGit2Sharp Repository object holds file handles and is not
/// thread-safe, so every method opens it with "using", does its job and closes it.
/// That means a background thread (Task.Run in the ViewModel) can call any method
/// safely, and the .git folder is never left locked.
/// </summary>
public class RepositoryManager : IRepositoryManager
{
    public string RepoPath { get; private set; } = string.Empty;
    public List<CommitNode> CommitHistory { get; private set; } = new();
    public bool IsRepositoryOpen => !string.IsNullOrEmpty(RepoPath);

    // ---------- Fig 3.2: directory selection + repository initialisation ----------

    public bool InitRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        // Discover() walks up from the chosen folder looking for a .git folder, so picking
        // a sub-folder of an existing repository opens that repository instead of
        // accidentally creating a second one nested inside it.
        string? gitDir = Repository.Discover(path);

        // Not tracked yet: run the equivalent of "git init".
        if (gitDir == null)
            gitDir = Repository.Init(path);

        using var repo = new Repository(gitDir);
        RepoPath = repo.Info.WorkingDirectory;

        GetCommitHistory();
        return true;
    }

    // ---------- Fig 3.3: staging interface ----------

    public List<FileAsset> ScanWorkingDirectory()
    {
        EnsureOpen();
        using var repo = new Repository(RepoPath);

        var options = new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,   // list files inside new folders, not just the folder
            IncludeIgnored = false
        };

        var result = new List<FileAsset>();
        foreach (StatusEntry entry in repo.RetrieveStatus(options))
        {
            FileStatus s = entry.State;
            if (s == FileStatus.Unaltered || s.HasFlag(FileStatus.Ignored))
                continue;

            string fullPath = Path.Combine(RepoPath, entry.FilePath);
            result.Add(new FileAsset
            {
                FileName = Path.GetFileName(entry.FilePath),
                FilePath = entry.FilePath,
                SizeInBytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0,
                TrackStatus = DescribeStatus(s),
                IsStaged = IsInIndex(s)
            });
        }

        return result.OrderBy(f => f.FilePath).ToList();
    }

    public void Stage(string filePath)
    {
        EnsureOpen();
        using var repo = new Repository(RepoPath);
        Commands.Stage(repo, filePath);      // also handles deleted files (stages the removal)
    }

    public void StageAll()
    {
        EnsureOpen();
        using var repo = new Repository(RepoPath);
        Commands.Stage(repo, "*");
    }

    public void Unstage(string filePath)
    {
        EnsureOpen();
        using var repo = new Repository(RepoPath);
        Commands.Unstage(repo, filePath);    // works even before the first commit exists
    }

    // ---------- Fig 3.4: commit execution + DAG history ----------

    public bool Commit(string message, string author)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(message))
            return false;

        using var repo = new Repository(RepoPath);

        // Nothing staged -> nothing to commit. Check first instead of relying on the exception.
        bool hasStaged = repo.RetrieveStatus().Any(e => IsInIndex(e.State));
        if (!hasStaged)
            return false;

        Signature signature = BuildSignature(repo, author);
        try
        {
            // LibGit2Sharp hashes the staged tree, links it to HEAD as its parent,
            // writes the commit object and moves the branch pointer forward.
            repo.Commit(message.Trim(), signature, signature);
        }
        catch (EmptyCommitException)
        {
            return false;
        }

        GetCommitHistory();
        return true;
    }

    public List<CommitNode> GetCommitHistory()
    {
        EnsureOpen();
        using var repo = new Repository(RepoPath);

        // Brand-new repository: HEAD points at a branch with no commits yet.
        if (repo.Head.Tip == null)
        {
            CommitHistory = new List<CommitNode>();
            return CommitHistory;
        }

        var filter = new CommitFilter
        {
            IncludeReachableFrom = repo.Head,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
        };

        CommitHistory = repo.Commits.QueryBy(filter)
            .Take(500) // cap so huge histories don't freeze the UI; add paging later if needed
            .Select(c => new CommitNode
            {
                Hash = c.Sha,
                Message = c.MessageShort,
                Author = c.Author.Name,
                Timestamp = c.Author.When.LocalDateTime,
                ParentHashes = c.Parents.Select(p => p.Sha).ToList()
            })
            .ToList();

        return CommitHistory;
    }

    public string GetCurrentBranchName()
    {
        if (!IsRepositoryOpen) return string.Empty;
        using var repo = new Repository(RepoPath);
        return repo.Head.FriendlyName;   // "master" or "main" depending on the user's Git config
    }

    public List<FileAsset> GetFilesInCommit(string commitHash)
    {
        EnsureOpen();
        using var repo = new Repository(RepoPath);
        Commit commit = repo.Lookup<Commit>(commitHash)
            ?? throw new ArgumentException($"Version {commitHash} was not found.");

        // Compare this snapshot with its parent (null parent = first commit, everything is new).
        Tree? parentTree = commit.Parents.FirstOrDefault()?.Tree;
        using TreeChanges changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree);

        return changes
            .Where(c => c.Status != ChangeKind.Deleted)   // a deleted file can't be restored from this version
            .Select(c => new FileAsset
            {
                FileName = Path.GetFileName(c.Path),
                FilePath = c.Path,
                TrackStatus = c.Status == ChangeKind.Added ? "Added" : c.Status.ToString(),
                SizeInBytes = (commit[c.Path]?.Target as Blob)?.Size ?? 0,
                IsStaged = false
            })
            .OrderBy(f => f.FilePath)
            .ToList();
    }

    public void RestoreFileFromCommit(string filePath, string commitHash)
    {
        EnsureOpen();
        using var repo = new Repository(RepoPath);

        // Same as "git checkout <commit> -- <file>": the old version replaces the file
        // on disk and is staged. Nothing in history is lost, and the user can still undo.
        repo.CheckoutPaths(commitHash, new[] { filePath },
            new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
    }

    // ---------- helpers ----------

    private void EnsureOpen()
    {
        if (!IsRepositoryOpen)
            throw new InvalidOperationException("No repository is open. Select a folder first.");
    }

    private static bool IsInIndex(FileStatus s) =>
        s.HasFlag(FileStatus.NewInIndex) ||
        s.HasFlag(FileStatus.ModifiedInIndex) ||
        s.HasFlag(FileStatus.DeletedFromIndex) ||
        s.HasFlag(FileStatus.RenamedInIndex) ||
        s.HasFlag(FileStatus.TypeChangeInIndex);

    // Plain-language labels for the UI (no "index", "workdir" jargon).
    private static string DescribeStatus(FileStatus s)
    {
        if (s.HasFlag(FileStatus.Conflicted)) return "Conflict";
        if (s.HasFlag(FileStatus.NewInWorkdir) || s.HasFlag(FileStatus.NewInIndex)) return "Added";
        if (s.HasFlag(FileStatus.DeletedFromWorkdir) || s.HasFlag(FileStatus.DeletedFromIndex)) return "Deleted";
        if (s.HasFlag(FileStatus.RenamedInWorkdir) || s.HasFlag(FileStatus.RenamedInIndex)) return "Renamed";
        return "Modified";
    }

    private static Signature BuildSignature(Repository repo, string author)
    {
        // Prefer the user's global Git identity if they have one configured.
        Signature? configured = repo.Config.BuildSignature(DateTimeOffset.Now);
        string name = !string.IsNullOrWhiteSpace(author) ? author.Trim()
                    : configured?.Name ?? Environment.UserName;
        string email = configured?.Email ?? $"{Environment.UserName}@ventrail.local";
        return new Signature(name, email, DateTimeOffset.Now);
    }
}

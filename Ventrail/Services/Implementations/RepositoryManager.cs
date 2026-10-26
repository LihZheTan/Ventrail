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
///
/// Week 6: every operation goes through Use(...), which turns low-level Git and
/// Windows errors into plain-language VentrailExceptions.
/// </summary>
public class RepositoryManager : IRepositoryManager
{
    public string RepoPath { get; private set; } = string.Empty;
    public List<CommitNode> CommitHistory { get; private set; } = new();
    public bool IsRepositoryOpen => !string.IsNullOrEmpty(RepoPath);

    // Written into every NEW repository so temporary and system files never end up in a version.
    // An existing .gitignore is never overwritten.
    internal const string DefaultGitignore =
        "# Created by Ventrail. Files matching these patterns are skipped when saving versions.\n" +
        "# You can edit this file; delete a line to start tracking those files again.\n" +
        "\n" +
        "# Windows / macOS system files\n" +
        "Thumbs.db\n" +
        "ehthumbs.db\n" +
        "desktop.ini\n" +
        "$RECYCLE.BIN/\n" +
        ".DS_Store\n" +
        "\n" +
        "# Office and LibreOffice temporary / lock files (created while a document is open)\n" +
        "~$*\n" +
        "~*.tmp\n" +
        "*.tmp\n" +
        ".~lock.*#\n" +
        "\n" +
        "# Build output and tool folders\n" +
        "bin/\n" +
        "obj/\n" +
        ".vs/\n" +
        "node_modules/\n" +
        "__pycache__/\n";

    static RepositoryManager()
    {
        // Git normally refuses folders owned by another Windows account. That is common for
        // non-technical users (USB/exFAT drives, folders copied from an old PC) and the error
        // is confusing. Ventrail never runs Git hooks or external commands, so the attack this
        // check prevents (running code from a planted repo config) does not apply here.
        GlobalSettings.SetOwnerValidation(false);
    }

    // ---------- Fig 3.2: directory selection + repository initialisation ----------

    public bool InitRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        string folder = Path.GetFullPath(path);

        // Discover() walks up from the chosen folder looking for a .git folder, so picking
        // a sub-folder of an existing repository opens that repository instead of
        // accidentally creating a second one nested inside it.
        string? gitDir = Repository.Discover(folder);

        // Not tracked yet: run the equivalent of "git init".
        if (gitDir == null)
        {
            string? reason = WhyFolderIsTooBroad(folder);
            if (reason != null)
                throw new VentrailException(
                    $"Ventrail can't track \"{folder}\" because it is {reason}. " +
                    "Choose a project folder inside it instead (for example a folder in Documents).");

            gitDir = Translate(() => Repository.Init(folder), folder);
            WriteDefaultGitignore(folder);
        }

        string workDir = Translate(() =>
        {
            using var repo = new Repository(gitDir);
            if (repo.Info.IsBare)
                throw new VentrailException(
                    "This folder is a Git storage folder without working files, so there is nothing to track. " +
                    "Choose the folder that contains your documents.");
            return repo.Info.WorkingDirectory;
        }, folder);

        RepoPath = workDir;
        GetCommitHistory();
        return true;
    }

    // ---------- Fig 3.3: staging interface ----------

    public List<FileAsset> ScanWorkingDirectory() => Use(repo =>
    {
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

            result.Add(new FileAsset
            {
                FileName = Path.GetFileName(entry.FilePath),
                FilePath = entry.FilePath,
                SizeInBytes = SafeFileSize(Path.Combine(RepoPath, entry.FilePath)),
                TrackStatus = DescribeStatus(s),
                IsStaged = IsInIndex(s)
            });
        }

        return result.OrderBy(f => f.FilePath).ToList();
    });

    // Commands.Stage also handles deleted files (stages the removal).
    public void Stage(string filePath) => Use(repo => Commands.Stage(repo, filePath), filePath);

    public void StageAll() => Use(repo => Commands.Stage(repo, "*"));

    // Works even before the first commit exists.
    public void Unstage(string filePath) => Use(repo => Commands.Unstage(repo, filePath), filePath);

    // ---------- Fig 3.4: commit execution + DAG history ----------

    public bool Commit(string message, string author)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        bool committed = Use(repo =>
        {
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
            return true;
        });

        if (committed)
            GetCommitHistory();
        return committed;
    }

    public List<CommitNode> GetCommitHistory()
    {
        CommitHistory = Use(repo =>
        {
            // Brand-new repository: HEAD points at a branch with no commits yet.
            if (repo.Head.Tip == null)
                return new List<CommitNode>();

            var filter = new CommitFilter
            {
                IncludeReachableFrom = repo.Head,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
            };

            return repo.Commits.QueryBy(filter)
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
        });
        return CommitHistory;
    }

    public string GetCurrentBranchName()
    {
        if (!IsRepositoryOpen) return string.Empty;
        return Use(repo => repo.Head.FriendlyName);   // "master" or "main" depending on the user's Git config
    }

    public List<FileAsset> GetFilesInCommit(string commitHash) => Use(repo =>
    {
        Commit commit = repo.Lookup<Commit>(commitHash)
            ?? throw new VentrailException($"Version {commitHash[..Math.Min(7, commitHash.Length)]} was not found.");

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
    });

    public void RestoreFileFromCommit(string filePath, string commitHash) => Use(repo =>
    {
        // Same as "git checkout <commit> -- <file>": the old version replaces the file
        // on disk and is staged. Nothing in history is lost, and the user can still undo.
        repo.CheckoutPaths(commitHash, new[] { filePath },
            new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });

        // Week 6: if another program holds the file open, libgit2 can skip it without an error.
        // Check the file on disk really matches the old version so the user is never told
        // "Restored" when nothing changed. (Reading a locked file throws, which Use() translates.)
        if (repo.Lookup<Commit>(commitHash)?[filePath]?.Target is Blob blob &&
            !FileMatchesBlob(Path.Combine(RepoPath, filePath), blob, filePath))
            throw new VentrailException(
                $"\"{filePath}\" could not be replaced. If it is open in another program, close it and try again.");
    }, filePath);

    // ---------- Week 7: discard changes + diff viewer ----------

    public string? DiscardChanges(string filePath) => Use(repo =>
    {
        StatusEntry entry = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true })
            .FirstOrDefault(e => e.FilePath == filePath)
            ?? throw new VentrailException($"\"{filePath}\" has no unsaved changes to discard.");

        // A rename shows up under the new name; discarding it also brings back the old name.
        string? oldPath = entry.HeadToIndexRenameDetails?.OldFilePath
                       ?? entry.IndexToWorkDirRenameDetails?.OldFilePath;
        Commit? head = repo.Head.Tip;

        // 1. Safety copy: move the current file to the Recycle Bin (or backup folder) first.
        string full = Path.Combine(RepoPath, filePath);
        string? movedTo = File.Exists(full) ? SafeDelete.MoveAway(full, filePath) : null;

        // 2. Files that were never saved are simply taken out of the staging area.
        if (head?[filePath] == null)
        {
            repo.Index.Remove(filePath);
            repo.Index.Write();
        }

        // 3. Files that have a saved version get it back, like "git checkout HEAD -- file".
        var restore = new[] { filePath, oldPath }
            .Where(p => p != null && head?[p] != null)
            .Select(p => p!)
            .ToList();
        if (restore.Count > 0)
            repo.CheckoutPaths(head!.Sha, restore, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });

        return movedTo;
    }, filePath);

    // Bigger files are not compared line by line (too slow, and too long to read anyway).
    private const long MaxDiffBytes = 2 * 1024 * 1024;
    private const int MaxDiffLines = 3000;

    public FileDiff GetFileDiff(string filePath) => Use(repo =>
    {
        var diff = new FileDiff { FilePath = filePath };
        string full = Path.Combine(RepoPath, filePath);
        Blob? saved = repo.Head.Tip?[filePath]?.Target as Blob;
        bool onDisk = File.Exists(full);

        if (Math.Max(onDisk ? SafeFileSize(full) : 0, saved?.Size ?? 0) > MaxDiffBytes)
        {
            diff.Message = "This file is too large to compare line by line. You can still restore or discard the whole file.";
            return diff;
        }
        if (saved?.IsBinary == true || (onDisk && LooksBinary(full)))
        {
            diff.Message = "This kind of file (for example a Word document, spreadsheet or picture) can't be " +
                           "compared line by line. You can still restore or discard the whole file.";
            return diff;
        }

        if (saved == null || !onDisk)
        {
            // New file: every line is added. Deleted file: every line is removed.
            string text = onDisk ? File.ReadAllText(full) : saved!.GetContentText(new FilteringOptions(filePath));
            var kind = onDisk ? DiffLineKind.Added : DiffLineKind.Removed;
            int n = 0;
            foreach (string line in SplitLines(text))
            {
                if (++n > MaxDiffLines) { diff.IsTruncated = true; break; }
                diff.Lines.Add(new DiffLine
                {
                    Kind = kind,
                    Text = line,
                    NewLineNumber = onDisk ? n : null,
                    OldLineNumber = onDisk ? null : n
                });
            }
        }
        else
        {
            // Changed file: last saved version (HEAD) against the file on disk, 3 lines of context.
            Patch patch = repo.Diff.Compare<Patch>(repo.Head.Tip!.Tree, DiffTargets.WorkingDirectory,
                new[] { filePath }, null, new CompareOptions { ContextLines = 3 });
            PatchEntryChanges? entry = patch[filePath];
            if (entry?.IsBinaryComparison == true)
            {
                diff.Message = "This file can't be compared line by line.";
                return diff;
            }
            diff.IsTruncated = ParsePatch(entry?.Patch ?? string.Empty, diff.Lines);
        }

        diff.LinesAdded = diff.Lines.Count(l => l.Kind == DiffLineKind.Added);
        diff.LinesRemoved = diff.Lines.Count(l => l.Kind == DiffLineKind.Removed);
        if (diff.Lines.Count == 0)
            diff.Message = "No text changes (only the file's details, such as line endings, changed).";
        return diff;
    }, filePath);

    // Turns unified-diff text ("@@ -3,4 +3,5 @@", "+added", "-removed", " same") into DiffLines.
    // Returns true if it stopped early because of MaxDiffLines.
    internal static bool ParsePatch(string patch, List<DiffLine> lines)
    {
        int oldNo = 0, newNo = 0;
        bool inHunk = false;
        foreach (string raw in SplitLines(patch))
        {
            if (lines.Count >= MaxDiffLines) return true;

            if (raw.StartsWith("@@"))
            {
                // @@ -oldStart[,count] +newStart[,count] @@ optional section text
                var parts = raw.Split(' ');
                oldNo = int.Parse(parts[1].TrimStart('-').Split(',')[0]);
                newNo = int.Parse(parts[2].TrimStart('+').Split(',')[0]);
                inHunk = true;
                lines.Add(new DiffLine { Kind = DiffLineKind.Hunk, Text = $"Around line {newNo}" });
                continue;
            }
            if (!inHunk || raw.StartsWith("\\")) continue;   // file header lines / "\ No newline at end of file"

            if (raw.StartsWith("+"))
                lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = raw[1..], NewLineNumber = newNo++ });
            else if (raw.StartsWith("-"))
                lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = raw[1..], OldLineNumber = oldNo++ });
            else
                lines.Add(new DiffLine
                {
                    Kind = DiffLineKind.Context,
                    Text = raw.Length > 0 ? raw[1..] : string.Empty,
                    OldLineNumber = oldNo++,
                    NewLineNumber = newNo++
                });
        }
        return false;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        if (text.Length == 0) yield break;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;   // no empty line after the last newline
        for (int i = 0; i < count; i++) yield return lines[i];
    }

    // Same rule Git uses: a zero byte in the first 8000 bytes means "binary".
    private static bool LooksBinary(string fullPath)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[8000];
        int n = fs.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, n) >= 0;
    }

    // ---------- Week 6: error handling ----------

    // Opens the repository for one operation and translates errors into plain language.
    private T Use<T>(Func<Repository, T> work, string? filePath = null)
    {
        EnsureOpen();
        return Translate(() =>
        {
            using var repo = new Repository(RepoPath);
            return work(repo);
        }, RepoPath, filePath);
    }

    private void Use(Action<Repository> work, string? filePath = null) =>
        Use(repo => { work(repo); return true; }, filePath);

    private static T Translate<T>(Func<T> work, string folder, string? filePath = null)
    {
        string what = filePath != null ? $"\"{filePath}\"" : "a file in this folder";
        try
        {
            return work();
        }
        catch (VentrailException)
        {
            throw;
        }
        catch (RepositoryNotFoundException ex)
        {
            throw new VentrailException(
                $"\"{folder}\" can't be found any more. It may have been moved, renamed or deleted, " +
                "or it is on a drive that isn't connected.", ex);
        }
        catch (LockedFileException ex)
        {
            throw new VentrailException(
                "Another program is using the version history of this folder (or an earlier save was " +
                "interrupted). Close other Git tools, wait a moment and try again.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new VentrailException(
                $"Windows didn't allow Ventrail to access {what}. Check that you have permission " +
                "to change this folder, then try again.", ex);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException && IsAccessProblem(ex.Message))
        {
            throw new VentrailException(
                $"Ventrail couldn't read or write {what}. If it is open in another program " +
                "(Word, Excel, Photoshop...), close it and try again. If a sub-folder is protected, " +
                "add it to the .gitignore file so it is skipped.", ex);
        }
    }

    private static bool IsAccessProblem(string message) =>
        message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("could not open", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("failed to open", StringComparison.OrdinalIgnoreCase);

    private void EnsureOpen()
    {
        if (!IsRepositoryOpen)
            throw new VentrailException("No folder is open. Select a folder first.");
    }

    // ---------- Week 6: initialisation helpers ----------

    // Tracking a whole drive, Windows or the user's profile would scan (and copy into history)
    // hundreds of thousands of files, so Ventrail refuses to create a repository there.
    internal static string? WhyFolderIsTooBroad(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string? root = Path.GetPathRoot(full);
        if (root != null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            return "a whole drive";

        var broad = new (Environment.SpecialFolder Folder, string Reason)[]
        {
            (Environment.SpecialFolder.Windows, "the Windows system folder"),
            (Environment.SpecialFolder.ProgramFiles, "the Program Files folder"),
            (Environment.SpecialFolder.ProgramFilesX86, "the Program Files folder"),
            (Environment.SpecialFolder.UserProfile, "your whole user folder"),
            (Environment.SpecialFolder.ApplicationData, "a hidden app settings folder"),
            (Environment.SpecialFolder.LocalApplicationData, "a hidden app settings folder"),
        };
        foreach (var (special, reason) in broad)
        {
            string path = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(path) &&
                string.Equals(Path.TrimEndingDirectorySeparator(path), full, StringComparison.OrdinalIgnoreCase))
                return reason;
        }
        return null;
    }

    private static void WriteDefaultGitignore(string folder)
    {
        string path = Path.Combine(folder, ".gitignore");
        if (File.Exists(path)) return;
        try
        {
            File.WriteAllText(path, DefaultGitignore);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the repository works without it, temporary files just show up as changes.
        }
    }

    // ---------- helpers ----------

    // A file can disappear or be locked between the scan and reading its size; never fail the scan for that.
    private static long SafeFileSize(string fullPath)
    {
        try { return File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // Compares the file on disk with a stored version, after line-ending filters (what checkout writes).
    private static bool FileMatchesBlob(string fullPath, Blob blob, string filePath)
    {
        if (!File.Exists(fullPath)) return false;
        using var expected = blob.GetContentStream(new FilteringOptions(filePath));
        using var actual = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var a = new byte[81920];
        var b = new byte[81920];
        while (true)
        {
            int na = expected.ReadAtLeast(a, a.Length, throwOnEndOfStream: false);
            int nb = actual.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
            if (na != nb || !a.AsSpan(0, na).SequenceEqual(b.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
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

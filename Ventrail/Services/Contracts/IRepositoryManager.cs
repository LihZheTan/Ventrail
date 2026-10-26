using System.Collections.Generic;
using Ventrail.Models;

namespace Ventrail.Services.Contracts;

/// <summary>
/// Version control core (wraps LibGit2Sharp). Matches RepositoryManager in the
/// class diagram (Fig 3.8) and the Initialize / Stage / Commit use cases (Fig 3.9).
/// </summary>
public interface IRepositoryManager
{
    string RepoPath { get; }
    List<CommitNode> CommitHistory { get; }
    bool IsRepositoryOpen { get; }

    // Fig 3.2: open the folder if it is already a repository, otherwise run "git init".
    bool InitRepository(string path);

    // Fig 3.3: compare working directory with the last commit (added / modified / deleted).
    List<FileAsset> ScanWorkingDirectory();

    void Stage(string filePath);
    void StageAll();
    void Unstage(string filePath);

    // Fig 3.4: write the staged snapshot as a new node in the DAG.
    bool Commit(string message, string author);

    List<CommitNode> GetCommitHistory();
    string GetCurrentBranchName();

    // Files that a given version added or changed (shown when a version is selected).
    List<FileAsset> GetFilesInCommit(string commitHash);

    // Bring back a file exactly as it was in an earlier commit (non-destructive:
    // history is untouched; the restored file shows up as a staged change).
    void RestoreFileFromCommit(string filePath, string commitHash);

    // Week 7: throw away unsaved changes to one file and go back to the last saved version.
    // The current copy goes to the Windows Recycle Bin first (or a backup folder on drives
    // without one), so nothing is lost for good. Returns where that copy went, or null if
    // there was no file on disk (e.g. discarding a deletion).
    string? DiscardChanges(string filePath);

    // Week 7: line-by-line comparison of a changed file with its last saved version.
    FileDiff GetFileDiff(string filePath);
}

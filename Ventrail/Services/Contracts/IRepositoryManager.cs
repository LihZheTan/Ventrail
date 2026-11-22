using System.Collections.Generic;
using Ventrail.Models;

namespace Ventrail.Services.Contracts;

/// <summary>
/// Version control core: opens a folder, finds changes, saves and restores versions
/// (wraps LibGit2Sharp).
/// </summary>
public interface IRepositoryManager
{
    string RepoPath { get; }
    List<CommitNode> CommitHistory { get; }
    bool IsRepositoryOpen { get; }

    // Open the folder if it is already a repository, otherwise run "git init".
    bool InitRepository(string path);

    // Compare the folder with the last saved version (added / modified / deleted).
    List<FileAsset> ScanWorkingDirectory();

    void Stage(string filePath);
    void StageAll();
    void Unstage(string filePath);

    // Save the ticked files as a new version.
    bool Commit(string message, string author);

    List<CommitNode> GetCommitHistory();
    string GetCurrentBranchName();

    // Files that a given version added or changed (shown when a version is selected).
    List<FileAsset> GetFilesInCommit(string commitHash);

    // Bring back a file exactly as it was in an earlier commit (non-destructive:
    // history is untouched; the restored file shows up as a staged change).
    void RestoreFileFromCommit(string filePath, string commitHash);

    // Throw away unsaved changes to one file and go back to the last saved version.
    // The current copy goes to the Windows Recycle Bin first (or a backup folder on drives
    // without one), so nothing is lost for good. Returns where that copy went, or null if
    // there was no file on disk (e.g. discarding a deletion).
    string? DiscardChanges(string filePath);

    // Line-by-line comparison of a changed file with its last saved version.
    FileDiff GetFileDiff(string filePath);

    // Add the versions from a verified package received from another computer.
    // Files only change for a clean fast-forward; otherwise the received version is kept aside.
    ImportResult ImportVersionPackage(string packageFile, string headHash, string branch, string sourceName);

    // "Download a copy": create a new repository in an empty folder and open it.
    bool InitFromPackage(string folder, string packageFile, string headHash, string branch);
}

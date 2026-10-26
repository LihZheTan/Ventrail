using System;
using System.Collections.Generic;
using System.Linq;
using Ventrail.Models;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Mock;

/// <summary>
/// In-memory fake of IRepositoryManager filled with dummy data (CP2 Week 2: "dummy models").
///
/// Why it exists:
///  - The UI can be built, tested and demoed without touching a real folder or Git.
///  - It proves the MVVM separation: the ViewModel cannot tell this apart from the real
///    RepositoryManager because it only ever talks to the IRepositoryManager interface.
///
/// Run the app with the "--demo" argument (the "Ventrail (Demo data)" launch profile) to use it.
/// </summary>
public class MockRepositoryManager : IRepositoryManager
{
    private readonly List<FileAsset> _workingChanges;
    private readonly Dictionary<string, List<FileAsset>> _filesByCommit = new();

    public string RepoPath { get; private set; } = string.Empty;
    public List<CommitNode> CommitHistory { get; private set; } = new();
    public bool IsRepositoryOpen => !string.IsNullOrEmpty(RepoPath);

    public MockRepositoryManager()
    {
        // A believable non-technical scenario: a student's thesis folder.
        _workingChanges = new List<FileAsset>
        {
            File("Chapter3_Methodology.docx", "Modified", 482_000),
            File("figures/flowchart_v2.png", "Added", 1_250_000),
            File("data/survey_results.xlsx", "Modified", 96_500),
            File("notes/old_ideas.txt", "Deleted", 0),
            File("notes/todo.md", "Modified", 412),   // text file, so the Week 7 diff viewer has lines to show
            File("slides/scan_sample.svs", "Added", 2_400_000_000), // large specialised file for AI demo later
        };

        AddCommit("Add literature review draft", "Tan Lih Zhe", DateTime.Now.AddDays(-6),
            File("Chapter2_Literature.docx", "Added", 350_000));
        AddCommit("Insert survey data and first charts", "Tan Lih Zhe", DateTime.Now.AddDays(-3),
            File("data/survey_results.xlsx", "Added", 90_000),
            File("figures/chart_age.png", "Added", 210_000));
        AddCommit("Fix references and formatting", "Tan Lih Zhe", DateTime.Now.AddHours(-20),
            File("Chapter2_Literature.docx", "Modified", 356_000));
    }

    public bool InitRepository(string path)
    {
        RepoPath = string.IsNullOrWhiteSpace(path) ? @"C:\Demo\Thesis" : path;
        return true;
    }

    public List<FileAsset> ScanWorkingDirectory() => _workingChanges.Select(Clone).ToList();

    public void Stage(string filePath) => SetStaged(filePath, true);
    public void Unstage(string filePath) => SetStaged(filePath, false);
    public void StageAll() => _workingChanges.ForEach(f => f.IsStaged = true);

    public bool Commit(string message, string author)
    {
        // Demo-only test hook: a message starting with "!fail" simulates a disk error,
        // so the error state (red toast + activity log) can be shown in a presentation.
        if (message.TrimStart().StartsWith("!fail", StringComparison.OrdinalIgnoreCase))
            throw new System.IO.IOException("Simulated disk error (demo mode)");

        var staged = _workingChanges.Where(f => f.IsStaged).ToList();
        if (staged.Count == 0 || string.IsNullOrWhiteSpace(message)) return false;

        AddCommit(message.Trim(), string.IsNullOrWhiteSpace(author) ? "Demo User" : author, DateTime.Now,
            staged.Select(Clone).ToArray());
        _workingChanges.RemoveAll(f => f.IsStaged);
        return true;
    }

    public List<CommitNode> GetCommitHistory() => CommitHistory.ToList();
    public string GetCurrentBranchName() => "main";

    public List<FileAsset> GetFilesInCommit(string commitHash) =>
        _filesByCommit.TryGetValue(commitHash, out var files) ? files.Select(Clone).ToList() : new();

    public void RestoreFileFromCommit(string filePath, string commitHash)
    {
        if (_workingChanges.Any(f => f.FilePath == filePath)) return;
        var original = GetFilesInCommit(commitHash).FirstOrDefault(f => f.FilePath == filePath);
        if (original is null) return;
        original.TrackStatus = "Modified";
        original.IsStaged = true;
        _workingChanges.Add(original);
    }

    public string? DiscardChanges(string filePath)
    {
        var file = _workingChanges.FirstOrDefault(f => f.FilePath == filePath);
        if (file is null) return null;
        _workingChanges.Remove(file);
        return file.TrackStatus == "Deleted" ? null : "the Recycle Bin";
    }

    public FileDiff GetFileDiff(string filePath)
    {
        var diff = new FileDiff { FilePath = filePath };
        if (filePath == "notes/todo.md")
        {
            diff.Lines = new List<DiffLine>
            {
                new() { Kind = DiffLineKind.Hunk, Text = "Around line 3" },
                new() { Kind = DiffLineKind.Context, Text = "## Chapter 3", OldLineNumber = 3, NewLineNumber = 3 },
                new() { Kind = DiffLineKind.Removed, Text = "- [ ] Write methodology draft", OldLineNumber = 4 },
                new() { Kind = DiffLineKind.Added, Text = "- [x] Write methodology draft", NewLineNumber = 4 },
                new() { Kind = DiffLineKind.Added, Text = "- [ ] Ask Dr Munirah to review section 3.2", NewLineNumber = 5 },
                new() { Kind = DiffLineKind.Context, Text = "- [ ] Redraw flowchart (Fig 3.4)", OldLineNumber = 5, NewLineNumber = 6 },
                new() { Kind = DiffLineKind.Context, Text = "", OldLineNumber = 6, NewLineNumber = 7 },
            };
            diff.LinesAdded = 2;
            diff.LinesRemoved = 1;
        }
        else
        {
            diff.Message = "This kind of file (for example a Word document, spreadsheet or picture) can't be " +
                           "compared line by line. You can still restore or discard the whole file.";
        }
        return diff;
    }

    // ---------- helpers ----------

    private void AddCommit(string message, string author, DateTime when, params FileAsset[] files)
    {
        string hash = Guid.NewGuid().ToString("N") + "00000000"; // 40 hex chars, like a SHA-1
        var node = new CommitNode
        {
            Hash = hash,
            Message = message,
            Author = author,
            Timestamp = when,
            ParentHashes = CommitHistory.Count > 0 ? new List<string> { CommitHistory[0].Hash } : new()
        };
        CommitHistory.Insert(0, node); // newest first, same order as the real RepositoryManager
        _filesByCommit[hash] = files.ToList();
    }

    private void SetStaged(string filePath, bool staged)
    {
        var file = _workingChanges.FirstOrDefault(f => f.FilePath == filePath);
        if (file != null) file.IsStaged = staged;
    }

    private static FileAsset File(string path, string status, long size) => new()
    {
        FilePath = path,
        FileName = System.IO.Path.GetFileName(path),
        TrackStatus = status,
        SizeInBytes = size
    };

    // Hand out copies so the ViewModel can't change the "repository" without calling Stage/Unstage.
    private static FileAsset Clone(FileAsset f) => new()
    {
        FilePath = f.FilePath, FileName = f.FileName, TrackStatus = f.TrackStatus,
        SizeInBytes = f.SizeInBytes, IsStaged = f.IsStaged
    };
}

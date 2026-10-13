using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ventrail.Models;
using Ventrail.Services.Contracts;

namespace Ventrail.ViewModels;

/// <summary>
/// Central controller from the class diagram (Fig 3.8). It turns button clicks into
/// RepositoryManager calls and exposes data the three-pane window binds to.
///
/// All Git work runs inside Task.Run so the window never freezes while scanning
/// or committing large folders (section 3.3.1 of the proposal).
/// </summary>
public partial class MainDashboardViewModel : ObservableObject
{
    private readonly IRepositoryManager _repo;

    public MainDashboardViewModel(IRepositoryManager repositoryManager)
    {
        _repo = repositoryManager;
        AuthorName = Environment.UserName;
    }

    // ---------- state bound by the UI ----------

    public ObservableCollection<FileAsset> Changes { get; } = new();
    public ObservableCollection<CommitNode> History { get; } = new();
    public ObservableCollection<FileAsset> CommitFiles { get; } = new();   // files in the selected version

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(StageAllCommand), nameof(CommitCommand))]
    private string _repoPath = string.Empty;

    [ObservableProperty] private string _branchName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitMessage = string.Empty;

    [ObservableProperty] private string _authorName = string.Empty;
    [ObservableProperty] private string _statusText = "Select a folder to start tracking.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(StageAllCommand), nameof(CommitCommand))]
    private bool _isBusy;

    [ObservableProperty] private FileAsset? _selectedFile;
    [ObservableProperty] private CommitNode? _selectedCommit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedFileCommand))]
    private FileAsset? _selectedCommitFile;

    // Generated hook: runs whenever the user picks a version in the History tab.
    partial void OnSelectedCommitChanged(CommitNode? value)
    {
        CommitFiles.Clear();
        SelectedCommitFile = null;
        if (value is null) return;
        try
        {
            foreach (var f in _repo.GetFilesInCommit(value.Hash)) CommitFiles.Add(f);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not read that version: {ex.Message}";
        }
    }

    public int StagedCount => Changes.Count(c => c.IsStaged);
    public bool HasRepo => !string.IsNullOrEmpty(RepoPath);

    // ---------- commands ----------

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for Ventrail to track" };
        if (dialog.ShowDialog() != true) return;

        await RunBusyAsync("Opening folder...", async () =>
        {
            bool ok = await Task.Run(() => _repo.InitRepository(dialog.FolderName));
            if (!ok) { StatusText = "That folder could not be opened."; return; }

            RepoPath = _repo.RepoPath;
            OnPropertyChanged(nameof(HasRepo));
            await ReloadAsync();
            StatusText = History.Count == 0
                ? "New tracking started. Stage your files and save your first version."
                : $"Loaded {History.Count} saved versions.";
        });
    }

    private bool CanUseRepo() => HasRepo && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task RefreshAsync() => RunBusyAsync("Scanning for changes...", ReloadAsync);

    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task StageAllAsync() => RunBusyAsync("Staging all changes...", async () =>
    {
        await Task.Run(_repo.StageAll);
        await ReloadAsync();
    });

    // Bound to each file's checkbox: ticked = goes into the next version.
    [RelayCommand]
    private Task ToggleStageAsync(FileAsset? file) => file is null ? Task.CompletedTask :
        RunBusyAsync(file.IsStaged ? "Staging..." : "Unstaging...", async () =>
        {
            // The checkbox has already flipped IsStaged through two-way binding.
            if (file.IsStaged) await Task.Run(() => _repo.Stage(file.FilePath));
            else await Task.Run(() => _repo.Unstage(file.FilePath));
            await ReloadAsync();
        });

    private bool CanCommit() => CanUseRepo() && !string.IsNullOrWhiteSpace(CommitMessage);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task CommitAsync() => RunBusyAsync("Saving version...", async () =>
    {
        if (StagedCount == 0) { StatusText = "Tick at least one file before saving a version."; return; }

        string message = CommitMessage, author = AuthorName;
        bool ok = await Task.Run(() => _repo.Commit(message, author));
        if (!ok) { StatusText = "Nothing was saved: no staged changes."; return; }

        CommitMessage = string.Empty;
        await ReloadAsync();
        StatusText = $"Version saved ({History.FirstOrDefault()?.ShortHash}).";
    });

    private bool CanRestore() => SelectedCommit is not null && SelectedCommitFile is not null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreSelectedFileAsync() => RunBusyAsync("Restoring file...", async () =>
    {
        string path = SelectedCommitFile!.FilePath, hash = SelectedCommit!.Hash;
        await Task.Run(() => _repo.RestoreFileFromCommit(path, hash));
        await ReloadAsync();
        StatusText = $"Restored {path} from version {hash[..7]}. Save a version to keep it.";
    });

    // ---------- helpers ----------

    private async Task ReloadAsync()
    {
        var (files, history, branch) = await Task.Run(() =>
            (_repo.ScanWorkingDirectory(), _repo.GetCommitHistory(), _repo.GetCurrentBranchName()));

        Changes.Clear();
        foreach (var f in files) Changes.Add(f);

        History.Clear();
        foreach (var c in history) History.Add(c);

        BranchName = branch;
        OnPropertyChanged(nameof(StagedCount));
        if (!IsBusy) StatusText = $"{Changes.Count} changed file(s), {StagedCount} staged.";
    }

    // Wraps every operation: busy flag on, friendly error message instead of a crash.
    private async Task RunBusyAsync(string message, Func<Task> work)
    {
        IsBusy = true;
        StatusText = message;
        try
        {
            await work();
            if (StatusText == message)
                StatusText = $"{Changes.Count} changed file(s), {StagedCount} staged.";
        }
        catch (Exception ex)
        {
            StatusText = $"Something went wrong: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

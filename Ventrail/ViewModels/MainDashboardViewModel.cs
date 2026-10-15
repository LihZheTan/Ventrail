using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;

namespace Ventrail.ViewModels;

/// <summary>
/// Central controller from the class diagram (Fig 3.8). It turns UI actions into service calls
/// and exposes the data the window binds to. It never touches UI controls directly (MVVM).
///
/// Every dependency is an interface handed in through the constructor, so App.xaml.cs decides
/// whether the real services or the demo/mock ones are used. The ViewModel can't tell the difference.
///
/// Git work runs inside Task.Run so the window never freezes (proposal section 3.3.1).
/// </summary>
public partial class MainDashboardViewModel : ObservableObject
{
    // The four arrows leaving MainDashboardViewModel in Fig 3.8.
    private readonly IRepositoryManager _repo;             // "Manages Git Core"
    private readonly IIntelligentStorageEngine _ai;        // "Calls ML.NET"
    private readonly INetworkNodeCoordinator _network;     // "Manages P2P Network"
    private readonly ISpecializedFileHandler _fileHandler; // "Interfaces with external libraries"

    public MainDashboardViewModel(
        IRepositoryManager repositoryManager,
        IIntelligentStorageEngine storageEngine,
        INetworkNodeCoordinator networkCoordinator,
        ISpecializedFileHandler fileHandler,
        bool isDemoMode = false)
    {
        _repo = repositoryManager;
        _ai = storageEngine;
        _network = networkCoordinator;
        _fileHandler = fileHandler;
        IsDemoMode = isDemoMode;
        AuthorName = Environment.UserName;
    }

    // ---------- state bound by the UI ----------

    public bool IsDemoMode { get; }

    // Class diagram: List<FileAsset> StagedFiles -> all changes, each with an IsStaged flag.
    public ObservableCollection<FileAsset> Changes { get; } = new();
    public ObservableCollection<CommitNode> History { get; } = new();
    public ObservableCollection<FileAsset> CommitFiles { get; } = new();   // files in the selected version

    // Class diagram: List<PeerNode> ActivePeers (filled in Weeks 10-12).
    public ObservableCollection<PeerNode> ActivePeers { get; } = new();

    // Folder tree for the left pane of the Staging page.
    public ObservableCollection<FolderNode> WorkspaceTree { get; } = new();

    // Which page the navigation rail is showing (Week 3).
    [ObservableProperty] private AppPage _currentPage = AppPage.Welcome;

    // Class diagram: String CurrentDirectory.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepo), nameof(RepoName))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(StageAllCommand),
                                nameof(CommitCommand), nameof(ShareRepositoryCommand))]
    private string _currentDirectory = string.Empty;

    [ObservableProperty] private string _branchName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitMessage = string.Empty;

    [ObservableProperty] private string _authorName = string.Empty;
    [ObservableProperty] private string _statusText = "Select a folder to start tracking.";
    [ObservableProperty] private string _recommendationText = "Select a changed file to see suggestions.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(StageAllCommand),
                                nameof(CommitCommand), nameof(ShareRepositoryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private FileAsset? _selectedFile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCommit))]
    private CommitNode? _selectedCommit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedFileCommand))]
    private FileAsset? _selectedCommitFile;

    public int StagedCount => Changes.Count(c => c.IsStaged);
    public bool HasRepo => !string.IsNullOrEmpty(CurrentDirectory);
    public bool HasSelectedFile => SelectedFile is not null;
    public bool HasChanges => Changes.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasPeers => ActivePeers.Count > 0;
    public string LatestVersionHash => History.FirstOrDefault()?.ShortHash ?? "none yet";
    public bool HasSelectedCommit => SelectedCommit is not null;

    // Folder name only, e.g. "Thesis" (used in headers and the Network page).
    public string RepoName => string.IsNullOrEmpty(CurrentDirectory) ? string.Empty
        : Path.GetFileName(CurrentDirectory.TrimEnd('\\', '/'));

    // ---------- reactions to selection changes (generated "On...Changed" hooks) ----------

    partial void OnSelectedFileChanged(FileAsset? value)
    {
        if (value != null) CheckFileRecommendation(value.FilePath);
    }

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

    // ---------- class diagram operations ----------

    /// <summary>Class diagram: InitializeDashboard(). Opens/creates the repository and loads everything.</summary>
    public Task InitializeDashboardAsync(string folderPath) => RunBusyAsync("Opening folder...", async () =>
    {
        bool ok = await Task.Run(() => _repo.InitRepository(folderPath));
        if (!ok) { StatusText = "That folder could not be opened."; return; }

        CurrentDirectory = _repo.RepoPath;
        await ReloadAsync();
        await RefreshTreeAsync();
        CurrentPage = AppPage.Staging;
        StatusText = History.Count == 0
            ? "New tracking started. Stage your files and save your first version."
            : $"Loaded {History.Count} saved versions.";
    });

    /// <summary>Class diagram: CheckFileRecommendation(String filePath). Real AI arrives in Weeks 8-9.</summary>
    public void CheckFileRecommendation(string filePath)
    {
        var file = Changes.FirstOrDefault(f => f.FilePath == filePath);
        try
        {
            string type = _ai.AnalyzeFileFormat(filePath);
            RecommendationText = _ai.GenerateRecommendation(type, file?.SizeInBytes ?? 0);
        }
        catch (NotImplementedException)
        {
            RecommendationText = "AI suggestions will appear here once the ML.NET engine is added (Weeks 8-9).";
        }
    }

    // ---------- commands (buttons) ----------

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for Ventrail to track" };
        if (dialog.ShowDialog() != true) return;
        await InitializeDashboardAsync(dialog.FolderName);
    }

    private bool CanUseRepo() => HasRepo && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task RefreshAsync() => RunBusyAsync("Scanning for changes...", async () =>
    {
        await ReloadAsync();
        await RefreshTreeAsync();
    });

    // Page navigation from buttons inside pages (the rail uses radio buttons bound to CurrentPage).
    [RelayCommand]
    private void NavigateToStaging() => CurrentPage = AppPage.Staging;

    [RelayCommand]
    private void NavigateToHistory() => CurrentPage = AppPage.History;

    // Left rail "Exit" button.
    [RelayCommand]
    private void Exit() => System.Windows.Application.Current?.Shutdown();

    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task StageAllAsync() => RunBusyAsync("Staging all changes...", async () =>
    {
        await Task.Run(_repo.StageAll);
        await ReloadAsync();
    });

    // Class diagram: StageSelectedFile(String filePath). Bound to each file's checkbox.
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

    // Class diagram: CommitStagedFiles(String message).
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

    // Class diagram: ShareRepository(String peerId). Real P2P arrives in Weeks 10-12.
    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task ShareRepositoryAsync() => RunBusyAsync("Looking for peers...", async () =>
    {
        try
        {
            var peers = await Task.Run(_network.DiscoverPeers);
            ActivePeers.Clear();
            foreach (var p in peers) ActivePeers.Add(p);
            OnPropertyChanged(nameof(HasPeers));
            StatusText = $"Found {ActivePeers.Count} peer(s).";
        }
        catch (NotImplementedException)
        {
            StatusText = "Peer-to-peer sharing is coming in Weeks 10-12.";
        }
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
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(LatestVersionHash));
        if (!IsBusy) StatusText = $"{Changes.Count} changed file(s), {StagedCount} staged.";
    }

    // Rebuilt only on open/refresh (not after every stage) so the user's expanded folders stay open.
    private async Task RefreshTreeAsync()
    {
        string root = CurrentDirectory;
        var paths = Changes.Select(c => c.FilePath).ToList();
        var nodes = await Task.Run(() => Directory.Exists(root)
            ? FolderTreeBuilder.FromDirectory(root)
            : FolderTreeBuilder.FromPaths(paths));   // demo mode: folder isn't on disk

        WorkspaceTree.Clear();
        foreach (var n in nodes) WorkspaceTree.Add(n);
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

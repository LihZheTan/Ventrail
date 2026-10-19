using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
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
    private readonly IRecentRepositoryStore _recent;       // Welcome page "Recent Repositories" (Week 4)

    public MainDashboardViewModel(
        IRepositoryManager repositoryManager,
        IIntelligentStorageEngine storageEngine,
        INetworkNodeCoordinator networkCoordinator,
        ISpecializedFileHandler fileHandler,
        IRecentRepositoryStore recentStore,
        bool isDemoMode = false)
    {
        _repo = repositoryManager;
        _ai = storageEngine;
        _network = networkCoordinator;
        _fileHandler = fileHandler;
        _recent = recentStore;
        IsDemoMode = isDemoMode;
        AuthorName = Environment.UserName;
        LoadRecentRepositories();
    }

    // ---------- state bound by the UI ----------

    public bool IsDemoMode { get; }

    // Class diagram: List<FileAsset> StagedFiles -> all changes, each with an IsStaged flag.
    public ObservableCollection<FileAsset> Changes { get; } = new();
    public ObservableCollection<CommitNode> History { get; } = new();
    public ObservableCollection<FileAsset> CommitFiles { get; } = new();   // files in the selected version

    // Class diagram: List<PeerNode> ActivePeers (filled in Weeks 10-12).
    public ObservableCollection<PeerNode> ActivePeers { get; } = new();

    // Recently opened folders, newest first (Welcome page).
    public ObservableCollection<string> RecentRepositories { get; } = new();
    public bool HasRecentRepositories => RecentRepositories.Count > 0;

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
    public async Task InitializeDashboardAsync(string folderPath)
    {
        IsLoadingRepository = true;   // spinner overlay (Week 5)
        try
        {
            await RunBusyAsync("Opening folder...", async () =>
            {
                bool ok = await Background(() => _repo.InitRepository(folderPath));
                if (!ok)
                {
                    StatusText = "That folder could not be opened.";
                    ShowToast($"Could not open \"{folderPath}\". Check that the folder still exists.", ToastKind.Error);
                    return;
                }

                CurrentDirectory = _repo.RepoPath;
                await ReloadAsync();
                await RefreshTreeAsync();
                CurrentPage = AppPage.Staging;

                _recent.Add(CurrentDirectory);
                LoadRecentRepositories();

                bool isNew = History.Count == 0;
                LogActivity(isNew ? $"Started tracking {CurrentDirectory}" : $"Opened {CurrentDirectory}");
                StatusText = isNew
                    ? "New tracking started. Stage your files and save your first version."
                    : $"Loaded {History.Count} saved versions.";
                ShowToast(isNew ? $"Ventrail is now tracking {RepoName}" : $"Opened {RepoName}", ToastKind.Success, 3);
            });
        }
        finally
        {
            IsLoadingRepository = false;
        }
    }

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

    // Clicking a folder in "Recent Repositories" on the Welcome page.
    [RelayCommand]
    private Task OpenRecentAsync(string? folderPath) =>
        string.IsNullOrEmpty(folderPath) ? Task.CompletedTask : InitializeDashboardAsync(folderPath);

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

    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task StageAllAsync() => RunBusyAsync("Staging all changes...", async () =>
    {
        await Background(_repo.StageAll);
        await ReloadAsync();
        LogActivity($"{StagedCount} file(s) staged");
    });

    // Class diagram: StageSelectedFile(String filePath). Bound to each file's checkbox.
    [RelayCommand]
    private Task ToggleStageAsync(FileAsset? file) => file is null ? Task.CompletedTask :
        RunBusyAsync(file.IsStaged ? "Staging..." : "Unstaging...", async () =>
        {
            // The checkbox has already flipped IsStaged through two-way binding.
            if (file.IsStaged) await Background(() => _repo.Stage(file.FilePath));
            else await Background(() => _repo.Unstage(file.FilePath));
            await ReloadAsync();
        });

    private bool CanCommit() => CanUseRepo() && !string.IsNullOrWhiteSpace(CommitMessage);

    // Class diagram: CommitStagedFiles(String message).
    // Week 5: shows the "Committing Changes..." progress screen from the proposal mockup.
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task CommitAsync() => RunBusyAsync("Saving version...", async () =>
    {
        var staged = Changes.Where(c => c.IsStaged).ToList();
        if (staged.Count == 0)
        {
            ShowToast("Tick at least one file before saving a version.");
            return;
        }

        // Week 6: every version keeps a full copy of each file, so warn before large files go in.
        var large = staged.Where(f => f.IsLarge).ToList();
        if (large.Count > 0)
        {
            string names = string.Join("\n", large.Take(4).Select(f => $"  • {f.FileName} ({f.DisplaySize})"));
            if (large.Count > 4) names += $"\n  • ...and {large.Count - 4} more";
            string total = new FileAsset { SizeInBytes = large.Sum(f => f.SizeInBytes) }.DisplaySize;
            bool saveAnyway = await AskAsync(
                "Save large files?",
                $"{large.Count} of the ticked files {(large.Count == 1 ? "is" : "are")} large:\n{names}\n\n" +
                $"Each saved version keeps a full copy, so this folder's history will grow by about {total}. " +
                "You can untick them and save the rest instead.",
                "Save anyway", "Go back", "");
            if (!saveAnyway)
            {
                LogActivity($"Save cancelled: {large.Count} large file(s) ticked");
                return;
            }
        }

        string message = CommitMessage, author = AuthorName, root = CurrentDirectory;
        CommitProgress = 0;
        IsCommitting = true;
        try
        {
            // Fig 3.4: metadata scan of every staged file before the snapshot is written.
            // (Weeks 8-9 plug the ML.NET advisor into this loop.)
            for (int i = 0; i < staged.Count; i++)
            {
                var file = staged[i];
                CommitProgressText = $"Checking {file.FileName} ({i + 1} of {staged.Count})...";
                CommitProgress = 80.0 * i / staged.Count;
                await Background(() =>
                {
                    string full = Path.Combine(root, file.FilePath);
                    if (File.Exists(full)) _ = new FileInfo(full).Length;
                });
                if (staged.Count <= 20) await Task.Delay(60);   // small commits: let each step be seen
            }

            CommitProgress = 85;
            CommitProgressText = "Writing the new version to history...";
            bool ok = await Background(() => _repo.Commit(message, author));
            if (!ok)
            {
                ShowToast("Nothing was saved: there were no staged changes.");
                return;
            }

            CommitProgress = 100;
            CommitProgressText = "Done";
            CommitMessage = string.Empty;
            await ReloadAsync();

            string hash = History.FirstOrDefault()?.ShortHash ?? string.Empty;
            SessionCommits++;
            SessionFilesSaved += staged.Count;
            LogActivity($"Version {hash} saved with {staged.Count} file(s): \"{message}\"");
            StatusText = $"Version saved ({hash}).";
            await Task.Delay(350);
            ShowToast($"Version {hash} saved successfully", ToastKind.Success);
        }
        finally
        {
            IsCommitting = false;
        }
    });

    // Class diagram: ShareRepository(String peerId). Real P2P arrives in Weeks 10-12.
    // Week 5: the firewall permission prompt from Fig 3.5 (denied = abort, allowed = continue).
    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task ShareRepositoryAsync() => RunBusyAsync("Checking network permissions...", async () =>
    {
        bool allowed = await AskAsync(
            "Allow peer-to-peer sharing?",
            "To share versions directly with other computers, Windows Firewall must let Ventrail use " +
            "port 4001 (TCP) and port 4002 (UDP).\n\nNothing is uploaded to the cloud. Windows may ask you " +
            "to confirm, and you can switch sharing off again at any time.",
            "Allow", "Not now", "\uEA18");

        if (!allowed)
        {
            LogActivity("Sharing cancelled: firewall permission not given");
            ShowToast("Sharing cancelled. Ventrail needs firewall permission to share.");
            StatusText = "Sharing cancelled.";
            return;
        }

        try
        {
            await Background(_network.StartListening);
            var peers = await Background(_network.DiscoverPeers);
            ActivePeers.Clear();
            foreach (var p in peers) ActivePeers.Add(p);
            OnPropertyChanged(nameof(HasPeers));
            StatusText = $"Found {ActivePeers.Count} peer(s).";
            LogActivity($"Peer discovery found {ActivePeers.Count} peer(s)");
        }
        catch (NotImplementedException)
        {
            LogActivity("Share requested (peer-to-peer arrives in Weeks 10-12)");
            ShowToast("Firewall setup and peer discovery arrive in Weeks 10-12.");
            StatusText = "Peer-to-peer sharing is coming in Weeks 10-12.";
        }
    });

    private bool CanRestore() => SelectedCommit is not null && SelectedCommitFile is not null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreSelectedFileAsync() => RunBusyAsync("Restoring file...", async () =>
    {
        string path = SelectedCommitFile!.FilePath, hash = SelectedCommit!.Hash;

        if (!await AskAsync(
                "Restore this file?",
                $"\"{path}\" will be replaced with the copy from version {hash[..7]}.\n\n" +
                "Your history is not changed: the current version is still saved, and you can restore it the same way.",
                "Restore", "Cancel", "\uE777"))
            return;

        await Background(() => _repo.RestoreFileFromCommit(path, hash));
        await ReloadAsync();
        LogActivity($"Restored {path} from version {hash[..7]}");
        StatusText = $"Restored {path} from version {hash[..7]}. Save a version to keep it.";
        ShowToast($"Restored {System.IO.Path.GetFileName(path)}. Save a version to keep it.", ToastKind.Success);
    });

    // ---------- helpers ----------

    private async Task ReloadAsync()
    {
        var (files, history, branch) = await Background(() =>
            (_repo.ScanWorkingDirectory(), _repo.GetCommitHistory(), _repo.GetCurrentBranchName()));

        // Remember what was selected so a reload (e.g. after ticking a box) doesn't clear the inspector.
        string? selectedPath = SelectedFile?.FilePath;
        string? selectedHash = SelectedCommit?.Hash;
        string? selectedCommitFile = SelectedCommitFile?.FilePath;

        Changes.Clear();
        foreach (var f in files) Changes.Add(f);

        // Mark the ends of the timeline so the connecting line stops at the first and last version.
        for (int i = 0; i < history.Count; i++)
        {
            history[i].IsLatest = i == 0;
            history[i].IsOldest = i == history.Count - 1;
        }
        History.Clear();
        foreach (var c in history) History.Add(c);

        SelectedFile = Changes.FirstOrDefault(f => f.FilePath == selectedPath);
        SelectedCommit = History.FirstOrDefault(c => c.Hash == selectedHash);
        SelectedCommitFile = CommitFiles.FirstOrDefault(f => f.FilePath == selectedCommitFile);

        BranchName = branch;
        OnPropertyChanged(nameof(StagedCount));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(LatestVersionHash));
        if (!IsBusy) StatusText = $"{Changes.Count} changed file(s), {StagedCount} staged.";
    }

    private void LoadRecentRepositories()
    {
        RecentRepositories.Clear();
        foreach (var path in _recent.Load()) RecentRepositories.Add(path);
        OnPropertyChanged(nameof(HasRecentRepositories));
    }

    // Rebuilt only on open/refresh (not after every stage) so the user's expanded folders stay open.
    private async Task RefreshTreeAsync()
    {
        string root = CurrentDirectory;
        var paths = Changes.Select(c => c.FilePath).ToList();
        var nodes = await Background(() => Directory.Exists(root)
            ? FolderTreeBuilder.FromDirectory(root)
            : FolderTreeBuilder.FromPaths(paths));   // demo mode: folder isn't on disk

        WorkspaceTree.Clear();
        foreach (var n in nodes) WorkspaceTree.Add(n);
    }

    // Runs work on a background thread (like Task.Run) so the window never freezes, but catches any
    // error ON that thread and re-throws it here, where RunBusyAsync handles it. Same behaviour as
    // Task.Run, except Visual Studio's debugger no longer pauses with "Exception User-Unhandled" for
    // errors we already handle (placeholder services, disk errors, the demo "!fail" hook).
    private static async Task<T> Background<T>(Func<T> work)
    {
        var (result, error) = await Task.Run(() =>
        {
            try { return (work(), (ExceptionDispatchInfo?)null); }
            catch (Exception ex) { return (default(T)!, ExceptionDispatchInfo.Capture(ex)); }
        });
        error?.Throw();   // keeps the original stack trace
        return result;
    }

    private static Task Background(Action work) => Background(() => { work(); return true; });

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
            // Week 5: errors become a red toast + a log entry instead of a crash or silent failure.
            // Week 6: VentrailException messages are already plain language, so show them as-is.
            string text = ex is VentrailException ? ex.Message : $"Something went wrong: {ex.Message}";
            StatusText = text;
            ShowToast(text, ToastKind.Error);
            LogActivity($"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

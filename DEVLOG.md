# Ventrail Dev Log

## Week 1 – Architecture & Foundation
**Done**
- Translated the class diagram (Fig 3.8) into code:
  - Models: `CommitNode`, `FileAsset`, `PeerNode`
  - Service contracts: `IRepositoryManager`, `INetworkNodeCoordinator`, `IIntelligentStorageEngine`, `ISpecializedFileHandler`
  - Implementations: `RepositoryManager` (early Git core work), plus stubs for
    `NetworkNodeCoordinator`, `IntelligentStorageEngine`, `SpecializedFileHandler`
  - `MainDashboardViewModel` (MVVM controller)
- Folder structure follows MVVM: `Models/`, `ViewModels/`, `Views/`, `Services/Contracts`, `Services/Implementations`.
- Git repository at solution level, pushed to GitHub (`LihZheTan/Ventrail`).

**Decisions**
- **WPF instead of WinUI 3.** WPF is mature, has strong MVVM tooling (CommunityToolkit.Mvvm),
  needs no MSIX packaging and has more documentation, which lowers risk in a 14-week timeline.
  Proposal section 1.4 already allows "WinUI 3/WPF".
- **Interfaces for every service.** The ViewModel depends on interfaces, not concrete classes,
  so stubs can be swapped for real implementations week by week without touching the UI.
- **Stubs throw `NotImplementedException`** with the planned week in the message, so an
  accidental call explains itself.

**Changes from the proposal design (update the class diagram in the CP2 report)**
- `IRepositoryManager` gained: `IsRepositoryOpen`, `ScanWorkingDirectory()`, `StageAll()`, `Unstage()`,
  `GetCurrentBranchName()`, `GetFilesInCommit()`, `RestoreFileFromCommit()`.
- `CommitNode` gained `ParentHashes` (DAG parent pointers), `ShortHash`, `DisplayTime`.
- `FileAsset` gained `IsStaged`, `DisplaySize`.

**Issues / notes**
- None yet.

---

## Week 2 – MVVM & Data Binding
**Done**
- **Composition root** in `App.xaml.cs`: the only place concrete classes are created. Removed
  `StartupUri` from `App.xaml`; the app now builds the ViewModel, injects the four services
  through its constructor, and sets it as the window's `DataContext`.
- **Views folder**: `MainWindow` moved to `Views/` (namespace `Ventrail.Views`). The code-behind
  only calls `InitializeComponent()`; no logic lives in the View.
- **ViewModel aligned with Fig 3.8**: constructor takes `IRepositoryManager`,
  `IIntelligentStorageEngine`, `INetworkNodeCoordinator` and `ISpecializedFileHandler`
  (the four arrows in the diagram). Added `ActivePeers`, `CurrentDirectory`,
  `InitializeDashboardAsync`, `ShareRepositoryCommand`, `CheckFileRecommendation`.
  Calls to stubbed services fail gracefully with a friendly "coming in Week X" message.
- **Dummy models**: `Services/Mock/MockRepositoryManager.cs` is an in-memory fake repository
  (a student's thesis folder with 3 past versions and 5 changes).
- **Launch profiles** (`Properties/launchSettings.json`): "Ventrail" (real Git) and
  "Ventrail (Demo data)" (`--demo`, uses the mock and shows a DEMO DATA badge).
- **Data binding** in the View: folder, branch, changes list with staging checkboxes, history,
  files-in-version list, commit box, status bar with busy indicator, peers list, AI insights text.

**Testing**
- Headless test of the ViewModel against both the mock and the real `RepositoryManager`:
  open → tick file → commit (button disabled until a message is typed) → history updates →
  select version → files listed → restore → AI panel placeholder → share placeholder. All passed.

**Class diagram → code mapping (MainDashboardViewModel)**

| Fig 3.8 | Code |
|---|---|
| `String CurrentDirectory` | `CurrentDirectory` |
| `List<FileAsset> StagedFiles` | `Changes` (each `FileAsset` has `IsStaged`) |
| `List<PeerNode> ActivePeers` | `ActivePeers` |
| `InitializeDashboard()` | `InitializeDashboardAsync(path)` |
| `StageSelectedFile(filePath)` | `ToggleStageCommand` |
| `CommitStagedFiles(message)` | `CommitCommand` (uses `CommitMessage`) |
| `ShareRepository(peerId)` | `ShareRepositoryCommand` |
| `CheckFileRecommendation(filePath)` | `CheckFileRecommendation(filePath)` |

**Decisions**
- Manual constructor injection instead of a DI container (Microsoft.Extensions.DependencyInjection):
  only 4 services, so a container adds a package and complexity without real benefit.
- `ObservableCollection` + CommunityToolkit `[ObservableProperty]`/`[RelayCommand]` source
  generators for binding, which removes INotifyPropertyChanged boilerplate.

**Issues / notes**
- None.

---

## Week 3 – Core GUI Construction
_(to fill in)_

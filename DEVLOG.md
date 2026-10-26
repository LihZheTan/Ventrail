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
**Done**
- **Dark theme** in `Themes/Theme.xaml`, merged in `App.xaml`: colours, text styles, buttons
  (default / accent / outline / rail), text boxes, list rows, tree, tooltips, status colours.
  Matches the mockups in proposal section 3.7; every colour can be changed from this one file.
- **App shell** (`Views/MainWindow.xaml`): left navigation rail (Home, Changes, History, P2P,
  Open folder; Profile, Settings, Exit at the bottom), page area, status bar with busy indicator.
  Navigation uses radio buttons bound to `CurrentPage` (`ViewModels/AppPage.cs`) through
  `EnumToBoolConverter`, so the ViewModel stays in control of which page is shown.
- **Pages** (`Views/Pages/`), all sharing the main ViewModel:
  - `WelcomeView`: logo, Open / Create repository, "Continue with ..." when a folder is open,
    Recent Repositories placeholder.
  - `StagingView`: the 3-pane dashboard (section 3.3.2). Left = workspace tree + branch,
    centre = staging list with colour-coded status and commit box, right = File Inspector + AI card.
  - `HistoryView`: version list + version details with files and Restore.
  - `NetworkView`: firewall, share and peers layout (static until Weeks 10-12).
- **Workspace tree**: `FolderTreeBuilder` (depth 3, max 400 entries, skips `.git`), rebuilt only on
  open/refresh so expanded folders stay open while staging.
- Empty states for no changes, no history, no selection and no peers.

**Decisions**
- Pages are `UserControl`s inside one window, switched by `CurrentPage`, instead of separate windows:
  simpler navigation and one shared state.
- Icons use the built-in Windows icon font (Segoe Fluent Icons / Segoe MDL2 Assets): no image files needed.
- Commit progress screen and Session Summary are left for Week 5 (loading and error states).

**Issues / notes**
- Week 2 commit only contained the file moves (they had been staged by `git mv`); fixed with a
  follow-up commit containing the rest of Week 2.

---

## Week 4 – Interactive UI Components
**Done**
- **Checkboxes**: custom dark rounded checkbox that fills blue with a white tick (`Theme.xaml`).
- **Status indicators**: round badge per changed file (+ added, pencil modified, - deleted,
  warning for conflicts) plus a file-type icon (`FileAsset.TypeGlyph`: picture, video, audio,
  code, archive, document).
- **Visual commit history**: History page is now a timeline. A vertical line joins versions
  (the Git DAG), the newest node glows and has a "Latest" tag, merges are purple with a
  "Merge" tag, times are relative ("3 days ago", exact time on hover).
  `CommitNode` gained `RelativeTime`, `IsMerge`, `IsLatest`, `IsOldest`.
- **Responsive layout** (`WidthBelowConverter` + DataTriggers on the page width):
  Staging hides the workspace tree below 940px and the inspector below 760px;
  History narrows the details pane below 860px; Network stacks into one column below 820px.
  Window minimum size lowered to 720x520.
- **Slim dark scrollbars** used everywhere.
- **Recent Repositories** on the Welcome page: `IRecentRepositoryStore`, saved locally to
  `%AppData%\Ventrail\recent-repositories.json` (max 6, hides deleted folders, never crashes on a
  damaged file); demo mode uses an in-memory list. One click reopens a folder.
- **Keyboard shortcuts**: F5 refresh, Ctrl+O open folder, Ctrl+Enter commit from the message box.
- **Bug fix**: selected file / version / file-in-version are kept after a reload, so ticking a
  checkbox no longer clears the File Inspector.

**Testing**
- Headless ViewModel tests: timeline flags, type icons, selection kept after staging and refresh,
  recent-folder store round trip, damaged JSON file.
- Built and run in Visual Studio (demo mode): checked every page at full width and at ~800px,
  ticked files, committed with Ctrl+Enter, checked the new version at the top of the timeline.
- Timeline line colour made brighter after the first run (was too faint on the dark background).

**Decisions**
- Responsiveness done with DataTriggers bound to the page width instead of code-behind
  SizeChanged handlers, keeping the Views free of logic.
- Recent folders stored as plain JSON in the user's AppData: local only, no registry, no cloud.

**Issues / notes**
- At very narrow widths the File Inspector (and its AI card) is hidden; acceptable because the
  minimum width keeps the main staging list usable.

---

## Week 5 – UI States (errors, firewall prompts, loading)
**Done**
- **Loading state**: spinner overlay ("Opening folder and scanning files...") while a folder is
  opened and scanned (`Views/Controls/Spinner.xaml`, `IsLoadingRepository`).
- **Commit progress screen** (proposal mockup, Fig 3.4): full-screen "Committing Changes..." with a
  glowing progress bar and percentage. Progress is real: each staged file's metadata is checked
  ("Checking X (2 of 5)..."), then "Writing the new version to history...". The ML.NET advisor will
  plug into this loop in Weeks 8-9.
- **Toast notifications**: bottom-right, green success / red error / blue info, auto-hide
  (errors stay longer), dismiss button, fade-in.
- **Error state**: every failure inside `RunBusyAsync` now shows a red toast, updates the status bar
  and is written to the activity log instead of failing silently.
- **Reusable confirm dialog** (`AskAsync`, Esc = cancel), used for:
  - the **firewall permission prompt** before sharing (Fig 3.5: "Not now" aborts, "Allow" continues
    to firewall setup / peer discovery, which arrive in Weeks 10-12);
  - confirming **Restore** of a file;
  - **exiting with ticked files that are not saved yet**.
- **Session Summary page** (Fig 3.7 + mockup): versions saved, files saved, P2P transfers and data
  shared (last two become real in Week 12), session activity log, Continue Workflow / Exit Application.
  The rail's Exit button now opens this page first.
- Feedback code lives in `MainDashboardViewModel.Feedback.cs` (partial class) to keep the main
  ViewModel focused on version control.

**Testing**
- Headless ViewModel test: loading flag during open, commit progress steps for 5 files, success toast,
  firewall dialog denied and allowed, restore confirm, simulated failure (red toast, overlay closes),
  exit confirm, Continue Workflow, activity log contents.
- Demo-only test hook: in demo mode, a commit message starting with `!fail` simulates a disk error so
  the error state can be shown in a presentation.
- Built in Visual Studio: first build failed (`x:Int32` is not available in this XAML namespace in the
  Summary page); replaced with a `HasActivity` property; rebuild succeeded.

- Click-through test in Visual Studio (demo mode): commit progress screen (seen at 48%, "Checking
  old_ideas.txt (4 of 5)"), success toast, firewall dialog (Not now / Allow), restore dialog and
  toast, Session Summary with activity log, exit warning with a ticked file, `!fail` error toast,
  checkbox toggled with Tab + Space.
- **Bug found and fixed:** when running under the debugger, errors thrown inside `Task.Run`
  (placeholder services, disk errors) made Visual Studio pause with "Exception User-Unhandled",
  even though the app handles them. Added a `Background(...)` helper that catches the error on the
  background thread and re-throws it on the UI thread where `RunBusyAsync` handles it. Same
  behaviour for users; no more false pauses during demos. Re-tested Allow and `!fail`: no pause.

**Decisions**
- In-app dialogs and toasts instead of Windows MessageBox: they match the dark theme, can't hide behind
  the window, and keep the ViewModel testable (dialogs are awaited through `AskAsync`).

**Issues / notes**
- Week 5 completes the clickable GUI prototype; Weeks 6-7 move on to the Git core.

---

## Week 6 – Git Core Integration (scanning, initialisation, cataloguing)

**What was done**
- The basic Git operations (init/open, scan, stage, commit, history, restore) already worked from
  earlier weeks, so Week 6 focused on making them safe for real folders and real users.
- **Default `.gitignore` for new repositories** (Fig 3.2). It skips Windows/macOS system files
  (`Thumbs.db`, `desktop.ini`, `.DS_Store`), Office and LibreOffice lock/temp files (`~$*`, `*.tmp`,
  `.~lock.*#`) and build/tool folders (`bin/`, `obj/`, `.vs/`, `node_modules/`, `__pycache__/`).
  If the folder already has a `.gitignore`, Ventrail leaves it alone.
- **Folders that are too broad are refused.** Ventrail won't create a repository in a whole drive
  (`C:\`), the Windows or Program Files folders, AppData, or the user's whole profile folder. These
  would mean scanning, and saving into history, hundreds of thousands of files. The user is told to
  pick a project folder inside it instead. Existing repositories still open as before.
- **Plain-language errors.** Every `RepositoryManager` method now runs through a `Use(...)` helper
  that opens the repository and turns low-level errors into a `VentrailException` with a
  plain-language message, which the UI shows as-is (no "Something went wrong:" prefix):
  - folder moved, deleted or on an unplugged drive;
  - `.git/index.lock` left by another Git tool or a crashed save;
  - Windows permission denied;
  - file open/locked in another program (Word, Excel...);
  - bare repositories (no working files).
- **Git ownership check turned off** (`GlobalSettings.SetOwnerValidation(false)`). Without this,
  folders on USB/exFAT drives or copied from another PC fail with a confusing "not owned by current
  user" error. Ventrail never runs hooks or external Git commands, so the attack this check guards
  against doesn't apply.
- **Large files** (cataloguing, and a first step on the proposal's storage-bloat problem):
  - `FileAsset.IsLarge`: 50 MB or more;
  - an amber "Large" badge in the staging list;
  - a "Save large files?" confirm dialog before saving, which lists the files and the total size
    the history will grow by ("Save anyway" / "Go back").
  - The fixed 50 MB rule is a placeholder for the ML.NET heavy/light classifier in Weeks 8-9.
- **Scan is more robust:** reading a file's size can no longer fail the whole scan (locked or
  just-deleted files report 0 B).

**Testing**
- Headless test console app (outside the repo) that drives the real `RepositoryManager` against
  temporary folders on Windows. 23/23 checks passed:
  - init writes `.gitignore`, and `~$` / `Thumbs.db` files are not listed;
  - an existing `.gitignore` is kept;
  - commit and history;
  - opening a subfolder opens the parent repository, with no nested `.git`;
  - `C:\` and the user profile folder are refused with a friendly message;
  - a missing folder returns false;
  - a 60 MB file is flagged as large;
  - a file locked by another process: the scan still works, and Stage and Restore give friendly
    messages;
  - `index.lock` gives a friendly message;
  - a sub-folder with access denied (via `icacls`) is skipped without an error;
  - a repository folder deleted while open gives a friendly message;
  - a normal restore works for a CRLF text file and a binary file.
- Demo build run: the "Large" badge shows on `scan_sample.svs` (2.24 GB).
- **Bug found and fixed:** restoring a file that another program has open did nothing and reported
  no error, so the app showed "Restored" when the file had not changed. `RestoreFileFromCommit` now
  compares the file on disk with the stored version (after line-ending filters) and reports a
  plain-language error if they differ.

**Decisions**
- Large files get a warning, not a block: the user decides. Chunking and Git LFS are still out of
  scope.
- Unreadable sub-folders are skipped silently (libgit2 behaviour) rather than stopping the scan.
  The user can add them to `.gitignore`.

**Issues / notes**
- Class diagram update for the report: `VentrailException` is new, and `FileAsset` gained
  `IsLarge` / `LargeFileBytes`.
- Git does not track empty folders. This is normal Git behaviour, worth a line in the user guide.

---

## Week 7 – Staging to Git index, hashing, DAG commit

**What was done**
- The Week 7 core (ticking files writes them to the Git index; saving hashes the staged tree and
  writes a commit node with its parent links into the DAG) was already working and was hardened in
  Week 6. This week adds the two everyday actions a non-technical user needs around it.
- **Discard changes** (File Inspector, red "Discard" button). It takes one file back to the last
  saved version:
  - modified file: the saved content comes back, and the staging tick is cleared;
  - new file that was never saved: removed from the folder and from staging;
  - deleted file: comes back;
  - renamed file: the old name comes back and the new one is removed.
- **Safety:**
  - a confirm dialog explains what will happen for that kind of change;
  - **the current copy is moved to the Windows Recycle Bin first** (`SafeDelete`, Shell
    `SHFileOperation` with `FOF_ALLOWUNDO`), so a mis-click never loses work for good;
  - USB and network drives have no Recycle Bin (Windows would delete for good), so there the file
    is copied to `%LocalAppData%\Ventrail\Discarded\<date-time>\` instead, and the toast says where;
  - a file that is open in another program is refused before anything is touched.
- **"View changes" diff viewer.** Opened with the "View changes" button or by double-clicking a
  file; Esc closes it. It is a full-window overlay comparing the last saved version with the file on
  disk:
  - green added lines, red removed lines, old and new line numbers;
  - "Around line N" section headers with 3 lines of context;
  - a summary such as "2 line(s) added, 1 line(s) removed";
  - new files show every line as added, and deleted files every line as removed.
  - Built on LibGit2Sharp `Diff.Compare<Patch>(HEAD tree, WorkingDirectory)`. The unified-diff text
    is parsed into `DiffLine`s (`Models/FileDiff.cs`), and the list is virtualised so long files
    stay fast.
  - Files that can't be compared line by line show a plain-language message instead: binary files
    (detected the same way as Git: a zero byte in the first 8000 bytes), files over 2 MB, and output
    is capped at 3000 lines.
- `IRepositoryManager` gained `DiscardChanges(path)` (returns where the old copy went) and
  `GetFileDiff(path)`. `MockRepositoryManager` implements both, and the demo data now has a text file
  (`notes/todo.md`) so the diff viewer has something to show.

**Testing**
- Headless test console app (outside the repo) driving the real `RepositoryManager` on temporary
  folders: **24/24 checks passed**:
  - diff of a modified file: exact added/removed lines and line numbers;
  - a staged change is still compared with the last saved version;
  - new and deleted files;
  - binary and >2 MB files give messages;
  - discard of a modified+staged file: content back and no longer listed;
  - discard of new, deleted and binary files;
  - discard of a staged file in a repository with no versions yet;
  - discard of a staged rename (`git mv`): back to the old name and nothing left changed;
  - discard of a file locked by another process: friendly error and the edits are kept;
  - discard of an unchanged file: friendly message.
- **Test finding (not a bug):** Git for Windows sets `core.autocrlf=true` system-wide, so restored
  text files get Windows line endings (`\r\n`). Content checks compare with line endings ignored,
  which matches what users see in Notepad/Word.
- App builds with 0 warnings and 0 errors. A demo click-through of the diff overlay is still to do
  in Visual Studio.

**Decisions**
- **No DAG graph drawing for branches.** Ventrail only works on one branch (the proposal notes that
  branching intimidates non-technical users), so history is always a straight line. The existing
  timeline already marks merge commits (from other Git tools) with a "Merge" tag. A full graph is
  left out unless branch features are added later.
- Discard uses the Recycle Bin instead of a hidden backup inside `.git`: users already know how to
  get files back from the Recycle Bin.
- The diff compares with the *last saved version*, not the staging area, because "what did I change
  since I last saved?" is the question users ask. The staging area is a Git detail they don't see.
- Word/Excel/PDF files show "can't be compared line by line". Comparing document contents is left
  for the optional specialised-file handler.

**Issues / notes**
- Class diagram update for the report: `FileDiff`/`DiffLine` models, `SafeDelete` helper, and
  `IRepositoryManager.DiscardChanges` / `GetFileDiff`.
- The File Inspector (and so the two new buttons) is hidden when the window is narrower than 760px.
  Double-click still opens the diff viewer there; Discard needs the wider window.

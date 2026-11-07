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

---

## Week 8 – ML.NET setup: classifying heavy vs light files

**What was done**
- **Defined "heavy" so it can be measured, not guessed.** A file is *heavy* if saving one new
  version adds **at least 1 MB to the history after Git's zlib compression**. This is the storage
  bloat problem from the proposal: a 4 MB photo is heavy (already compressed, so every version is a
  new full copy), but a 3 MB text log is light (it compresses to a fraction of that).
- **Features read cheaply from the file** (`Services/AI/FileFeatures.cs`, shared by the app and the
  trainer). Only the **size and the first 4 KB** are read, so even multi-GB files classify in
  milliseconds:
  - log10 of the size;
  - Shannon entropy of the header (about 8 bits/byte = already compressed or encrypted);
  - the share of zero bytes and of text-like bytes;
  - the **format from the magic bytes** (zip/docx, PNG, JPEG, PDF, TIFF/BigTIFF (.svs), PSD,
    MP4, EXE...), because the extension can lie but the header can't;
  - the file family from the extension (document, image, video, code...).
- **Offline trainer** (`Ventrail.ModelTrainer`, a new console project in the solution):
  - Collects ~6,000 real files from **non-personal locations only**: Windows wallpapers and media,
    fonts, logs, Program Files, and the NuGet cache. It caps each extension at 250 files so DLLs
    don't dominate. The user's own folders are included only with `--include`.
  - Adds 300 synthetic large text files (logs, CSV exports, plain text, 100 KB–40 MB). These are
    common for students but rare in system folders.
  - **Labels each file by actually compressing it** with zlib, the same compression Git uses. Files
    over 16 MB are labelled from 8 evenly spread 1 MB samples, to keep training fast.
  - Trains an **ML.NET FastTree** binary classifier on the cheap features: the model *predicts* the
    expensive measurement without compressing the file.
  - Evaluates on a held-out 20%, runs 5-fold cross-validation, compares with size-only rules, and
    writes `data/metrics.md`.
  - The dataset is saved as `data/training-data.csv` with **no file names or paths**.
  - Saves the model to `Ventrail/MLModels/ventrail_classifier.zip` (63 KB), which is copied next to
    the exe.
- **`IntelligentStorageEngine` is now real** (was a stub):
  - loads the model once, then `ClassifyFile()` returns Heavy/Light, the probability, a friendly
    format name ("JPEG picture", "Word document") and whether the format is already compressed;
  - `AnalyzeFileFormat()` works from the header bytes;
  - `GenerateRecommendation()` stays for Week 9;
  - runs fully offline: no file content leaves the PC;
  - thread-safe (`PredictionEngine` is used under a lock);
  - a missing or corrupt model never crashes the app: the advisor switches off and the Week 6 50 MB
    rule takes over;
  - for files that aren't on disk (demo data, locked files) it estimates from the extension and
    size, and says so in the UI.
- **In the app:**
  - the staging list shows an amber **"Heavy"** badge (tooltip = the advisor's explanation),
    instead of the Week 6 "Large" badge;
  - the File Inspector's AI card shows the verdict, e.g. *"Heavy file (99% sure). This JPEG picture
    is already compressed, so every saved version stores a full new copy in the history (about
    4.0 MB each time)."*;
  - the commit progress screen shows "Checking photo.jpg (2 of 5): heavy file...".

**Results** (held-out test set, 1,270 files; full report in `Ventrail.ModelTrainer/data/metrics.md`)

| Rule | Accuracy | Precision (heavy) | Recall (heavy) | F1 (heavy) |
|---|---|---|---|---|
| Size >= 50 MB (Week 6 "Large" badge) | 96.22% | 1.000 | 0.059 | 0.111 |
| Size >= 1 MB | 97.24% | 0.593 | 1.000 | 0.745 |
| **ML.NET FastTree** | **99.92%** | **1.000** | **0.980** | **0.990** |

- AUC 1.000; 5-fold cross-validation accuracy 99.63% (±0.22%).
- Only 4% of files are heavy, so **accuracy is misleading**: the 50 MB rule scores 96% while finding
  only 6% of heavy files. F1 shows the real gap.
- The model beats the size rule because it knows *what kind* of content it is looking at: big text
  is light, compressed media just over 1 MB is heavy.

**Testing**
- Engine test console app (outside the repo), 22/22 checks passed:
  - 4 MB JPEG → heavy; 150 KB JPEG → light; 30 MB MP4 → heavy; 400 KB Word file → light;
  - 3 MB text log → light; 40 MB text log → heavy; 2 KB note → light;
  - demo estimates (2.4 GB .svs → heavy; 482 KB .docx → light);
  - thread-safety under 8 parallel callers;
  - missing and corrupt model files → advisor off, no crash;
  - end to end with the ViewModel and demo data: badges fill in, the inspector text is right,
    deleted files get their own message, and badges don't flicker on reload.
- **Performance problem found and fixed:**
  - Predicting is about 1 ms per file, but the *first* time a file is opened Windows' antivirus
    scans it: about 30–70 ms per file, so 500 new files took ~30 s. The first version classified
    inside the scan, which would have frozen opening a big folder.
  - Fixes:
    1. Classification now runs **after** the scan, in batches of 25 on a background thread. Files
       appear at once and badges fill in a moment later.
    2. The engine **caches verdicts by path, size and last-modified time**, so the rescans after
       every checkbox tick don't re-open files: 500 unchanged files in ~0.35 s instead of ~33 s.
    3. Verdicts carry over across reloads, so badges don't flicker.
    4. Loading ML.NET and the model takes about 4–8 s the first time, so the app now starts loading
       it in the background at startup.
- App and trainer build with 0 warnings and 0 errors.

**Decisions**
- FastTree (gradient-boosted trees) rather than logistic regression: "heavy" depends on
  size *combined with* content type (big text is fine, big JPEG isn't), which trees capture
  naturally.
- Labels come from a real measurement (zlib size), not hand-labelling or a rule. That avoids a
  circular "ML learns my own rule", and gives the model a meaning a user can check.
- Training data is taken from system folders, not the developer's personal files: it is
  reproducible, raises no privacy issue, and is still varied (291 extensions).
- The Week 6 large-file *dialog* (≥ 50 MB) stays. The Heavy badge is information; a pop-up for
  every heavy photo would be annoying. Week 9 turns the verdict into advice.
- Confidence is shown capped at 99%: tree models output probabilities near 0/1, and "100% sure"
  would overstate it to users.

**Issues / notes**
- Class diagram update for the report: `FileFeatures`, `FileFeatureExtractor`, `HeavyPrediction`
  and `FileClassification`; `IIntelligentStorageEngine.IsModelLoaded` / `ClassifyFile`;
  `FileAsset.Classification` (FileAsset is now observable).
- Dataset limitation: system folders have few personal document types (photos from phones, theses,
  datasets). Running the trainer with `--include` on a folder of typical student files would make
  it more representative. The model's near-perfect score partly reflects how separable size plus
  content type is.
- New dependency: Microsoft.ML / Microsoft.ML.FastTree 4.0.2 (about 4-5 MB of DLLs in the app output).

## Week 9 – AI suggestions in the UI (commit messages, storage and chunking advice)

**What was done**
- **AI suggestion card above the commit box** (proposal mockup):
  - It appears as soon as files are ticked and shows:
    - a **suggested commit message**;
    - a one-line summary (e.g. *"2 heavy files in this version. Leaving 1 file out would keep about
      2.24 GB out of the history."*);
    - one line of advice per heavy file. A very large file gets a red warning icon.
  - **Accept AI Suggestions** fills in the message and unticks the files the advisor suggests
    leaving out (heavy and ≥ 50 MB). It never unticks the last ticked file, and the message is
    rewritten for the files that are still ticked.
  - **Edit Manually** uses the suggestion as a starting draft only if the box is empty (it never
    overwrites what the user typed). It moves the cursor into the box and leaves the ticked files
    alone.
  - Either button hides the card until the set of ticked files changes.
- **Commit message writer** (`Services/AI/SuggestionWriter.cs`), rule-based and offline:
  - It groups files by change: *Update* (modified), *Add*, *Remove* (deleted), *Rename*.
  - It names files where that reads well (*"Update Chapter3.docx"*, *"Update a.docx and b.xlsx"*).
    It counts them when they're the same kind (*"Add 3 pictures"*), and adds the folder when all
    files share one (*"... in figures"*).
  - It tries three levels of detail and keeps the first that fits in **72 characters** (Git's
    first-line convention). Example for the demo: *"Update 3 files, add 2 pictures and remove
    old_ideas.txt"*.
- **Storage and chunking tip in the File Inspector**, under the Week 8 verdict:
  - heavy files: save at milestones rather than after every small edit;
  - very large files: ask whether the file needs a history at all;
  - **chunking**: *"When you share this folder, it is sent in 2,289 blocks of 1 MB, each checked
    against its fingerprint (hash)..."*. The block size (`SuggestionWriter.ShareBlockBytes`, 1 MB)
    is the constant Week 12's block streaming will use;
  - light files: a short note that saving often costs almost nothing.
- **`GenerateRecommendation(fileType, fileSize)` is implemented** (it was `NotImplementedException`).
  It estimates from the extension and size with the Week 8 model, and accepts `"report.docx"`,
  `".docx"` or `"docx"`. New contract methods:
  - `GetStorageTip(FileAsset)`, which never loads the model, so it is safe on the UI thread;
  - `SuggestCommit(stagedFiles)`.
- The suggestion is rebuilt after every scan and whenever new verdicts arrive. The advice depends
  on them, and they fill in a moment after the scan.

**Testing**
- Headless test console app (outside the repo), 39/39 checks passed:
  - commit messages: single file per verb, same kind counted plus folder, mixed files named,
    "biggest + N other files", demo set, 40 files still ≤ 72 characters, an over-long file name
    falling back to counts;
  - advice: only heavy files (or large ones with no verdict), none for deleted files, warnings
    first, leave-out list, summary text;
  - block count rounding (0 B, 1 B, exactly 1 MB, 1 MB + 1 → 1, 1, 1, 2);
  - `GenerateRecommendation` with and without a model file;
  - end to end with the ViewModel and demo data:
    - no card while nothing is ticked;
    - the card appears after Stage All;
    - Edit Manually fills an empty box but keeps a typed message;
    - the card comes back when the ticked files change;
    - Accept unticks the 2.4 GB slide and writes a message without it;
    - the version is saved with the AI message;
    - Accept never unticks the only ticked file.
- Visual check in the demo app: the inspector tip shows correctly. The card's buttons weren't
  clicked through, because Windows focus switching interrupted the automated clicks.
- Builds with 0 warnings and 0 errors.

**Decisions**
- **Templates rather than a language model** for commit messages:
  - everything stays offline and small (no multi-GB model);
  - the same files always give the same message, which is easy to test and explain;
  - the message stays honest: it only describes what Ventrail actually knows (file names, change
    type, kind of file).
  - The ML part is the Week 8 classifier that drives the advice.
- "Accept" applies the advice (unticking very large files) because that is what accepting a
  suggestion means. It never deletes, ignores or moves files: unticked files stay in the folder and
  can be ticked again. The Week 6 large-file dialog still guards the save.
- The card hides after a choice instead of nagging, and comes back only when the ticked files
  change.

**Issues / notes**
- Class diagram update for the report:
  - `CommitSuggestion` and `StorageAdvice`;
  - `SuggestionWriter`;
  - `IIntelligentStorageEngine.GetStorageTip` and `SuggestCommit`;
  - ViewModel `Suggestion`, `AcceptSuggestionCommand`, `EditSuggestionManuallyCommand` and
    `RecommendationTip`.
- Renamed files only know their new path, so the message says "Rename new_name.docx", not
  "old → new".
- "Edit Manually" uses a few lines of code-behind (`StagingView.xaml.cs`) to move keyboard focus,
  which is a view-only concern; all logic stays in the ViewModel.

## Week 10 – Firewall audit and permission prompt

**What was done**
- **Firewall audit** (`Services/Implementations/FirewallAuditor.cs`), read-only, no admin rights:
  - It reads the Windows Firewall settings through Windows' own COM API (`HNetCfg.FwPolicy2`), which
    works in any Windows language (unlike parsing `netsh` output).
  - For each port (**TCP 4001** for transfers, **UDP 4002** for discovery) it works out what Windows
    would do with an incoming connection. **Block rules win**, then allow rules, then the network's
    default. It also handles "block all incoming connections" and a firewall that is switched off.
  - It only counts rules that apply to the current network type (Private / Public / Domain), are
    switched on, are inbound, and match the protocol, the port (single, list, range or any) and the
    program (Ventrail's exe or any program).
  - It ignores rules that Windows only applies to something else: service rules (e.g. Remote
    Desktop) and Store-app rules (e.g. "Game Bar"; see Issues).
  - Each port gets **Allowed / Needs permission / Blocked**, with a plain-language reason as a
    tooltip, e.g. *Blocked by the firewall rule "Ventrail.exe"*.
- **Permission prompt** (proposal Fig 3.5: denied → abort, allowed → continue):
  1. Turning on sharing (the switch or "Share via P2P") audits the firewall first.
  2. If a port isn't allowed, Ventrail's own dialog explains why, in plain words.
  3. **Allow** opens the normal **Windows admin prompt (UAC)**, which adds two rules with `netsh`:
     - `Ventrail P2P (TCP 4001)` and `Ventrail P2P (UDP 4002)`;
     - limited to Ventrail's exe and its two ports;
     - **only devices on the same local network** (`remoteip=localsubnet`).
  4. The audit runs again to confirm. Only then are the ports opened.
  5. "Not now" in Ventrail's dialog, or "No" on the Windows prompt, aborts cleanly: the switch goes
     back off, a toast explains why, and the Activity Log records it.
- **Public networks:**
  - Rules normally cover Private and Domain (work) networks only.
  - If the PC is on a **Public** network (café or campus Wi-Fi), the dialog says so, explains that
    devices on the same network could connect, and points to Settings to mark a trusted network as
    Private. Allowing then includes the Public profile.
- **Blocked by an old rule:** if someone once pressed "Cancel" on Windows' own "allow access?"
  pop-up, Windows created a block rule for Ventrail.exe. Ventrail recognises it as its own, says so
  in the dialog, and replaces it. **Other programs' rules are never touched.**
- **"Remove firewall permission"** link: asks first, stops sharing, and deletes only Ventrail's two
  rules (again through the admin prompt).
- **Local node listening** (`NetworkNodeCoordinator`):
  - **TCP 4001** accepts connections and answers `VENTRAIL/1 <peer id>`. Week 12 replaces this with
    block streaming.
  - **UDP 4002** answers the discovery question `VENTRAIL?` with
    `VENTRAIL! <peer id> <tcp port>` and ignores any other packet. Week 11's discovery broadcasts
    that question.
  - Start/stop is idempotent and thread-safe.
  - "Port already used by another program (maybe Ventrail is open twice)" is a friendly error, and a
    failed start never leaves one port half-open.
  - A short **peer id** (`vt-` + 12 hex digits) is created once and kept in
    `%LocalAppData%\Ventrail\peer-id.txt`, so peers recognise this computer between runs.
- **Network page is live:**
  - the Online/Offline pill turns green while sharing (tooltip shows the peer id);
  - the firewall card shows a summary sentence, coloured pills per port, and a "check again"
    button;
  - the "Enable P2P Sharing" switch works;
  - the firewall is checked automatically the first time the page opens.
- **Demo mode** uses `MockNetworkNodeCoordinator`. It starts at "Needs permission" and "Allow"
  succeeds without an admin prompt, so the flow can be shown without changing the PC's firewall or
  opening ports.
- The share logic moved into `MainDashboardViewModel.Network.cs` (partial class, like `.Feedback.cs`).

**Testing**
- Headless test console app (outside the repo), 54 checks:
  - port matching (single, list, range, any, keywords, near-misses);
  - **rule evaluation with made-up rules**:
    - no rules; our allow rule; block beats allow;
    - Windows' own block rule for Ventrail recognised, someone else's block rule not ours;
    - other program; any program; Store-app rule; service rule; "any program, any port" rule;
    - other network type; disabled and outbound rules; UDP vs TCP; any protocol;
    - firewall off; block-all; network default allow; Public detection; `%SystemRoot%` paths;
  - the exact `netsh` scripts (local subnet only, private/domain vs +public, old rules replaced
    first, delete-by-program only when asked, remove only deletes Ventrail's rules);
  - **real audit on this PC (read-only):** Public network, both ports "Needs permission";
  - **real sockets on loopback** with spare ports (so Windows never shows a firewall pop-up):
    - the TCP hello and the UDP discovery answer (other packets ignored);
    - port-in-use error; stop frees the ports; nothing listens after stop;
    - the peer id survives a restart;
  - **ViewModel flow (Fig 3.5)** with the demo node:
    - the page audits on first open;
    - asks before changing anything;
    - "Not now" aborts and is logged;
    - "Allow" leads to online;
    - switch off/on, with no second prompt;
    - remove permission;
    - a fake node that says "No" at the admin prompt: abort, the switch goes back off, and the
      Public warning is shown.
- I did **not** run the real admin prompt or change this PC's firewall during testing (that's the
  user's decision); the exact commands it runs are tested instead.

**Decisions**
- Rules are tied to Ventrail's exe, its two ports and the **local subnet**, not "allow everything":
  P2P sharing in this project is between computers on the same network.
- The admin prompt is unavoidable for firewall changes, and that is a good thing: Windows shows the
  user exactly what is being changed. Ventrail never tries to work around it.
- Ventrail's own explanation comes **before** the Windows prompt, because a bare UAC prompt is
  confusing for non-technical users.
- The audit errs on the cautious side. A rule that *might* only apply to another app is ignored, so
  the worst case is asking for permission that wasn't strictly needed, never claiming access that
  isn't there.

**Issues / notes**
- **Found while testing on this PC:** the first audit said both ports were "Allowed by Game Bar".
  - Store apps (Game Bar, Teams, Microsoft Store) have firewall rules with *no program and any
    port*, which looked like "allow everything". Windows actually ties them to the app package.
  - Through COM the package id comes back empty, but these rules have an owning user
    (`LocalUserOwner`), so they are now ignored, along with service rules and "any program, any
    port" allow rules.
  - Reading `LocalUserOwner` through `dynamic` threw an exception on every rule (an 18 s audit). A
    direct `IDispatch` call fixed it.
- **Audit speed** depends on Windows. Reading the ~690 rules took 0.2 s on an idle PC and 3–4 s while
  Visual Studio was installing extensions; plain PowerShell took the same time, so the delay is in
  the firewall service, not Ventrail. The audit always runs in the background with "Checking..." on
  screen, and the owner lookup is limited to the few rules that need it.
- The rule is tied to the exe path. A Debug build (`bin\Debug`) and a later installed copy are
  different programs to Windows, so each needs its own permission.
- If Week 11 needs discovery beyond the local network (internet DHT), the `remoteip=localsubnet`
  limit has to be widened. That is a deliberate change, made in one place (`BuildAllowScript`).
- Class diagram update for the report:
  - `FirewallStatus` and `PortCheck`;
  - `FirewallAuditor`;
  - `INetworkNodeCoordinator.CheckFirewall`, `RequestFirewallPermission`,
    `RemoveFirewallPermission`, `IsListening`, `StopListening` and `DiscoveryPort`;
  - `MockNetworkNodeCoordinator`.

## Week 11 — Peer discovery (LAN broadcast + Kademlia DHT)

**Goal:** find other computers running Ventrail, show them on the Peer-to-Peer page, and know which
of them already have a copy of the open repository (proposal: libp2p/IPFS and DHT peer discovery).

**What was built**
- **Two-step discovery, the same way libp2p works on a local network:**
  1. **LAN broadcast** on UDP 4002 ("is any Ventrail here?"), like libp2p's mDNS. Sent to
     255.255.255.255 *and* each network's own broadcast address, because Windows only sends the
     first one out of one adapter.
  2. **Kademlia DHT lookup**: Ventrail asks the peers it found "who do you know near this id?", then
     asks the closer peers they name, until nobody closer turns up. Peers that missed the broadcast
     (e.g. Wi-Fi that drops broadcasts) are still found through someone else.
- **Routing table** (`KademliaRoutingTable`): 48 k-buckets (K = 8) by XOR distance between ids.
  Peer ids (`vt-` + 12 hex) and repository keys (`rp-` + 12 hex) share the same 48-bit space.
  A full bucket keeps its long-lived peers and only replaces one that's been silent for 15 min.
  Peers that stop answering are dropped during lookups.
- **Provider records** (IPFS "content routing"): "Share via P2P" announces the repository to the
  K peers closest to its key; any peer can then ask "who has this repository?".
  - The key is a SHA-256 of the repository's **first version**, so every copy of the same
    repository gets the same key, and the key reveals nothing about folder or file names.
  - Records expire after 30 min and are re-announced on each search.
- **Messages** (`DiscoveryMessage`): one line of plain text per UDP packet, each starting with the
  sender's id, ports and computer name, so the receiver learns the sender (as Kademlia does).
  `VENTRAIL?`/`!`, `-FIND`/`-PEERS`, `-PROVIDE`, `-GETPROV`/`-PROVIDERS`. Week 10's bare
  `VENTRAIL?` still works.
- **Connect** (`EstablishConnection`): opens TCP 4001 and checks the hello line carries the peer id
  discovery promised. A different computer now using that address is noticed and dropped.
  Week 12 will keep this connection open and stream blocks over it.
- **Add by address**: type an IP or computer name when a computer doesn't show up.
- **Network page:**
  - "Find peers" button and a live summary line;
  - each peer shows its computer name, IP and peer id, a green **"Has a copy"** badge, and a
    working Connect button;
  - the list also updates on its own when another computer finds this one (`PeersChanged` event).
  - Switching sharing on searches automatically; switching it off empties the list.
- **Demo mode:** finds two made-up computers (LAB-PC-02 has a copy); Connect and Add work.

**Testing** (headless console app, `%LOCALAPPDATA%\Temp\vw11`, **67/67 pass**)
- Keys and XOR distance, bucket index, repository key.
- Routing table rules, checked against a brute-force sort of 300 random ids.
- Provider expiry and cap, plus message/entry parsing including malformed, oversized and
  non-UTF-8 packets.
- Three real nodes on loopback where A's "broadcast" only reaches B:
  - **A still finds C through the DHT**;
  - B learns C passively, and discovery took about 1.5 s;
  - C finds that A has the repository;
  - connect works, an impostor at A's old address is caught, and a peer that went offline is
    dropped.
- A **lying fake peer** that lists an internet address (8.8.8.8) and a dead local one: neither ends
  up in the table.
- The ViewModel flow with the demo node.
- Week 10's 54 checks still pass. Two expectations were updated on purpose: the discovery answer
  now has extra fields after the old ones, and Share now really finds peers.
- Visual check in demo mode: Allow, then peers listed with the "Has a copy" badge, then Connect
  shows "Connected".
  - Some test clicks went to the Claude app window sitting on top of Ventrail (invisible in
    screenshots). That also explains the Week 9 click problems; it's not a Ventrail bug.

**Decisions**
- **Own small Kademlia instead of a libp2p library.** The .NET libp2p ports were still previews,
  and joining the public IPFS DHT would announce this computer on the internet. That contradicts
  the "same network only" firewall rule and the privacy goal. The design follows Kademlia
  (k-buckets, XOR distance, iterative lookup, alpha = 3, provider records), so the report can
  explain it in the same terms.
- **Everything stays on the local network:**
  - packets from, and peer addresses pointing to, anything outside this PC's subnets are ignored
    (the same rule as the firewall's `localsubnet`);
  - peers learnt second-hand only enter the table after they answer directly.
- Announcing a repository only happens when the user clicks **Share via P2P**, never automatically.
- Timeouts are fixed for now (broadcast 1.5 s, query 0.8 s × 2 tries, connect 3 s). Week 13 makes
  them adaptive.

**Issues / notes**
- Windows reports "port unreachable" from an earlier UDP send as an error on the *next* receive,
  which would have ended the listening loop. Turned off with `SIO_UDP_CONNRESET`.
- Campus/café Wi-Fi often blocks devices from talking to each other ("client isolation"). Then
  neither broadcast nor DHT can help; a phone hotspot or home router works for the demo.
- Class diagram update for the report:
  - `KademliaRoutingTable`, `ProviderStore`, `DhtKey` and `DiscoveryMessage`;
  - `PeerNode` gains DiscoveryPort, DisplayName, LastSeen and HasCurrentRepository;
  - `INetworkNodeCoordinator` gains PeersChanged, DisplayName, GetKnownPeers, AddPeerByAddress,
    ProvideRepository and FindProviders.

## Week 12 — Sending versions: sockets, block streaming, hash verification

**Goal:** actually move versions between computers over TCP, in 1 MB blocks, checking every block
by its fingerprint (hash), so a receiving computer can either update its copy ("Get latest") or
get a brand-new copy ("Download a copy").

**What was built**
- **Version package** (`VersionPackage`): one file holding a Git **pack** (every version, compressed
  the way Git stores it) plus its index, made with LibGit2Sharp's `PackBuilder`.
  - The file is named after its SHA-256, so the same versions always give the same package.
  - The sender builds it once per latest version and reuses it until the repository changes.
- **TCP protocol** on port 4001, after Week 10's hello line:
  - `LIST` returns the repositories this computer shares, with the latest version of each;
  - `MANIFEST <repo key>` returns the package's size, its SHA-256 and the SHA-256 of every 1 MB block;
  - `BLOCK <hash> <index>` returns exactly one block;
  - `ERROR …` for anything wrong, `BYE` to finish.
  - Text lines and raw bytes share one buffered stream; the receiver checks every field before using it.
- **Three layers of checking** on the receiving side:
  1. **each block** is checked against its SHA-256 as it arrives; a damaged block is asked for
     again (up to 3 tries, reconnecting if the connection broke);
  2. **the whole package** is checked against its SHA-256 at the end;
  3. **Git checks every version** while importing (objects are named by their own hash), so a
     package that matched its fingerprint but holds broken data is still refused.
- **Resume:** finished blocks stay on disk (`%LocalAppData%\Ventrail\inbox`). After a failure or
  Cancel, trying again only fetches the missing blocks.
- **Importing safely** (`RepositoryManager.ImportVersionPackage`): your files only change when
  that is completely safe:
  - **Updated:** the other version simply continues yours (a fast-forward) **and** you have no
    unsaved changes;
  - **Waiting for save:** you have unsaved changes, so the version is kept aside and your files
    are untouched;
  - **Diverged:** you both saved different versions, so theirs is kept aside as
    `refs/remotes/<computer>/<branch>`. Combining both is future work.
  - **Already up to date / already newer:** nothing changes.
- **Download a copy** (`InitFromPackage`): you pick a folder, it gets a new subfolder named after
  the repository, `Thesis (2)` if that name is taken, and the copy opens straight away.
- **Network page:**
  - each peer lists what it shares, with a **Get latest** button (same repository) or a
    **Download a copy** button;
  - a progress card shows "N of M blocks checked", blocks kept from last time and damaged blocks
    sent again, with a **Cancel** button;
  - the Summary page's "shared this session" now counts real bytes.
- **Demo mode:** LAB-PC-02 has a newer version of the open repo (one block "arrives damaged" and
  is re-sent), and both demo PCs offer "Group-Report" to download.

**Testing** (headless console app, `%LOCALAPPDATA%\Temp\vw12`, **48/48 pass**)
- A real repository with a 3 MB random file:
  - the package spans 4 blocks and is byte-identical after the trip;
  - all 3 versions arrive, and the copy gets the same repository key.
- Malformed and truncated packages are refused. A package with damaged pack data is refused by
  **Git's check**.
- Get latest:
  - with unsaved changes, the files are left alone;
  - after the edit is undone, it fast-forwards;
  - then up to date, already newer, and diverged (nothing overwritten).
- Damaged blocks:
  - a block damaged twice is re-sent and the download succeeds;
  - a block that's always damaged stops the download with a clear message;
  - trying again **resumes** (only the missing blocks are fetched), and Cancel also resumes.
- When the sender saves a new version, a new package is made, the old one is deleted from the
  sender's outbox, and requests for its blocks are refused.
- Nonsense requests get `ERROR` and the connection closes; the sender keeps serving others.
- The ViewModel flow with the demo services: Get latest, a second click says "already have",
  folder dialog cancelled, the copy opens in `Group-Report (2)`, and Cancel works.
- Weeks 10 (54/54) and 11 (67/67) still pass. Two Week 11 expectations changed on purpose
  (Connect status text; the same history in another folder now counts as the same repository).
- Visual check in demo mode: peer cards with Get latest / Download a copy, then the progress card
  mid-transfer, then "Your files now match LAB-PC-02's latest version".

**Decisions**
- **Pull, never push:** versions only move when the receiving user clicks. Nobody can drop files
  onto someone else's computer.
- **A whole pack each time**, not just the new versions. That's simpler and always complete; a
  smaller "only what's new" pack is possible later (Git can exclude what the receiver already has).
- **Never overwrite:** unsaved work or different saved versions on the receiving side are never
  replaced; the received version is kept aside instead.
- 1 MB blocks match the Week 9 storage advice ("sent in N blocks of 1 MB").
- Timeouts are still fixed (connect 3 s, block 30 s, manifest 3 min while the sender packs).
  Week 13 makes them adaptive.

**Issues / notes**
- libgit2 writes pack files **read-only**, so cleaning up the build folder failed at first;
  the attributes are now cleared first.
- With `core.autocrlf=true` (set on this PC), text files are checked out with Windows line
  endings. That's expected, and only the tests had to allow for it.
- The peer list now refreshes after the history loads. Before, opening a different folder could
  briefly show "Get latest" for the wrong repository.
- Clicks on the right half of the screen during visual tests went to the Claude app window, so
  buttons there were pressed through UI Automation instead (same cause as Week 11).
- Class diagram update for the report:
  - `VersionPackage`, `BlockHashing`, `TransferManifest`, `TransferProgress`, `OfferedRepository`,
    `ImportResult`;
  - `INetworkNodeCoordinator` gains ListRepositories, RequestVersion, DownloadPackage,
    TransferData and BytesSent;
  - `IRepositoryManager` gains ImportVersionPackage and InitFromPackage.

## Week 13 — End-to-end testing and adaptive timeouts

**Goal:**
- test the whole flow the way two users would actually use it, not just the parts one by one;
- replace the fixed waiting times from Weeks 11–12 with ones that learn from the real network.

**Adaptive timeouts** (`RttEstimator`, `ThroughputEstimator` and `PeerLink`, one per peer)
- **Round-trip time** is tracked the way TCP itself does it (Jacobson/Karels, RFC 6298):
  - Ventrail keeps a smoothed round-trip time (SRTT) and how much it varies (RTTVAR), and waits
    SRTT + 4 × RTTVAR;
  - a missed answer doubles the next wait (back-off), and a real answer brings it back down;
  - limits are 0.25–8 s. It starts at 0.8 s, the old fixed value.
  - Every answer counts as a sample: broadcast replies, DHT answers and TCP connect + hello.
- **Speed:** a smoothed bytes-per-second figure per peer sets how long one 1 MB block may take
  (3 × the expected time + the round-trip wait, between 2 and 120 s). It starts at 30 s, the old
  fixed value.
  - When a block times out, the speed estimate drops **at once** to half of what that timeout
    proves is possible (like TCP's "multiplicative decrease"), instead of averaging down over
    many timeouts.
- **Where the waits apply:**
  - DHT questions use that peer's round-trip wait; the second try waits twice as long.
  - The broadcast waits at least 1.5 s, longer if a slow peer has been seen.
  - Connecting waits 2 × round trip + 1 s; each answer line waits 2 s + 4 × round trip.
- **"WAIT" keep-alive:** a sender busy packing a big repository says `WAIT` every 2 s. A busy
  sender gets all the time it needs, while a frozen one is noticed within seconds.
- **Three strikes:** a peer only leaves the routing table after 3 lookups in a row get no answer.
  Before, one miss was enough, so a link that briefly got slower lost its peers.
- **On screen:** each peer shows its round-trip time ("· 4 ms"), and the progress card shows speed
  and time left ("3.4 MB/s · about 1 s left").

**End-to-end tests** (`%LOCALAPPDATA%\Temp\vw13`, **42/42 pass**)
- **Set-up:** two real nodes on loopback, each driven through its **own real ViewModel**, real
  RepositoryManager and real Git repository, exactly as the buttons would.
  - Only the firewall check is replaced, so tests never trigger the Windows admin prompt.
- **A day in the life:**
  1. Alice saves a version, switches sharing on and shares it.
  2. Bob finds her, sees "Download a copy", and gets an identical copy (a 1.6 MB binary,
     byte-for-byte), which opens straight away.
  3. Alice saves again, and Bob's button now says "Get latest"; his files update.
  4. With unsaved work, Bob's files are left alone. After both have saved different versions,
     the versions are kept apart.
  5. Bob shares back, and Alice sees his copy as the same repository.
- **Slow network:** 1 s delay on every answer and 400 KB/s.
  - Bob still finds Alice. The old fixed 0.8 s wait would have missed every answer.
  - The download completes, with speed and time left shown.
  - Speed learnt earlier on the fast link no longer causes a timeout on every block: 1 retry
    instead of 4 (this was found by the test and fixed, see Issues).
- **Busy sender:** packing takes 8 s, longer than any single wait, and the receiver keeps
  waiting thanks to `WAIT`.
- **Frozen peer mid-download:** noticed in about 12 s (Week 12 would take 90 s+). Once the peer
  is back, the download resumes from block 3.
- **Peer health:**
  - a peer whose link got slower is kept, and the waits grow until its answers get through again
    (Carol then learns Bob from Alice through the DHT);
  - a peer that's silent for 3 lookups is dropped.
- **Sharing switched off on the other side:** a clear message within 2 s, also in the activity
  log.
- **Unit checks** of the estimators: fast and slow links, jitter, back-off and limits.
- **Every earlier suite still passes:** Week 7 (24), 8 (22), 9 (39), 10 (54), 11 (67), 12 (48).
  One Week 11 expectation changed on purpose: a dead peer is now dropped after 3 lookups, not 1.
- **Visual check in demo mode:** ping on the peer cards, and the progress card with speed and
  time left.

**Issues found by the end-to-end tests**
- **Remembered speed on a changed link:** after a fast download, a slow link timed out once on
  *every* block, because the averaged speed came down too slowly. Fixed with the immediate drop
  on timeout.
- **Inflated reconnect wait:** block timeouts were also doubling the round-trip wait, which made
  noticing a frozen peer slower (17.5 s). Block timeouts now only lower the speed estimate.
- **Test clean-up:** temp folders were left behind, because Git's pack files are read-only.
  Fixed in the test clean-up. Some older `vw12-*` / `vw13-*` folders are still in Temp (about
  50 MB) and are safe to delete.

**Decisions**
- Follow TCP's own proven method instead of inventing one. It's easy to justify in the report,
  and the unit tests show its known behaviour (steady vs. unsteady links, back-off, recovery).
- The test hooks (`TestConditions`, `TestBlockFilter`) live in the real coordinator but do
  nothing unless a test sets them. The slow, frozen and busy cases run through the real code
  paths, not a copy.
- Class diagram update for the report: `RttEstimator`, `ThroughputEstimator`, `PeerLink`,
  `NetworkConditions`; `PeerNode.RoundTripMs`; `TransferProgress.BytesPerSecond` / `TimeLeft`.

## Week 14 — Clean-up, documentation and packaging

**Goal:** turn the working app into a finished release: tidy the code and UI, write the
documentation, move the tests into the repository, and package Ventrail so a non-technical user
can run it without installing anything else.

**Clean-up**
- The Welcome page showed "v0.5 · CP2 Week 5". It now reads the real version
  ("Version 1.0.0", plus "· demo data" in demo mode).
- Release information in `Ventrail.csproj`: version 1.0.0, product, author, company, description.
  It shows in the exe's Properties.
- **App icon:** a glowing blue "V" matching the Welcome-page logo, generated as a multi-size `.ico`
  (16–256 px), used for the exe, window and taskbar.
- The Summary page's "Transfers" counter was never increased; it now counts downloads from peers.
- Out-of-date code comments (Week 10/11 "will be replaced in Week 12" notes) were updated.
- The build has **0 warnings, 0 errors**.

**UX bugs found while writing the user guide** (both fixed)
- **Peer-to-Peer page unreachable without a folder open.** Its rail button was disabled until a
  repository was open, so someone who doesn't have the repository yet could never reach
  "Download a copy". The Week 13 end-to-end test drove the ViewModel directly, so it couldn't
  catch this.
  - The page is now always available.
  - The Welcome page has a "Get a repository from another computer on your network" link.
- **"Add by address" needs the other PC's IP, but it wasn't shown anywhere.** The Network page now
  shows "This computer: LAB-PC-02 (192.168.1.24)".

**Tests moved into the repository** (`tests/`)
- The 7 headless suites (Weeks 7–13) and the Week 8 classifier speed benchmark came out of
  `%TEMP%` into `tests/Ventrail.Tests.*`, and appear under a `tests` folder in the solution.
- Hard-coded paths to this PC were replaced by `TestPaths`, which finds the repository from
  wherever the tests run.
- `tests\run-all.ps1` builds and runs every suite, prints a summary table, and exits non-zero on
  any failure. `-Only 12,13` runs some weeks; `-Benchmark` adds the speed check.
- Result: **all 296 checks pass** (Week 7: 24, 8: 22, 9: 39, 10: 54, 11: 67, 12: 48, 13: 42).
- The Week 12/13 suites now clean up their temp folders. Git's read-only pack files had blocked
  that before.

**Packaging** (`build\package.ps1`)
1. Runs all test suites; any failure stops the release.
2. `dotnet publish` **self-contained** for win-x64, so users don't need to install .NET.
3. Checks that the exe, the ML.NET model and the native Git library are in the package, and adds
   the user guide.
4. Zips it: **`artifacts\Ventrail-1.0.0-win-x64.zip`, 64.5 MB** (153 MB unzipped).
5. If Inno Setup 6 is installed, also builds `Ventrail-1.0.0-Setup.exe` from
   `installer\Ventrail.iss`. Inno Setup isn't installed on this PC, so only the zip was made.
   - The installer installs per user (no admin needed), with Start menu entries for the app,
     demo mode and the user guide.
   - On uninstall it removes Ventrail's two firewall rules (Windows asks first), and never
     touches repositories.
- **Checked:** the zip was unzipped into a fresh folder and started.
  - Demo mode works, with the icon, version and Heavy badges (so the ML.NET model loads from the
    package).
  - Real mode works: the native Git library loaded, a folder was initialised with the default
    `.gitignore`, and its changes were scanned.

**Documentation**
- `README.md`: what Ventrail is, features, screenshots, how to run, test and package it, and the
  repository layout.
- `docs/USER_GUIDE.md`: a step-by-step guide for non-technical users, from starting the app to
  saving versions, the AI advisor, history, sharing and troubleshooting. It ships inside the
  package.
- `docs/ARCHITECTURE.md`: layers, Git core, the AI model and its features, the P2P design
  (firewall, DHT, transfer protocol, adaptive timeouts), testing, and known limits.
- `tests/README.md`: what each suite covers and how to run it.
- 5 screenshots in `docs/images/`, captured from the demo by a script, so only Ventrail's window
  is in them.

**Known limits** (also in ARCHITECTURE.md)
- Merging two different saved versions isn't supported; both are kept safely.
- Each transfer sends the whole history.
- Sharing works on the local network only, on purpose.
- Transfers are hash-checked but not encrypted on the wire.
- The optional specialised-file previews weren't built.

**Notes**
- `Ventrail/_to_delete/` and `Ventrail/_backup_before_git_core.zip` are old local backups (about
  136 KB). They're git-ignored and were left in place; deleting them is up to the author.

## After Week 14 — Redesign: GitHub Desktop-style layout, light and dark themes

**Goal (user request):** make Ventrail look and work like GitHub Desktop's menus and layout, with
slightly different fonts, and offer light and dark themes.

**What changed**
- **Shell** (`MainWindow`):
  - a **menu bar** (File / View / Repository / Help, every item with its shortcut);
  - a **toolbar** with *Current repository ▾* (a drop-down of recent repositories, plus "Add or
    create…" and "From network…"), *Current branch*, *Scan for changes* (with "Last scanned at…"),
    *Share on network* (Online/Offline and a peer-count badge) and a sun/moon theme switch;
  - a slim status bar showing the version. The old icon rail is gone; the menus and toolbar
    replace it.
- **`RepositoryView`** replaces StagingView and HistoryView:
  - a resizable left sidebar with **Changes | History** tabs;
  - Changes has tick boxes, rows showing the folder in grey before the file name, a square status
    mark (+ • −), the AI suggestion (with a "Why?" toggle for the advice) and a **Summary +
    Description** commit box with a "Commit to *branch*" button;
  - the right pane shows the **diff of the selected file inline** (GitHub Desktop's main feature),
    under a header with Expand and Discard and the AI verdict;
  - when nothing has changed, a "No local changes" screen with suggestions;
  - History keeps the timeline (Week 4 DAG) and shows the version's description, files and Restore.
- **Welcome page:** "Let's get started!", with three start options and the recent repositories.
- **Themes:**
  - `Colors.Dark.xaml` / `Colors.Light.xaml` hold the same colour keys, and every view uses them
    through `DynamicResource`;
  - `ThemeManager` swaps them at runtime: Light / Dark / Match Windows, which keeps following
    Windows. It also matches the window's title bar and remembers the choice in
    `%AppData%\Ventrail\settings.json`.
  - In both themes the toolbar stays dark, as in GitHub Desktop.
- **Fonts:** Segoe UI Variable Text for text, Variable Display for headings and Cascadia Mono for
  hashes, paths and diffs. Windows 10 falls back to Segoe UI / Consolas.
- **ViewModel** (`MainDashboardViewModel.Shell.cs`, still UI-free):
  - `CurrentTheme` / `SetTheme`, `CommitDescription` (joined to the summary with a blank line, as
    Git expects), `CommitButtonText`, `LastScanText`;
  - the inline diff, with only the latest selection's diff kept;
  - Show in Explorer, User guide (F1), About.
- `CommitNode.Description` loads the rest of a commit message, so a description written in
  Ventrail is shown in History.

**Testing**
- New suite `tests/Ventrail.Tests.Redesign` (**22 checks**):
  - light and dark define the same colour keys;
  - every `{DynamicResource}` the views use exists in both themes;
  - no colour is used as a `StaticResource` (it wouldn't change with the theme);
  - summary + description become one Git message, and the description shows in History;
  - the inline diff follows the latest selection;
  - the theme, navigation and About commands work.
- **All 318 checks pass** (8 suites).
- Visual check of every screen in both themes: Changes with inline diff, History, the repository
  drop-down, the View → Theme menu, Network mid-transfer, the permission dialog, Welcome and the
  Session Summary.

**Issues found and fixed**
- The Changes panel didn't fill the sidebar's width: it was docked left inside a DockPanel instead
  of filling it.
- Menus opened to the left of their header on this PC, because of Windows' "handedness" setting
  (`MenuDropAlignment`). Ventrail now opens them left-aligned like GitHub Desktop. WPF caches the
  setting on first read, so it must be read once before it's overridden.
- The AI suggestion card squeezed the file list to three rows; its advice now sits behind "Why?".
- A Week 11 test assumed the PC's first network adapter is a normal LAN. With a VPN connected
  (a one-address subnet), that assumption failed; the test now picks a real LAN adapter.

**Notes**
- The redesign keeps every feature and binding, so the proposal's figures map onto the new screens:
  - Staging area → Changes tab;
  - File Inspector → the right pane;
  - Version history → History tab.
- The old views stay in git history (Week 14 commit) for the report.

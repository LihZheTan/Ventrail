# Ventrail architecture

This is the technical overview for developers and assessors. The week-by-week reasoning, the
problems found and the decisions made are in [DEVLOG.md](../DEVLOG.md).

## 1. Layers (MVVM)

```
Views (XAML)  ──binding──▶  MainDashboardViewModel  ──interfaces──▶  Services
 Pages, dialogs, toasts        (partial: core, .Feedback,           IRepositoryManager        → RepositoryManager (LibGit2Sharp)
                                .Network)                           IIntelligentStorageEngine → IntelligentStorageEngine (ML.NET)
                                                                    INetworkNodeCoordinator   → NetworkNodeCoordinator (sockets + DHT)
                                                                    ISpecializedFileHandler   → SpecializedFileHandler (optional, not built)
                                                                    IRecentRepositoryStore    → JsonRecentRepositoryStore
```

- **App.xaml.cs** is the only place where concrete classes are chosen. `--demo` swaps in
  `MockRepositoryManager`, `MockNetworkNodeCoordinator` and `InMemoryRecentRepositoryStore`, so
  the UI can be shown without touching real files, the firewall or the network.
- The ViewModel uses CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`). All slow
  work runs on background threads; errors become plain-language `VentrailException`s, shown as
  toasts and written to the activity log.
- **Views, redesigned in a GitHub Desktop style:**
  - `MainWindow` holds the menu bar, the toolbar (repository drop-down, branch, scan, share, theme
    switch), the pages and the overlays.
  - `RepositoryView` holds the Changes/History sidebar and the detail pane (inline diff, version
    details). `WelcomeView`, `NetworkView` and `SummaryView` are the other pages.
- **Themes:**
  - `Themes/Colors.Dark.xaml` and `Colors.Light.xaml` define the same colour keys, and
    `Themes/Theme.xaml` holds every style.
  - Views only use colours through `{DynamicResource}`, so `ThemeManager` can swap the colour
    dictionary at runtime: Light, Dark, or Match Windows (it follows Windows while open).
    It also matches the title bar and saves the choice in `%AppData%\Ventrail\settings.json`.
  - The ViewModel only holds `CurrentTheme`; `App.xaml.cs` applies it.
  - Fonts: Segoe UI Variable (Text and Display) and Cascadia Mono, with Windows 10 fallbacks.
- `RepositoryManager` opens the Git repository for each operation and closes it afterwards
  (LibGit2Sharp objects aren't thread-safe), so the `.git` folder is never left locked.

## 2. Version control (Weeks 2-7)

| Feature | Where |
|---|---|
| Open/create a repository; refuse drives and system folders | `RepositoryManager.InitRepository`, `WhyFolderIsTooBroad` |
| Default `.gitignore` for Office lock files, `Thumbs.db`, build folders | `RepositoryManager.DefaultGitignore` |
| Scan, stage, commit, history | `ScanWorkingDirectory`, `Stage`, `Commit`, `GetCommitHistory` |
| Restore an old file (history is never rewritten) | `RestoreFileFromCommit` |
| Discard changes, with a safety copy in the Recycle Bin | `DiscardChanges`, `SafeDelete` |
| Line-by-line diff | `GetFileDiff` |

## 3. AI storage advisor (Weeks 8-9)

- **Model:** ML.NET **FastTree** binary classifier, *heavy* vs *light*. A file is heavy if one
  saved version adds at least 1 MB to the history after Git's zlib compression.
  - Trained offline by `Ventrail.ModelTrainer` on 6,180 files (`data/training-data.csv`).
  - Shipped as `MLModels/ventrail_classifier.zip`.
- **Features** (`FileFeatures`): log of the size, entropy of the file's header bytes (close to 8 =
  already compressed), share of zero bytes, share of printable text, the format detected from
  the magic bytes, and the family of the extension (document, image, video...).
- **Results on the held-out test set:** 99.92 % accuracy, F1 (heavy) 0.990. The old "≥ 50 MB" rule
  only found 6 % of heavy files. Full report: `Ventrail.ModelTrainer/data/metrics.md`.
- **`SuggestionWriter`** (rule-based, offline) turns the verdicts into a commit message of at most
  72 characters, storage advice and "leave out" suggestions. Nothing leaves the computer.

## 4. Peer-to-peer (Weeks 10-13)

### Ports and firewall
- **TCP 4001** carries version transfers; **UDP 4002** handles discovery and the DHT.
- `FirewallAuditor` reads Windows Firewall rules through its COM API (no admin needed) and works
  out what Windows would do with each port: block rules win, then allow rules, then the network's
  default.
- Permission is two `netsh` allow rules, run through the normal UAC prompt after Ventrail explains
  why. The rules are limited to Ventrail's exe, its two ports and `remoteip=localsubnet`.

### Discovery: LAN broadcast + Kademlia DHT
- **Broadcast** `VENTRAIL?` to 255.255.255.255 and to each network's own broadcast address
  (like libp2p's mDNS). This finds the first peers.
- **Kademlia** (`KademliaRoutingTable`):
  - 48-bit ids (`vt-` + 12 hex for peers, `rp-` + 12 hex for repositories) and XOR distance;
  - 48 k-buckets with K = 8, and iterative lookup with α = 3;
  - this finds peers that missed the broadcast.
- **Provider records** (`ProviderStore`, like IPFS content routing): a computer announces "I have
  repository *rp-…*". The key is a SHA-256 of the repository's **first commit**, so every copy of
  a repository has the same key and the key reveals nothing about its contents.
- Only addresses on this computer's own subnets are accepted. This matches the firewall rule and
  protects against peers listing internet addresses.
- One-line text messages (`DiscoveryMessage`): `VENTRAIL?` / `VENTRAIL!`, `VENTRAIL-FIND` /
  `VENTRAIL-PEERS`, `VENTRAIL-PROVIDE`, `VENTRAIL-GETPROV` / `VENTRAIL-PROVIDERS`.

### Transfers: block streaming + hash verification
```
receiver                                  sender (TCP 4001)
   ── connect ──────────────────────────▶  VENTRAIL/1 <peer id>        (id must match discovery)
   LIST                                 ◀─ REPOS n, then key head branch name
   MANIFEST <repo key>                  ◀─ WAIT (every 2 s while packing) ...
                                        ◀─ MANIFEST sha256 size 1048576 blocks head branch name
                                           + one SHA-256 per 1 MB block
   BLOCK <sha256> <i>   (for each block) ◀─ DATA i length + bytes
   BYE
```
1. The sender builds a **version package**: a Git pack and its index (`PackBuilder`) in one file,
   named by its SHA-256 and cached until the repository changes (`VersionPackage`).
2. The receiver checks **every block** against the manifest, **the whole package** at the end,
   and then **Git checks every version** while importing (objects are named by their own hash).
3. Finished blocks stay on disk (`%LocalAppData%\Ventrail\inbox`), so a cancelled or broken
   download resumes.
4. **Import never overwrites work** (`ImportVersionPackage`):
   - a clean fast-forward updates the files;
   - unsaved changes, or two different saved versions, keep the received version aside under
     `refs/remotes/<computer>/<branch>`.
5. Versions only move when the **receiving** user asks (pull, never push).

### Adaptive timeouts (`AdaptiveTimeout.cs`)
- **Per-peer round-trip time**, estimated like TCP (Jacobson/Karels, RFC 6298):
  - wait = SRTT + 4·RTTVAR, between 0.25 and 8 s, starting at 0.8 s;
  - the wait doubles after a miss (back-off).
- **Per-peer speed:** a smoothed bytes-per-second figure.
  - Each block may take 3 × its expected time plus the round-trip wait (2–120 s).
  - A timeout immediately lowers the speed estimate (multiplicative decrease).
- A peer leaves the routing table after **3 lookups in a row** without an answer.

## 5. Testing

Headless console test suites live in [tests/](../tests). Run them all with
`powershell -ExecutionPolicy Bypass -File tests\run-all.ps1`: 318 checks, from Week 7's discard
and diff up to Week 13's end-to-end runs, plus the redesign suite (it also checks that both colour
themes define every colour the views use).
- The **end-to-end suite** drives two real nodes, each through its own real ViewModel and Git
  repository on loopback.
- It covers slow links, a frozen peer, a busy sender, peer health and switching sharing off.
- Network conditions are simulated with `NetworkNodeCoordinator.TestConditions`; it has no
  effect unless a test sets it.

## 6. Known limits / future work

- **Combining two different versions (merge)** isn't supported. Ventrail keeps both, safely.
- **Each transfer sends the whole history.** A smaller "only what's new" pack is possible, since
  Git can exclude what the receiver already has.
- **Discovery is local-network only, on purpose.** Sharing over the internet would need NAT
  traversal and a public DHT, and that would expose the computer.
- **Transfers aren't encrypted on the wire.** Integrity is guaranteed by the hashes, but someone
  on the same network could read the data. TLS or Noise (as libp2p uses) would be the next step.
- **Specialised file previews** (`ISpecializedFileHandler`) were an optional feature and weren't
  built.

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;
using Ventrail.Services.Implementations.Dht;
using Ventrail.Services.Implementations.Transfer;
using Ventrail.Services.Mock;
using Ventrail.ViewModels;
using TransferProgress = Ventrail.Models.TransferProgress;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); }
T Throws<T>(string name, Action a, Func<T, bool>? ok = null) where T : Exception
{
    try { a(); Check(name, false, "(no exception)"); return null!; }
    catch (T ex) { Check(name, ok?.Invoke(ex) ?? true, ex.Message); return ex; }
    catch (Exception ex) { Check(name, false, $"{ex.GetType().Name}: {ex.Message}"); return null!; }
}

string root = Path.Combine(Path.GetTempPath(), "vw12-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
var sig = new Signature("Tester", "t@example.com", DateTimeOffset.Now);

// ---------- a real repository on the "sending" computer ----------
string src = Path.Combine(root, "Thesis");
Directory.CreateDirectory(src);
Repository.Init(src);
var bigBytes = new byte[3 * 1024 * 1024 + 12345];
new Random(42).NextBytes(bigBytes);   // random = doesn't compress, so the package spans several blocks
string Commit(string folder, string file, byte[] content, string message)
{
    using var r = new Repository(folder);
    string full = Path.Combine(folder, file);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllBytes(full, content);
    Commands.Stage(r, file);
    return r.Commit(message, sig, sig).Sha;
}
byte[] Text(string s) => Encoding.UTF8.GetBytes(s);
string v1 = Commit(src, "Chapter1.txt", Text("Introduction\n"), "v1");
Commit(src, "data/scan.bin", bigBytes, "v2 big file");
string v3 = Commit(src, "Chapter1.txt", Text("Introduction\nMore text\n"), "v3");
string repoKey = DhtKey.ForRepository(v1);

// ---------- version package ----------
string outbox = Path.Combine(root, "pk");
var info = VersionPackage.Create(src, outbox);
Check("package created with SHA-256 name", File.Exists(info.FilePath) && Path.GetFileName(info.FilePath) == info.Sha256 + ".vtpk"
      && BlockHashing.FileSha256(info.FilePath) == info.Sha256 && info.HeadHash == v3, $"{info.Size} bytes");
var blocks = BlockHashing.BlockHashes(info.FilePath);
Check("1 MB blocks", blocks.Count == (int)((info.Size + BlockHashing.BlockSize - 1) / BlockHashing.BlockSize) && blocks.Count >= 4, $"{blocks.Count} blocks");
Check("same versions -> same package (cache-friendly)", VersionPackage.Create(src, outbox).Sha256 == info.Sha256);

string junk = Path.Combine(root, "junk.vtpk");
File.WriteAllText(junk, "VTPK1\npack-zzz\n1\n1\nab");
Throws<VentrailException>("malformed package refused", () => VersionPackage.Unpack(junk, Path.Combine(root, "x")));
var truncated = File.ReadAllBytes(info.FilePath)[..^100];
File.WriteAllBytes(junk, truncated);
Throws<VentrailException>("truncated package refused", () => VersionPackage.Unpack(junk, Path.Combine(root, "x")));

// ---------- two real nodes on loopback ----------
NetworkNodeCoordinator Node(string name, int tcp, int udp) =>
    new(IPAddress.Loopback, tcp, udp, Path.Combine(root, "node-" + name), @"C:\x\Ventrail.exe", Array.Empty<IPEndPoint>(), name);
var S = Node("SENDER", 47101, 47102);
var R = Node("RECEIVER", 47111, 47112);
S.StartListening(); R.StartListening();
S.ProvideRepository(repoKey, src, "Thesis");
R.AddPeerByAddress("127.0.0.1:47102");

var offers = R.ListRepositories(S.LocalPeerId);
Check("LIST: peer offers Thesis with its latest version", offers.Count == 1 && offers[0].Key == repoKey && offers[0].Name == "Thesis"
      && offers[0].HeadHash == v3 && offers[0].PeerName == "SENDER", string.Join(",", offers.Select(o => $"{o.Name}@{o.ShortHead}")));
Throws<VentrailException>("unshared repository refused", () => R.RequestVersion(S.LocalPeerId, DhtKey.ForRepository("nope")),
    ex => ex.Message.Contains("not shared"));

var m = R.RequestVersion(S.LocalPeerId, repoKey);
Check("manifest: size, block fingerprints, head", m.HeadHash == v3 && m.BlockSize == BlockHashing.BlockSize && m.BlockCount == blocks.Count
      && m.BlockHashes.SequenceEqual(blocks) && m.Name == "Thesis" && m.Branch.Length > 0, $"{m.Size} bytes, {m.BlockCount} blocks, branch {m.Branch}");

var reports = new List<TransferProgress>();
var sync = new SyncProgress(p => reports.Add(p));
string pkg = R.DownloadPackage(m, sync, CancellationToken.None);
Check("download: whole package matches its SHA-256", BlockHashing.FileSha256(pkg) == m.PackageHash && pkg.StartsWith(Path.Combine(root, "node-RECEIVER", "inbox")));
Check("progress reported block by block up to 100%", reports.Count >= m.BlockCount && reports[^1].Percent == 100 && reports[^1].BytesDone == m.Size);
Check("sender counts bytes sent", S.BytesSent >= m.Size, $"{S.BytesSent}");
Check("already downloaded -> instant", R.DownloadPackage(m, null, CancellationToken.None) == pkg);
Check("TransferData (class diagram) works for a requested package", R.TransferData(S.LocalPeerId, m.PackageHash));
Throws<VentrailException>("TransferData for an unknown hash refused", () => R.TransferData(S.LocalPeerId, new string('0', 64)));

// ---------- download a copy ----------
var rm = new RepositoryManager();
string copy = Path.Combine(root, "Copy", "Thesis");
Check("InitFromPackage creates and opens the copy", rm.InitFromPackage(copy, pkg, m.HeadHash, m.Branch) && rm.RepoPath.TrimEnd('\\') == copy);
Check("copy has all 3 versions", rm.GetCommitHistory().Count == 3 && rm.GetCommitHistory()[0].Hash == v3);
Check("copy's files are byte-identical", File.ReadAllBytes(Path.Combine(copy, "data", "scan.bin")).SequenceEqual(bigBytes)
      && File.ReadAllText(Path.Combine(copy, "Chapter1.txt")).Replace("\r\n", "\n") == "Introduction\nMore text\n");
Check("copy has no unsaved changes", rm.ScanWorkingDirectory().Count == 0);
Check("copy gets the same repository key", DhtKey.ForRepository(rm.GetCommitHistory()[^1].Hash) == repoKey);
Throws<VentrailException>("non-empty folder refused", () => new RepositoryManager().InitFromPackage(copy, pkg, m.HeadHash, m.Branch),
    ex => ex.Message.Contains("not empty"));

// Git's own check: a package with a damaged pack is refused even if its fingerprint "matched".
var bytes = File.ReadAllBytes(pkg);
int headerEnd = 0; for (int n = 0; n < 4; headerEnd++) if (bytes[headerEnd] == '\n') n++;
for (int i = headerEnd + 40; i < headerEnd + 4000; i += 7) bytes[i] ^= 0x5a;
string damaged = Path.Combine(root, "damaged.vtpk");
File.WriteAllBytes(damaged, bytes);
Throws<VentrailException>("damaged versions fail Git's check", () => new RepositoryManager().InitFromPackage(Path.Combine(root, "Bad"), damaged, m.HeadHash, m.Branch));

// ---------- get latest ----------
string v4 = Commit(src, "Chapter2.txt", Text("Chapter 2\n"), "v4");
var m4 = R.RequestVersion(S.LocalPeerId, repoKey);
Check("new version -> new manifest, old package removed from the sender's outbox",
      m4.HeadHash == v4 && m4.PackageHash != m.PackageHash && !File.Exists(Path.Combine(root, "node-SENDER", "outbox", m.PackageHash + ".vtpk")));
Throws<VentrailException>("blocks of an outdated package refused", () => R.DownloadPackage(new TransferManifest
{
    PeerId = m.PeerId, PackageHash = new string('1', 64), Size = m.Size, BlockSize = m.BlockSize, BlockHashes = m.BlockHashes, Name = "x"
}, null, CancellationToken.None), ex => ex.Message.Contains("no longer offered"));
string pkg4 = R.DownloadPackage(m4, null, CancellationToken.None);

File.WriteAllText(Path.Combine(copy, "Chapter1.txt"), "my unsaved edit");
var w = rm.ImportVersionPackage(pkg4, m4.HeadHash, m4.Branch, "SENDER");
Check("unsaved changes -> files untouched, version kept aside", w.Outcome == ImportOutcome.WaitingForSave
      && File.ReadAllText(Path.Combine(copy, "Chapter1.txt")) == "my unsaved edit" && !File.Exists(Path.Combine(copy, "Chapter2.txt"))
      && w.SavedAs.StartsWith("refs/remotes/SENDER/"), w.SavedAs);
using (var r = new Repository(copy)) Check("kept-aside version is in the repository", r.Refs[w.SavedAs]?.TargetIdentifier == v4);
using (var r = new Repository(copy))   // user undoes their edit (what Discard does)
    r.CheckoutPaths(r.Head.Tip.Sha, new[] { "Chapter1.txt" }, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
var u = rm.ImportVersionPackage(pkg4, m4.HeadHash, m4.Branch, "SENDER");
Check("clean -> fast-forward, files updated", u.Outcome == ImportOutcome.Updated && File.Exists(Path.Combine(copy, "Chapter2.txt"))
      && rm.GetCommitHistory()[0].Hash == v4);
Check("same again -> already up to date", rm.ImportVersionPackage(pkg4, m4.HeadHash, m4.Branch, "SENDER").Outcome == ImportOutcome.AlreadyUpToDate);

string mine = Commit(copy, "notes.txt", Text("my notes"), "receiver's own version");
Check("older version received -> 'already newer'", rm.ImportVersionPackage(pkg, m.HeadHash, m.Branch, "SENDER").Outcome == ImportOutcome.AlreadyNewer
      && rm.GetCommitHistory()[0].Hash == mine);
Commit(src, "Chapter3.txt", Text("Chapter 3\n"), "v5 sender");
var m5 = R.RequestVersion(S.LocalPeerId, repoKey);
var d = rm.ImportVersionPackage(R.DownloadPackage(m5, null, CancellationToken.None), m5.HeadHash, m5.Branch, "SENDER");
Check("both saved different versions -> diverged, nothing overwritten", d.Outcome == ImportOutcome.Diverged
      && rm.GetCommitHistory()[0].Hash == mine && File.Exists(Path.Combine(copy, "notes.txt")) && !File.Exists(Path.Combine(copy, "Chapter3.txt")));

// ---------- damaged blocks, resume, cancel ----------
int damagedSent = 0;
S.TestBlockFilter = (i, data) =>
{
    if (i != 1 || Interlocked.Increment(ref damagedSent) > 2) return data;
    var bad = (byte[])data.Clone(); bad[100] ^= 0xff; return bad;   // block 1 arrives damaged twice
};
var m5b = R.RequestVersion(S.LocalPeerId, repoKey);
File.Delete(R.DownloadPackage(m5, null, CancellationToken.None));   // force a fresh download
reports.Clear();
string again = R.DownloadPackage(m5b, sync, CancellationToken.None);
Check("damaged block detected and sent again", BlockHashing.FileSha256(again) == m5b.PackageHash && reports[^1].BlocksRetried == 2,
      $"retried={reports[^1].BlocksRetried}");

S.TestBlockFilter = (i, data) => i == 2 ? new byte[data.Length] : data;   // block 2 is always wrong
File.Delete(again);
Throws<VentrailException>("block that never arrives intact -> stops with a clear message", () => R.DownloadPackage(m5b, null, CancellationToken.None),
    ex => ex.Message.Contains("Block 3 of") && ex.Message.Contains("try again"));
S.TestBlockFilter = null;
reports.Clear();
string resumed = R.DownloadPackage(m5b, sync, CancellationToken.None);
Check("try again -> resumes, only missing blocks fetched", BlockHashing.FileSha256(resumed) == m5b.PackageHash
      && reports[0].BlocksResumed == 2 && reports[0].BlocksDone == 2, $"resumed={reports[0].BlocksResumed}");

File.Delete(resumed);
var cts = new CancellationTokenSource();
var cancelAfterOne = new SyncProgress(p => { if (p.BlocksDone >= 1) cts.Cancel(); });
try { R.DownloadPackage(m5b, cancelAfterOne, cts.Token); Check("cancel stops the download", false); }
catch (OperationCanceledException) { Check("cancel stops the download", true); }
reports.Clear();
Check("after cancel -> resumes", BlockHashing.FileSha256(R.DownloadPackage(m5b, sync, CancellationToken.None)) == m5b.PackageHash
      && reports[0].BlocksResumed >= 1, $"resumed={reports[0].BlocksResumed}");

// ---------- protocol robustness ----------
using (var raw = new TcpClient())
{
    await raw.ConnectAsync(IPAddress.Loopback, 47101);
    var reader = new StreamReader(raw.GetStream());
    var writer = new StreamWriter(raw.GetStream()) { AutoFlush = true, NewLine = "\n" };
    string? hello = await reader.ReadLineAsync();
    await writer.WriteLineAsync("BLOCK nothex 0");
    string? e1 = await reader.ReadLineAsync();
    await writer.WriteLineAsync("DELETE EVERYTHING");
    string? e2 = await reader.ReadLineAsync();
    Check("nonsense requests get ERROR, then the connection closes", hello == $"VENTRAIL/1 {S.LocalPeerId}"
          && e1!.StartsWith("ERROR") && e2 == "ERROR Unknown request." && await reader.ReadLineAsync() is null, $"{e1} | {e2}");
}
Check("sender still serves after nonsense", R.ListRepositories(S.LocalPeerId).Count == 1);
Check("Connect (hello check) still works", R.EstablishConnection(S.LocalPeerId));
S.StopListening();
Throws<VentrailException>("sender offline -> friendly error", () => R.RequestVersion(S.LocalPeerId, repoKey), ex => ex.Message.Contains("didn't answer"));
R.StopListening();

// ---------- ViewModel (demo services) ----------
MainDashboardViewModel NewVm(INetworkNodeCoordinator net) => new(new MockRepositoryManager(),
    new IntelligentStorageEngine(@"C:\nope.zip"), net, new SpecializedFileHandler(), new InMemoryRecentRepositoryStore(), true);
async Task WaitFor(Func<bool> cond) { for (int i = 0; i < 100 && !cond(); i++) await Task.Delay(50); }

var mock = new MockNetworkNodeCoordinator();
var vm = NewVm(mock);
await vm.InitializeDashboardAsync(@"C:\Demo\Thesis");
vm.IsSharingEnabled = true;
var toggle = vm.ToggleSharingCommand.ExecuteAsync(null);
await WaitFor(() => vm.IsDialogOpen);
vm.ConfirmDialogCommand.Execute(null);
await toggle;
await vm.ShareRepositoryCommand.ExecuteAsync(null);
var lab = vm.ActivePeers.First(p => p.Title == "LAB-PC-02");
Check("peer lists what it shares", lab.Repositories.Count == 2 && lab.HasRepositories, string.Join(",", lab.Repositories.Select(o => $"{o.Name}:{o.ActionText}")));
var latest = lab.Repositories.First(o => o.IsCurrentRepository);
Check("same repository -> 'Get latest', other -> 'Download a copy'", latest.ActionText == "Get latest"
      && lab.Repositories.Single(o => !o.IsCurrentRepository).ActionText == "Download a copy");

var dl = vm.DownloadOfferCommand.ExecuteAsync(latest);
await WaitFor(() => vm.IsTransferring);
Check("progress card shows while receiving", vm.IsTransferring && vm.TransferTitle.Contains("LAB-PC-02"));
await dl;
Check("Get latest -> new version on top, success message", vm.History[0].Hash == new string('a', 40) && vm.StatusText.Contains("now match")
      && !vm.IsTransferring, vm.StatusText);
Check("damaged block mentioned in the log", vm.ActivityLog.Any(a => a.Text.Contains("failed the SHA-256 check")));
Check("bytes counted on the Summary page", vm.SessionBytesShared > 0, vm.SessionSharedDisplay);
await vm.DownloadOfferCommand.ExecuteAsync(latest);
Check("again -> 'already have' (nothing downloaded)", vm.ToastMessage.Contains("already have"), vm.ToastMessage);

vm.PickFolder = _ => null;
var report = vm.ActivePeers.First(p => p.Title == "LAB-PC-02").Repositories.First(o => !o.IsCurrentRepository);
int before = vm.ActivityLog.Count;
await vm.DownloadOfferCommand.ExecuteAsync(report);
Check("folder dialog cancelled -> nothing happens", vm.ActivityLog.Count == before && vm.CurrentDirectory == @"C:\Demo\Thesis");
string parent = Path.Combine(root, "Downloads");
Directory.CreateDirectory(Path.Combine(parent, "Group-Report"));
File.WriteAllText(Path.Combine(parent, "Group-Report", "keep.txt"), "x");   // a non-empty folder with that name already exists
vm.PickFolder = _ => parent;
await vm.DownloadOfferCommand.ExecuteAsync(report);
Check("Download a copy -> opens new folder (name kept free)", vm.CurrentDirectory == Path.Combine(parent, "Group-Report (2)")
      && vm.CurrentPage == AppPage.Staging, vm.CurrentDirectory);

var vm2 = NewVm(mock);
await vm2.InitializeDashboardAsync(@"C:\Demo\Thesis");
vm2.IsSharingEnabled = true;
await vm2.ToggleSharingCommand.ExecuteAsync(null);
var offer2 = vm2.ActivePeers.First(p => p.Title == "LAB-PC-02").Repositories.First(o => !o.IsCurrentRepository);
vm2.PickFolder = _ => parent;
var dl2 = vm2.DownloadOfferCommand.ExecuteAsync(offer2);
await WaitFor(() => vm2.IsTransferring);
vm2.CancelTransferCommand.Execute(null);
await dl2;
Check("Cancel -> stops with 'kept' message, nothing opened", vm2.ToastMessage.Contains("Download cancelled") && !vm2.IsTransferring
      && vm2.CurrentDirectory == @"C:\Demo\Thesis", vm2.ToastMessage);

try { foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(root, true); } catch (Exception) { }
Console.WriteLine($"\n{pass} passed, {fail} failed.");

// Reports on the calling thread (Progress<T> would post to the thread pool and arrive late).
class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
{
    public void Report(TransferProgress value) => report(value);
}

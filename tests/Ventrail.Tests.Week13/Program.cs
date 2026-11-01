using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;
using Ventrail.Services.Implementations.Transfer;
using Ventrail.Services.Mock;
using Ventrail.ViewModels;
using TransferProgress = Ventrail.Models.TransferProgress;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); }
void Section(string title) => Console.WriteLine($"\n--- {title} ---");

// ================= 1. adaptive timeouts (pure logic) =================
Section("adaptive timeouts");
var est = new RttEstimator(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(8));
Check("starts at the Week 11 value (0.8 s)", est.Timeout == TimeSpan.FromMilliseconds(800) && est.Smoothed is null);
for (int i = 0; i < 20; i++) est.Observe(TimeSpan.FromMilliseconds(5));
Check("fast LAN -> short wait (but never below 0.25 s)", est.Timeout == TimeSpan.FromMilliseconds(250), $"{est.Timeout.TotalMilliseconds} ms");
var slow = new RttEstimator(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(8));
slow.Observe(TimeSpan.FromMilliseconds(1200));
Check("one slow answer -> wait above it straight away", slow.Timeout > TimeSpan.FromMilliseconds(1200), $"{slow.Timeout.TotalMilliseconds} ms");
var jitter = new RttEstimator(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(8));
var steady = new RttEstimator(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(8));
var rnd = new Random(3);
for (int i = 0; i < 50; i++) { jitter.Observe(TimeSpan.FromMilliseconds(rnd.Next(100, 900))); steady.Observe(TimeSpan.FromMilliseconds(500)); }
Check("unsteady link -> more patience than a steady one with the same average", jitter.Timeout > steady.Timeout,
      $"{jitter.Timeout.TotalMilliseconds:F0} vs {steady.Timeout.TotalMilliseconds:F0} ms");
est.Backoff(); est.Backoff();
Check("missed answers -> wait doubles", est.Timeout == TimeSpan.FromSeconds(1));
for (int i = 0; i < 10; i++) est.Backoff();
Check("...but never beyond 8 s", est.Timeout == TimeSpan.FromSeconds(8));
est.Observe(TimeSpan.FromMilliseconds(5));
Check("a real answer brings it back down", est.Timeout < TimeSpan.FromSeconds(8), $"{est.Timeout.TotalMilliseconds:F0} ms");

var link = new PeerLink();
Check("block wait before any speed is known = 30 s (Week 12 value)", link.BlockTimeout(1 << 20) == TimeSpan.FromSeconds(30));
link.Throughput.Observe(1 << 20, TimeSpan.FromMilliseconds(10));   // 100 MB/s LAN
Check("fast link -> a stuck block is noticed in 2 s", link.BlockTimeout(1 << 20) == TimeSpan.FromSeconds(2));
var slowLink = new PeerLink();
slowLink.Throughput.Observe(1 << 20, TimeSpan.FromSeconds(10));    // 100 KB/s
Check("slow link -> 3x the expected time", slowLink.BlockTimeout(1 << 20) > TimeSpan.FromSeconds(30), $"{slowLink.BlockTimeout(1 << 20).TotalSeconds:F0} s");
Check("connect wait follows the round trip (min 2 s)", new PeerLink().ConnectTimeout == TimeSpan.FromSeconds(2.6));
var dropped = new PeerLink();
dropped.Throughput.Observe(1 << 20, TimeSpan.FromMilliseconds(10));
dropped.Throughput.TimedOut(1 << 20, TimeSpan.FromSeconds(2));
Check("a timed-out block drops the speed estimate at once (next wait fits a slow link)", dropped.BlockTimeout(1 << 20) >= TimeSpan.FromSeconds(12), $"{dropped.BlockTimeout(1 << 20).TotalSeconds:F0} s");

// ================= helpers for the end-to-end runs =================
string root = Path.Combine(Path.GetTempPath(), "vw13-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
int portBase = 48100;
NetworkNodeCoordinator Node(string name, int n, params int[] targets) =>
    new(IPAddress.Loopback, portBase + n * 10 + 1, portBase + n * 10 + 2, Path.Combine(root, "node-" + name), @"C:\x\Ventrail.exe",
        targets.Select(t => new IPEndPoint(IPAddress.Loopback, portBase + t * 10 + 2)), name);
MainDashboardViewModel Vm(INetworkNodeCoordinator net) => new(new RepositoryManager(),
    new IntelligentStorageEngine(@"C:\nope.zip"), new AllowedFirewall(net), new SpecializedFileHandler(), new InMemoryRecentRepositoryStore());
async Task Save(MainDashboardViewModel vm, string message)
{
    await vm.RefreshCommand.ExecuteAsync(null);
    await vm.StageAllCommand.ExecuteAsync(null);
    vm.CommitMessage = message;
    await vm.CommitCommand.ExecuteAsync(null);
}
async Task SharingOn(MainDashboardViewModel vm) { vm.IsSharingEnabled = true; await vm.ToggleSharingCommand.ExecuteAsync(null); }
OfferedRepository? OfferFrom(MainDashboardViewModel vm, string peer, string repo) =>
    vm.ActivePeers.FirstOrDefault(p => p.Title == peer)?.Repositories.FirstOrDefault(o => o.Name == repo);
string Norm(string path) => File.ReadAllText(path).Replace("\r\n", "\n");
byte[] Random(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

// ================= 2. end to end: a day in the life =================
Section("end to end: Alice and Bob");
string alice = Path.Combine(root, "Alice", "Thesis");
Directory.CreateDirectory(Path.Combine(alice, "data"));
File.WriteAllText(Path.Combine(alice, "Chapter1.txt"), "Chapter 1 - draft\n");
File.WriteAllBytes(Path.Combine(alice, "data", "survey.bin"), Random(1_600_000, 1));   // 2 blocks

var nodeA = Node("ALICE-PC", 0, 1);
var nodeB = Node("BOB-PC", 1, 0);
var A = Vm(nodeA);
var B = Vm(nodeB);

await A.InitializeDashboardAsync(alice);
await Save(A, "First draft");
Check("Alice saves her first version", A.History.Count == 1 && A.Changes.Count == 0, A.StatusText);
await SharingOn(A);
Check("Alice's sharing on without a prompt (firewall already allowed)", A.IsSharingEnabled && nodeA.IsListening && !A.IsDialogOpen);
await A.ShareRepositoryCommand.ExecuteAsync(null);
Check("Alice shares Thesis", A.StatusText.Contains("Thesis is shared"), A.StatusText);

await SharingOn(B);
var offer = OfferFrom(B, "ALICE-PC", "Thesis");
Check("Bob finds Alice and what she shares", offer is { ActionText: "Download a copy" }, B.PeersSummary);
Check("Bob sees Alice's round-trip time", B.ActivePeers.First().RoundTripMs is >= 0 and < 250, B.ActivePeers.First().RoundTripText);
Check("Alice's list shows Bob too (he contacted her)", A.ActivePeers.Any(p => p.Title == "BOB-PC"));

string bobFolder = Path.Combine(root, "Bob");
Directory.CreateDirectory(bobFolder);
B.PickFolder = _ => bobFolder;
await B.DownloadOfferCommand.ExecuteAsync(offer);
string bob = Path.Combine(bobFolder, "Thesis");
Check("Bob downloads a copy; it opens straight away", B.CurrentDirectory.TrimEnd('\\') == bob && B.History.Count == 1, B.ToastMessage);
Check("Bob's files are identical", Norm(Path.Combine(bob, "Chapter1.txt")) == "Chapter 1 - draft\n"
      && File.ReadAllBytes(Path.Combine(bob, "data", "survey.bin")).SequenceEqual(File.ReadAllBytes(Path.Combine(alice, "data", "survey.bin"))));
Check("Bob has nothing unsaved after the download", B.Changes.Count == 0);
Check("bytes counted on both sides", B.SessionBytesShared > 1_600_000 && nodeA.BytesSent > 1_600_000);

File.WriteAllText(Path.Combine(alice, "Chapter1.txt"), "Chapter 1 - second draft\n");
await Save(A, "Second draft");
await B.FindPeersCommand.ExecuteAsync(null);
offer = OfferFrom(B, "ALICE-PC", "Thesis");
Check("now the same repository -> 'Get latest'", offer is { ActionText: "Get latest" } && offer.HeadHash == A.History[0].Hash);
await B.DownloadOfferCommand.ExecuteAsync(offer);
Check("Get latest -> Bob's files match Alice's second draft", Norm(Path.Combine(bob, "Chapter1.txt")) == "Chapter 1 - second draft\n"
      && B.History.Count == 2 && B.History[0].Hash == A.History[0].Hash, B.StatusText);

File.WriteAllText(Path.Combine(bob, "Chapter1.txt"), "Bob's unsaved notes\n");
File.WriteAllText(Path.Combine(alice, "Chapter2.txt"), "Chapter 2\n");
await Save(A, "Add chapter 2");
await B.FindPeersCommand.ExecuteAsync(null);
await B.DownloadOfferCommand.ExecuteAsync(OfferFrom(B, "ALICE-PC", "Thesis"));
Check("Bob has unsaved work -> nothing overwritten", B.StatusText.Contains("unsaved changes")
      && Norm(Path.Combine(bob, "Chapter1.txt")) == "Bob's unsaved notes\n" && !File.Exists(Path.Combine(bob, "Chapter2.txt")), B.StatusText);
await Save(B, "Bob's comments");
await B.FindPeersCommand.ExecuteAsync(null);
await B.DownloadOfferCommand.ExecuteAsync(OfferFrom(B, "ALICE-PC", "Thesis"));
Check("both saved different versions -> kept apart, Bob's version stays", B.StatusText.Contains("each saved different versions")
      && B.History[0].Message == "Bob's comments", B.StatusText);

// Bob shares too: Alice can see his copy and its latest version.
await B.ShareRepositoryCommand.ExecuteAsync(null);
await A.FindPeersCommand.ExecuteAsync(null);
var fromBob = OfferFrom(A, "BOB-PC", "Thesis");
Check("two-way: Alice sees Bob's copy as the same repository", fromBob is { IsCurrentRepository: true } && fromBob.HeadHash == B.History[0].Hash);

// ================= 3. slow network =================
Section("slow network (1 s delay, 400 KB/s)");
nodeA.TestConditions = new NetworkConditions { Latency = TimeSpan.FromMilliseconds(1000), BytesPerSecond = 400 * 1024 };
File.WriteAllBytes(Path.Combine(alice, "data", "survey.bin"), Random(1_600_000, 2));
await Save(A, "New survey data");
var sw = Stopwatch.StartNew();
await B.FindPeersCommand.ExecuteAsync(null);
var alicePeer = B.ActivePeers.FirstOrDefault(p => p.Title == "ALICE-PC");
Check("Bob still finds Alice (a fixed 0.8 s wait would miss every answer)", alicePeer != null && alicePeer.HasRepositories,
      $"{sw.ElapsedMilliseconds} ms");
Check("Bob's measured round trip went up (smoothed, so it climbs over a few answers)", alicePeer?.RoundTripMs is > 200, alicePeer?.RoundTripText ?? "");

// Bob's copy diverged earlier, so download a fresh copy of the new version over the slow link.
var progressSeen = new List<string>();
B.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(B.TransferDetail)) lock (progressSeen) progressSeen.Add(B.TransferDetail); };
string bob2Folder = Path.Combine(root, "Bob2");
Directory.CreateDirectory(bob2Folder);
B.PickFolder = _ => bob2Folder;
sw.Restart();
var slowOffer = OfferFrom(B, "ALICE-PC", "Thesis")!;
slowOffer.IsCurrentRepository = false;   // download a fresh copy rather than "Get latest" into Bob's diverged one
await B.DownloadOfferCommand.ExecuteAsync(slowOffer);
Check("download over the slow link completes", B.CurrentDirectory.StartsWith(bob2Folder) && B.History[0].Hash == A.History[0].Hash,
      $"{sw.Elapsed.TotalSeconds:F1} s, {B.ToastMessage}");
Check("progress shows speed and time left", progressSeen.Any(t => t.Contains("/s") && t.Contains("left")), progressSeen.LastOrDefault() ?? "");
Check("speed learnt on the fast link didn't cause a timeout on every block", progressSeen.All(t => !t.Contains("asked for again")) || progressSeen.Last().Contains("1 block(s) asked for again"), progressSeen.LastOrDefault() ?? "");
nodeA.TestConditions = null;

// ================= 4. a sender that is busy packing =================
Section("sender busy packing (8 s)");
nodeA.TestConditions = new NetworkConditions { ExtraPackingTime = TimeSpan.FromSeconds(8) };
File.WriteAllText(Path.Combine(alice, "Chapter3.txt"), "Chapter 3\n");
await Save(A, "Add chapter 3");
sw.Restart();
var m = nodeB.RequestVersion(nodeA.LocalPeerId, nodeA.GetKnownPeers().Count >= 0 ? OfferFrom(B, "ALICE-PC", "Thesis")!.Key : "");
Check("receiver keeps waiting while the sender says WAIT", m.HeadHash == A.History[0].Hash && sw.Elapsed > TimeSpan.FromSeconds(7),
      $"{sw.Elapsed.TotalSeconds:F1} s");
nodeA.TestConditions = null;

// ================= 5. a peer that freezes mid-download =================
Section("frozen peer");
File.WriteAllBytes(Path.Combine(alice, "data", "big.bin"), Random(4_200_000, 3));   // 5 blocks
await Save(A, "Add big file");
var key = OfferFrom(B, "ALICE-PC", "Thesis")!.Key;
var m5 = nodeB.RequestVersion(nodeA.LocalPeerId, key);
nodeA.TestConditions = new NetworkConditions { BytesPerSecond = 2 * 1024 * 1024 };
var freezeAfterTwo = new SyncProgress(p => { if (p.BlocksDone >= 2 && nodeA.TestConditions is { } c) c.Stall = true; });
sw.Restart();
string? frozenMessage = null;
try { nodeB.DownloadPackage(m5, freezeAfterTwo, CancellationToken.None); }
catch (VentrailException ex) { frozenMessage = ex.Message; }
sw.Stop();
Check("frozen peer noticed quickly (Week 12: 30 s per try, 90 s+)", frozenMessage != null && sw.Elapsed < TimeSpan.FromSeconds(15),
      $"{sw.Elapsed.TotalSeconds:F1} s: {frozenMessage}");
nodeA.TestConditions = null;
var resumeReports = new List<TransferProgress>();
string done = nodeB.DownloadPackage(m5, new SyncProgress(resumeReports.Add), CancellationToken.None);
Check("peer back -> download resumes from block 3", BlockHashing.FileSha256(done) == m5.PackageHash && resumeReports[0].BlocksResumed >= 2,
      $"resumed={resumeReports[0].BlocksResumed}");

// ================= 6. peers that slow down are kept, dead ones dropped =================
Section("peer health");
var C = Node("CAROL-PC", 2);           // no broadcast targets: she only knows peers through the DHT
C.StartListening();
C.AddPeerByAddress($"127.0.0.1:{portBase + 2}");   // knows Alice (fast) ...
nodeA.TestConditions = new NetworkConditions { Latency = TimeSpan.FromMilliseconds(1200) };   // ... then Alice's link gets slow
C.DiscoverPeers();
Check("one missed lookup doesn't drop a peer that got slower", C.GetKnownPeers().Any(p => p.Title == "ALICE-PC")
      && C.GetKnownPeers().All(p => p.Title != "BOB-PC"));
C.DiscoverPeers();
C.DiscoverPeers();
Check("the wait grows until Alice's slow answers get through: Carol learns Bob from her",
      C.GetKnownPeers().Any(p => p.Title == "ALICE-PC") && C.GetKnownPeers().Any(p => p.Title == "BOB-PC"),
      string.Join(",", C.GetKnownPeers().Select(p => $"{p.Title}{p.RoundTripText}")));
nodeA.TestConditions = null;
nodeB.StopListening();
for (int i = 0; i < PeerLink.DropAfterMisses; i++)
{
    C.DiscoverPeers();
    if (i < PeerLink.DropAfterMisses - 1)
        Check($"Bob silent for {i + 1} lookup(s): still kept", C.GetKnownPeers().Any(p => p.Title == "BOB-PC"));
}
Check("Bob silent for 3 lookups: dropped", C.GetKnownPeers().All(p => p.Title != "BOB-PC"),
      string.Join(",", C.GetKnownPeers().Select(p => p.Title)));

// ================= 7. sharing off on the other side =================
Section("sharing switched off");
await SharingOn(B);   // Bob back on
await B.FindPeersCommand.ExecuteAsync(null);
var last = OfferFrom(B, "ALICE-PC", "Thesis");
A.IsSharingEnabled = false;
await A.ToggleSharingCommand.ExecuteAsync(null);
sw.Restart();
await B.DownloadOfferCommand.ExecuteAsync(last);
Check("Alice offline -> Bob gets a clear message quickly", B.ToastMessage.Contains("didn't answer") && sw.Elapsed < TimeSpan.FromSeconds(5),
      $"{sw.Elapsed.TotalSeconds:F1} s: {B.ToastMessage}");
Check("error is in Bob's activity log", B.ActivityLog.Any(a => a.Text.Contains("didn't answer")));

nodeA.StopListening(); nodeB.StopListening(); C.StopListening();
try { foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(root, true); } catch (Exception) { }
Console.WriteLine($"\n{pass} passed, {fail} failed.");

class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
{
    public void Report(TransferProgress value) => report(value);
}

// The real node, except the firewall check: tests must never trigger the Windows admin prompt.
class AllowedFirewall(INetworkNodeCoordinator real) : INetworkNodeCoordinator
{
    public FirewallStatus CheckFirewall() => new()
    {
        NetworkProfile = "Private", HasVentrailRules = true,
        Ports = new() { new() { Port = LocalPort, Protocol = "TCP", Access = PortAccess.Allowed, Detail = "test" },
                        new() { Port = DiscoveryPort, Protocol = "UDP", Access = PortAccess.Allowed, Detail = "test" } }
    };
    public bool RequestFirewallPermission(bool includePublicNetworks) => throw new InvalidOperationException("never in tests");
    public bool RemoveFirewallPermission() => throw new InvalidOperationException("never in tests");

    public int LocalPort => real.LocalPort;
    public int DiscoveryPort => real.DiscoveryPort;
    public string LocalPeerId => real.LocalPeerId;
    public List<PeerNode> ConnectedPeers => real.ConnectedPeers;
    public bool IsListening => real.IsListening;
    public bool StartListening() => real.StartListening();
    public void StopListening() => real.StopListening();
    public event EventHandler? PeersChanged { add => real.PeersChanged += value; remove => real.PeersChanged -= value; }
    public string DisplayName => real.DisplayName;
    public List<PeerNode> GetKnownPeers() => real.GetKnownPeers();
    public List<PeerNode> DiscoverPeers() => real.DiscoverPeers();
    public PeerNode? AddPeerByAddress(string address) => real.AddPeerByAddress(address);
    public void ProvideRepository(string k, string p, string n) => real.ProvideRepository(k, p, n);
    public List<PeerNode> FindProviders(string k) => real.FindProviders(k);
    public bool EstablishConnection(string id) => real.EstablishConnection(id);
    public List<OfferedRepository> ListRepositories(string id) => real.ListRepositories(id);
    public TransferManifest RequestVersion(string id, string k) => real.RequestVersion(id, k);
    public string DownloadPackage(TransferManifest m, IProgress<TransferProgress>? p, CancellationToken c) => real.DownloadPackage(m, p, c);
    public bool TransferData(string id, string h) => real.TransferData(id, h);
    public long BytesSent => real.BytesSent;
}

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;
using Ventrail.Services.Implementations.Dht;
using Ventrail.Services.Mock;
using Ventrail.ViewModels;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); }
PeerNode P(string id, string name = "") => new() { PeerId = id, DisplayName = name, IPAddress = "127.0.0.1", Port = 1, DiscoveryPort = 2 };

// ---------- keys and XOR distance ----------
Check("valid ids", DhtKey.IsValid("vt-0123456789ab") && DhtKey.IsValid("rp-ffffffffffff"));
Check("invalid ids rejected", !DhtKey.IsValid("vt-0123") && !DhtKey.IsValid("xx-0123456789ab") && !DhtKey.IsValid("vt-0123456789AB")
      && !DhtKey.IsValid("vt-01234567890g") && !DhtKey.IsValid(null));
Check("XOR distance", DhtKey.Distance("vt-000000000001", "vt-000000000003") == 2 && DhtKey.Distance("vt-00000000000f", "vt-00000000000f") == 0);
Check("bucket index = highest differing bit", KademliaRoutingTable.BucketIndex("vt-000000000000", "vt-000000000001") == 0
      && KademliaRoutingTable.BucketIndex("vt-000000000000", "vt-800000000000") == 47
      && KademliaRoutingTable.BucketIndex("vt-000000000000", "vt-000000000100") == 8);
string k1 = DhtKey.ForRepository("ABCDEF0123"), k2 = DhtKey.ForRepository("abcdef0123"), k3 = DhtKey.ForRepository("abcdef0124");
Check("repo key: same first version -> same key, different -> different", k1 == k2 && k1 != k3 && k1.StartsWith("rp-") && DhtKey.IsValid(k1), k1);

// ---------- routing table ----------
var table = new KademliaRoutingTable("vt-000000000000");
var t0 = DateTime.UtcNow;
Check("add new peer -> true", table.AddOrUpdate(P("vt-000000000001", "PC1"), t0));
Check("same peer again -> refresh, not new", !table.AddOrUpdate(P("vt-000000000001"), t0.AddSeconds(5)) && table.Count == 1);
Check("refresh keeps the name learnt earlier", table.Find("vt-000000000001")!.DisplayName == "PC1");
Check("self and invalid ids are never added", !table.AddOrUpdate(P("vt-000000000000"), t0) && !table.AddOrUpdate(P("vt-bad"), t0));
// Bucket 47 (first bit differs): fill it with K peers.
var tb = new KademliaRoutingTable("vt-000000000000");
for (int i = 0; i < KademliaRoutingTable.K; i++) tb.AddOrUpdate(P($"vt-8000000000{i:x2}"), t0);
Check("full bucket ignores a newcomer while everyone is active", !tb.AddOrUpdate(P("vt-8000000000ff"), t0.AddMinutes(1)) && tb.Count == KademliaRoutingTable.K);
Check("full bucket replaces a long-silent peer", tb.AddOrUpdate(P("vt-8000000000ff"), t0.AddMinutes(20)) && tb.Find("vt-800000000000") is null);
var rnd = new Random(7);
var big = new KademliaRoutingTable("vt-123456789abc");
var ids = Enumerable.Range(0, 300).Select(_ => "vt-" + rnd.NextInt64(0, 1L << 48).ToString("x12")).Distinct().ToList();
foreach (var id in ids) big.AddOrUpdate(P(id), t0);
string target = "vt-fedcba987654";
var closest = big.Closest(target).Select(p => p.PeerId).ToList();
var inTable = big.All().Select(p => p.PeerId).ToList();
var brute = inTable.OrderBy(id => DhtKey.Distance(id, target)).Take(8).ToList();
Check("Closest() = brute-force XOR order", closest.SequenceEqual(brute));
Check("table stays small (k-buckets)", big.Count < ids.Count && big.Count <= 48 * KademliaRoutingTable.K, $"{big.Count} of {ids.Count}");
Check("Remove", big.Remove(closest[0]) && big.Find(closest[0]) is null);

// ---------- provider records ----------
var store = new ProviderStore();
store.Add(k1, P("vt-000000000001"), t0);
Check("provider record found", store.Get(k1, t0.AddMinutes(1)).Count == 1 && store.Get(k3, t0).Count == 0);
Check("provider record expires", store.Get(k1, t0.AddMinutes(31)).Count == 0);
for (int i = 0; i < 30; i++) store.Add(k3, P($"vt-0000000001{i:x2}"), t0.AddSeconds(i));
Check("providers per key are capped", store.Get(k3, t0.AddMinutes(1)).Count == ProviderStore.MaxProvidersPerKey);

// ---------- message format ----------
string header = DiscoveryMessage.Header(DiscoveryMessage.Find, "vt-0123456789ab", 4001, 4002, "Lab PC #2");
var parsed = DiscoveryMessage.Parse(header + " rp-aaaaaaaaaaaa");
Check("header round trip", parsed is { Verb: DiscoveryMessage.Find } && parsed.Sender!.PeerId == "vt-0123456789ab" && parsed.Sender.Port == 4001
      && parsed.Sender.DiscoveryPort == 4002 && parsed.Sender.DisplayName == "Lab_PC_2" && parsed.Args.SequenceEqual(new[] { "rp-aaaaaaaaaaaa" }), header);
Check("bare Week 10 question still understood", DiscoveryMessage.Parse("VENTRAIL?") is { Verb: "VENTRAIL?", Sender: null });
Check("malformed messages ignored", new[] { "", "HELLO", "VENTRAIL-FIND", "VENTRAIL-FIND vt-0123456789ab 4001 4002",
      "VENTRAIL? vt-0123 4001 4002 x", "VENTRAIL? vt-0123456789ab 0 4002 x", "VENTRAIL? vt-0123456789ab 4001 70000 x",
      "VENTRAIL? rp-0123456789ab 4001 4002 x" }.All(s => DiscoveryMessage.Parse(s) is null));
Check("oversized or non-UTF-8 packets ignored", DiscoveryMessage.Parse(new byte[2000]) is null && DiscoveryMessage.Parse(new byte[] { 0x56, 0xff, 0xfe }) is null);
Check("names are one clean word", DiscoveryMessage.CleanName("a b;c\nd" + new string('x', 50)).Length == 32 && DiscoveryMessage.CleanName("") == "-");
var entries = DiscoveryMessage.Entries(new[] { new PeerNode { PeerId = "vt-0123456789ab", IPAddress = "192.168.1.5", Port = 4001, DiscoveryPort = 4002, DisplayName = "PC" } });
var back = DiscoveryMessage.ParseEntries(entries);
Check("entries round trip", back.Count == 1 && back[0].IPAddress == "192.168.1.5" && back[0].DiscoveryPort == 4002 && back[0].DisplayName == "PC", entries);
Check("bad entries skipped", DiscoveryMessage.ParseEntries("vt-0123456789ab@::1:1:2:x,junk,vt-bad@1.2.3.4:1:2:x,vt-0123456789ab@1.2.3.4:0:2:x,-").Count == 0);
Check("entry list capped", DiscoveryMessage.ParseEntries(string.Join(",", Enumerable.Range(0, 30).Select(i => $"vt-0000000000{i:x2}@10.0.0.1:1:2:-"))).Count == DiscoveryMessage.MaxEntries);
Check("no entries -> '-'", DiscoveryMessage.Entries(Array.Empty<PeerNode>()) == "-" && DiscoveryMessage.ParseEntries("-").Count == 0);

// ---------- local network rule (matches the firewall's localsubnet) ----------
Check("loopback is local, internet is not", NetworkNodeCoordinator.IsLocalNetworkAddress(IPAddress.Loopback)
      && !NetworkNodeCoordinator.IsLocalNetworkAddress(IPAddress.Parse("8.8.8.8")));
var myLan = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
    .SelectMany(n => n.GetIPProperties().UnicastAddresses).FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
if (myLan != null)
{
    var neighbour = myLan.GetAddressBytes(); neighbour[3] = (byte)(neighbour[3] == 1 ? 2 : 1);
    Check("this PC's own network is local", NetworkNodeCoordinator.IsLocalNetworkAddress(myLan) && NetworkNodeCoordinator.IsLocalNetworkAddress(new IPAddress(neighbour)), myLan.ToString());
}
var bc = NetworkNodeCoordinator.DefaultBroadcastTargets(4002);
Check("broadcast targets: 255.255.255.255 + each network's broadcast", bc[0].Address.Equals(IPAddress.Broadcast) && bc.All(t => t.Port == 4002),
      string.Join(", ", bc.Select(t => t.Address)));

// ---------- three real nodes on loopback ----------
// The "broadcast" of A only reaches B, so A can only find C through the DHT (B tells A about C).
string root = Path.Combine(Path.GetTempPath(), "vw11-" + Guid.NewGuid().ToString("N")[..6]);
IPEndPoint Ep(int port) => new(IPAddress.Loopback, port);
NetworkNodeCoordinator Node(string name, int tcp, int udp, params int[] targets) =>
    new(IPAddress.Loopback, tcp, udp, Path.Combine(root, name), @"C:\x\Ventrail.exe", targets.Select(Ep), name);
var A = Node("NODE-A", 46101, 46102, 46112);
var B = Node("NODE-B", 46111, 46112, 46102, 46122);
var C = Node("NODE-C", 46121, 46122, 46112);
Check("three different peer ids", new[] { A.LocalPeerId, B.LocalPeerId, C.LocalPeerId }.Distinct().Count() == 3);

try { A.DiscoverPeers(); Check("discovery needs sharing on", false); }
catch (VentrailException ex) { Check("discovery needs sharing on", ex.Message.Contains("Turn on P2P sharing")); }

A.StartListening(); B.StartListening(); C.StartListening();
int bChanged = 0;
B.PeersChanged += (_, _) => Interlocked.Increment(ref bChanged);

// Week 10 compatibility: the bare question still gets an answer starting "VENTRAIL! <id> <tcp>".
using (var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
{
    await u.SendAsync(Encoding.UTF8.GetBytes("garbage \u0001\u0002"), Ep(46102));
    await u.SendAsync(Encoding.UTF8.GetBytes("VENTRAIL?"), Ep(46102));
    var r = u.ReceiveAsync();
    bool got = await Task.WhenAny(r, Task.Delay(2000)) == r;
    string text = got ? Encoding.UTF8.GetString(r.Result.Buffer) : "";
    Check("bare question answered (after ignoring junk)", text.StartsWith($"VENTRAIL! {A.LocalPeerId} 46101 46102 NODE-A"), text);
}

var cPeers = C.DiscoverPeers();
Check("C finds B by broadcast", cPeers.Any(p => p.PeerId == B.LocalPeerId && p.DisplayName == "NODE-B" && p.Port == 46111 && p.DiscoveryPort == 46122 - 10));
Check("B learnt C from C's question (no search on B)", B.GetKnownPeers().Any(p => p.PeerId == C.LocalPeerId) && bChanged > 0, $"events={bChanged}");

var sw = Stopwatch.StartNew();
var aPeers = A.DiscoverPeers();
sw.Stop();
Check("A finds C through the DHT (broadcast never reached C)", aPeers.Any(p => p.PeerId == C.LocalPeerId && p.DisplayName == "NODE-C"),
      string.Join(", ", aPeers.Select(p => p.Title)));
Check("A finds B", aPeers.Any(p => p.PeerId == B.LocalPeerId));
Check("C learnt A when A looked it up", C.GetKnownPeers().Any(p => p.PeerId == A.LocalPeerId));
Check("discovery is quick on a LAN", sw.Elapsed < TimeSpan.FromSeconds(6), $"({sw.ElapsedMilliseconds} ms)");
Check("nobody lists itself", !aPeers.Any(p => p.PeerId == A.LocalPeerId) && !B.GetKnownPeers().Any(p => p.PeerId == B.LocalPeerId));

// ---------- provider records over the network ----------
string repoKey = DhtKey.ForRepository("1111111111111111111111111111111111111111");
A.ProvideRepository(repoKey, root, "Repo");
var providers = C.FindProviders(repoKey);
Check("C finds that A has the repository", providers.Count == 1 && providers[0].PeerId == A.LocalPeerId, string.Join(",", providers.Select(p => p.Title)));
Check("unknown repository -> nobody", C.FindProviders(DhtKey.ForRepository("2222")).Count == 0);
Check("A doesn't list itself as a provider", A.FindProviders(repoKey).All(p => p.PeerId != A.LocalPeerId));
try { A.ProvideRepository(A.LocalPeerId, root, "Repo"); Check("peer id is not a repo key", false); }
catch (ArgumentException) { Check("peer id is not a repo key", true); }

// ---------- connect (TCP hello check) ----------
Check("C connects to A", C.EstablishConnection(A.LocalPeerId) && C.ConnectedPeers.Any(p => p.PeerId == A.LocalPeerId)
      && C.GetKnownPeers().First(p => p.PeerId == A.LocalPeerId).IsConnected);
try { C.EstablishConnection("vt-999999999999"); Check("unknown peer -> friendly error", false); }
catch (VentrailException ex) { Check("unknown peer -> friendly error", ex.Message.Contains("Search for peers again")); }

// ---------- add by address ----------
var D = Node("NODE-D", 46131, 46132);   // no broadcast targets at all
D.StartListening();
var viaAddress = D.AddPeerByAddress("127.0.0.1:46112");
Check("add by address finds B", viaAddress?.PeerId == B.LocalPeerId && viaAddress.DisplayName == "NODE-B");
Check("nobody there -> null", D.AddPeerByAddress("127.0.0.1:46199") is null);
try { D.AddPeerByAddress("8.8.8.8"); Check("internet address refused", false); }
catch (VentrailException ex) { Check("internet address refused", ex.Message.Contains("not on your local network"), ex.Message); }
try { D.AddPeerByAddress("  "); Check("empty address -> friendly error", false); }
catch (VentrailException) { Check("empty address -> friendly error", true); }

// ---------- a lying peer: lists an internet address and a dead local one ----------
const string FakeId = "vt-ffffffffffff";
using var fake = new UdpClient(new IPEndPoint(IPAddress.Loopback, 46190));
var fakeStop = new CancellationTokenSource();
int fakeQuestions = 0;
_ = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            var pk = await fake.ReceiveAsync(fakeStop.Token);
            var m = DiscoveryMessage.Parse(pk.Buffer);
            if (m is null) continue;
            Interlocked.Increment(ref fakeQuestions);
            string h(string verb) => DiscoveryMessage.Header(verb, FakeId, 46191, 46190, "FAKE");
            string? reply = m.Verb switch
            {
                DiscoveryMessage.Hello => h(DiscoveryMessage.HelloAnswer),
                DiscoveryMessage.Find => h(DiscoveryMessage.Peers) + " vt-123456789abc@8.8.8.8:4001:4002:EVIL,vt-0000000000aa@127.0.0.1:46198:46198:GHOST",
                _ => null
            };
            if (reply != null) await fake.SendAsync(Encoding.UTF8.GetBytes(reply), pk.RemoteEndPoint);
        }
    }
    catch (Exception) { }
});
var E = Node("NODE-E", 46141, 46142, 46190);
E.StartListening();
var ePeers = E.DiscoverPeers();
Check("internet address from a peer is ignored", ePeers.Any(p => p.PeerId == FakeId) && !ePeers.Any(p => p.PeerId == "vt-123456789abc"));
Check("listed-but-silent peer never enters the table", !ePeers.Any(p => p.PeerId == "vt-0000000000aa"));
fakeStop.Cancel();
fake.Close();
E.StopListening();
var ePeers2 = E.DiscoverPeersSafe();
Check("discover after stop -> refused", ePeers2 is null);

// ---------- peer goes offline / address reused ----------
A.StopListening();
try { C.EstablishConnection(A.LocalPeerId); Check("offline peer -> friendly error", false); }
catch (VentrailException ex) { Check("offline peer -> friendly error", ex.Message.Contains("didn't answer"), ex.Message); }
var impostor = Node("NODE-IMPOSTOR", 46101, 46102);   // a different Ventrail now uses A's address
impostor.StartListening();
try { C.EstablishConnection(A.LocalPeerId); Check("different computer at that address -> noticed", false); }
catch (VentrailException ex) { Check("different computer at that address -> noticed", ex.Message.Contains("no longer") && C.GetKnownPeers().All(p => p.PeerId != A.LocalPeerId), ex.Message); }
impostor.StopListening();

// Dead peers are dropped by the next lookup (C still knows B; stop B and look again).
B.StopListening();
for (int i = 0; i < 3; i++) C.DiscoverPeers();   // Week 13: dropped after 3 silent lookups, not 1
Check("peer that stopped answering is dropped (after 3 lookups)", C.GetKnownPeers().All(p => p.PeerId != B.LocalPeerId),
      string.Join(",", C.GetKnownPeers().Select(p => p.Title)));
Check("stop clears connections", (C.StopListeningAnd(() => C.ConnectedPeers.Count == 0)));
D.StopListening();
Directory.Delete(root, true);

// ---------- ViewModel (demo node) ----------
MainDashboardViewModel NewVm(INetworkNodeCoordinator net) => new(new MockRepositoryManager(),
    new IntelligentStorageEngine(@"C:\nope.zip"), net, new SpecializedFileHandler(), new InMemoryRecentRepositoryStore(), true);
async Task WaitFor(Func<bool> cond) { for (int i = 0; i < 80 && !cond(); i++) await Task.Delay(50); }

var mock = new MockNetworkNodeCoordinator();
var vm = NewVm(mock);
await vm.InitializeDashboardAsync(@"C:\Demo\Thesis");
Check("sharing off -> summary says turn it on", vm.PeersSummary.Contains("Turn on P2P sharing") && vm.ActivePeers.Count == 0);
vm.IsSharingEnabled = true;
var toggle = vm.ToggleSharingCommand.ExecuteAsync(null);
await WaitFor(() => vm.IsDialogOpen);
vm.ConfirmDialogCommand.Execute(null);
await toggle;
Check("switching on searches automatically", vm.ActivePeers.Count == 2 && vm.PeersSummary.StartsWith("2 computer"), vm.PeersSummary);
Check("no 'Has a copy' before sharing the repo", vm.ActivePeers.All(p => !p.HasCurrentRepository));

await vm.ShareRepositoryCommand.ExecuteAsync(null);
Check("share -> peers with a copy are marked", vm.ActivePeers.Count(p => p.HasCurrentRepository) == 1, vm.StatusText);
Check("share is logged with the repo key", vm.ActivityLog.Any(a => a.Text.Contains("(key rp-")));

await vm.ConnectPeerCommand.ExecuteAsync(vm.ActivePeers[1]);
Check("Connect -> peer shows connected", vm.ActivePeers.Any(p => p.IsConnected) && vm.StatusText.StartsWith("Connected to"), vm.StatusText);

vm.PeerAddress = "192.168.1.77";
await vm.AddPeerByAddressCommand.ExecuteAsync(null);
Check("add by address -> listed, box cleared", vm.ActivePeers.Count == 3 && vm.PeerAddress == "" && vm.ActivePeers.Any(p => p.IPAddress == "192.168.1.77"));
vm.PeerAddress = "";
await vm.AddPeerByAddressCommand.ExecuteAsync(null);
Check("empty address -> hint, nothing added", vm.ActivePeers.Count == 3 && vm.ToastMessage.Contains("IP address"));

vm.IsSharingEnabled = false;
await vm.ToggleSharingCommand.ExecuteAsync(null);
Check("switch off -> list empties", vm.ActivePeers.Count == 0 && !vm.HasPeers);
vm.IsSharingEnabled = true;
await vm.ToggleSharingCommand.ExecuteAsync(null);
Check("switch on again -> peers back, nobody connected", vm.ActivePeers.Count == 3 && vm.ActivePeers.All(p => !p.IsConnected));

await vm.InitializeDashboardAsync(@"C:\Demo\Other");
Check("same history in another folder -> still recognised as the same repository (Week 12)", vm.ActivePeers.Single(p => p.Title == "LAB-PC-02").HasCurrentRepository);

Console.WriteLine($"\n{pass} passed, {fail} failed.");

static class Helpers
{
    public static List<PeerNode>? DiscoverPeersSafe(this INetworkNodeCoordinator n)
    {
        try { return n.DiscoverPeers(); } catch (VentrailException) { return null; }
    }
    public static bool StopListeningAnd(this INetworkNodeCoordinator n, Func<bool> after) { n.StopListening(); return after(); }
}

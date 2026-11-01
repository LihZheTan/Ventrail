using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations.Dht;
using Ventrail.Services.Implementations.Transfer;

namespace Ventrail.Services.Implementations;

/// <summary>
/// P2P node (Fig 3.8).
/// Week 10: firewall audit + permission (FirewallAuditor) and local listening:
///  - TCP 4001 accepts connections and answers with a hello line ("VENTRAIL/1 &lt;peer id&gt;").
///    Week 12 replaces the hello with block streaming.
///  - UDP 4002 answers discovery and DHT messages (see DiscoveryMessage).
/// Week 11: peer discovery, in the two steps libp2p uses on a local network:
///  1. a LAN broadcast ("is any Ventrail here?") finds the first peers, like libp2p's mDNS;
///  2. a Kademlia DHT lookup asks those peers who they know, so peers that miss the broadcast are
///     still found, and "provider records" tell which peers hold a copy of a repository.
/// Only computers on the same local network are accepted, matching the firewall rules
/// (remoteip=localsubnet); nothing is sent to the internet or the public IPFS network.
/// </summary>
public partial class NetworkNodeCoordinator : INetworkNodeCoordinator, IDisposable
{
    public const int DefaultTcpPort = 4001;   // libp2p's common default port
    public const int DefaultUdpPort = 4002;
    public const string HelloPrefix = "VENTRAIL/1";
    public const string DiscoveryQuestion = DiscoveryMessage.Hello;
    public const string DiscoveryAnswer = DiscoveryMessage.HelloAnswer;

    // Kademlia's "alpha": how many peers a lookup asks at the same time.
    private const int Alpha = 3;
    private const int MaxLookupRounds = 6;
    // Week 13: waits adapt per peer (PeerLink). These are only the limits for the broadcast, where
    // nobody is known yet, and for asking an address typed by the user.
    private static readonly TimeSpan BroadcastWait = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MaxBroadcastWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AddressWait = TimeSpan.FromSeconds(4);

    // What has been learnt about each peer's connection (round-trip time, speed).
    private readonly ConcurrentDictionary<string, PeerLink> _links = new();

    private PeerLink Link(string peerId) => _links.GetOrAdd(peerId, _ => new PeerLink());

    /// <summary>
    /// For tests only: makes this computer answer like one on a slow or broken network
    /// (delay before every answer, limited sending speed, or no answers at all).
    /// </summary>
    public NetworkConditions? TestConditions { get; set; }

    // Windows reports "port unreachable" from an earlier send as an error on the next receive,
    // which would end the receive loop. This switches that off (SIO_UDP_CONNRESET).
    private const int SioUdpConnReset = -1744830452;

    private readonly IPAddress _bindAddress;
    private readonly IReadOnlyList<IPEndPoint>? _fixedTargets;
    private readonly object _gate = new();
    private readonly KademliaRoutingTable _table;
    private readonly ProviderStore _providers = new();
    private readonly Dictionary<string, Offer> _provided = new();   // repositories this computer offers
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _stop;

    public int LocalPort { get; }
    public int DiscoveryPort { get; }
    public string LocalPeerId { get; }
    public string DisplayName { get; }
    public List<PeerNode> ConnectedPeers { get; } = new();

    public event EventHandler? PeersChanged;

    // The exe the firewall rules are made for (the running Ventrail.exe).
    public string ExePath { get; }

    /// <param name="bindAddress">Any (default) = reachable from the network. Tests use Loopback,
    /// which Windows Firewall never asks about.</param>
    /// <param name="dataFolder">Where the peer id is kept (default %LocalAppData%\Ventrail).</param>
    /// <param name="discoveryTargets">Where the "is any Ventrail here?" question goes. Default: the
    /// broadcast address of every connected network. Tests list loopback ports instead.</param>
    public NetworkNodeCoordinator(IPAddress? bindAddress = null, int tcpPort = DefaultTcpPort,
                                  int udpPort = DefaultUdpPort, string? dataFolder = null, string? exePath = null,
                                  IEnumerable<IPEndPoint>? discoveryTargets = null, string? displayName = null)
    {
        _bindAddress = bindAddress ?? IPAddress.Any;
        LocalPort = tcpPort;
        DiscoveryPort = udpPort;
        ExePath = exePath ?? Environment.ProcessPath ?? string.Empty;
        _dataFolder = dataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ventrail");
        LocalPeerId = LoadOrCreatePeerId(_dataFolder);
        DisplayName = DiscoveryMessage.CleanName(displayName ?? Environment.MachineName);
        _fixedTargets = discoveryTargets?.ToList();
        _table = new KademliaRoutingTable(LocalPeerId);
    }

    private IReadOnlyList<(int Port, string Protocol)> Ports => new[] { (LocalPort, "TCP"), (DiscoveryPort, "UDP") };

    // ---------- Week 10: firewall ----------

    public FirewallStatus CheckFirewall() => FirewallAuditor.Audit(ExePath, Ports);

    public bool RequestFirewallPermission(bool includePublicNetworks)
    {
        var before = CheckFirewall();
        bool ownBlocks = before.Ports.Exists(p => p.BlockedByOwnRule);
        return FirewallAuditor.RunElevated(
            FirewallAuditor.BuildAllowScript(ExePath, Ports, includePublicNetworks, ownBlocks));
    }

    public bool RemoveFirewallPermission() => FirewallAuditor.RunElevated(FirewallAuditor.BuildRemoveScript(Ports));

    // ---------- Week 10: listening ----------

    public bool IsListening { get { lock (_gate) return _tcp != null; } }

    public bool StartListening()
    {
        lock (_gate)
        {
            if (_tcp != null) return true;
            TcpListener? tcp = null;
            UdpClient? udp = null;
            try
            {
                tcp = new TcpListener(_bindAddress, LocalPort);
                tcp.Start();
                udp = NewUdp(new IPEndPoint(_bindAddress, DiscoveryPort));
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                int port = tcp?.Server.IsBound == true ? DiscoveryPort : LocalPort;   // TCP worked, so UDP failed
                tcp?.Stop();
                udp?.Dispose();
                throw new VentrailException(
                    $"Port {port} is already used by another program (maybe Ventrail is open twice). " +
                    "Close the other program and try again.", ex);
            }

            _tcp = tcp;
            _udp = udp;
            _stop = new CancellationTokenSource();
            _ = AcceptLoopAsync(tcp, _stop.Token);
            _ = DiscoveryLoopAsync(udp, _stop.Token);
            return true;
        }
    }

    public void StopListening()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            _tcp?.Stop();
            _udp?.Dispose();
            _tcp = null;
            _udp = null;
            _stop = null;
            foreach (var p in ConnectedPeers) _table.Update(p.PeerId, n => n.IsConnected = false);
            ConnectedPeers.Clear();
        }
    }

    public void Dispose() => StopListening();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => ServeAsync(client, stop), stop);   // Week 12: hello, then requests
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }   // StopListening
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    // Answers other computers' discovery and DHT messages on the UDP port.
    private async Task DiscoveryLoopAsync(UdpClient udp, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(stop); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }

                if (TestConditions is { Stall: true }) continue;
                string? reply = HandleRequest(DiscoveryMessage.Parse(packet.Buffer), packet.RemoteEndPoint);
                if (reply is null) continue;
                byte[] data = Encoding.UTF8.GetBytes(reply);
                if (TestConditions is { Latency: var delay } && delay > TimeSpan.Zero)
                    _ = Task.Run(async () =>   // answer later without holding up other questions
                    {
                        try { await Task.Delay(delay, stop); await udp.SendAsync(data, packet.RemoteEndPoint, stop); }
                        catch (Exception) { }
                    }, stop);
                else
                    await udp.SendAsync(data, packet.RemoteEndPoint, stop);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    /// <summary>The answer to one incoming message, or null for none (unknown, malformed or not allowed).</summary>
    private string? HandleRequest(DiscoveryMessage? msg, IPEndPoint from)
    {
        if (msg is null || !IsLocalNetworkAddress(from.Address)) return null;
        var sender = msg.Sender;
        if (sender != null)
        {
            if (sender.PeerId == LocalPeerId) return null;   // our own broadcast coming back
            Learn(sender, from.Address);
        }

        switch (msg.Verb)
        {
            case DiscoveryMessage.Hello:
                return Header(DiscoveryMessage.HelloAnswer);

            case DiscoveryMessage.Find when msg.Args.Length >= 1 && DhtKey.IsValid(msg.Args[0]):
                return Header(DiscoveryMessage.Peers) + " " +
                       DiscoveryMessage.Entries(_table.Closest(msg.Args[0], KademliaRoutingTable.K, except: sender?.PeerId));

            case DiscoveryMessage.Provide when sender != null && msg.Args.Length >= 1 && IsRepoKey(msg.Args[0]):
                _providers.Add(msg.Args[0], WithAddress(sender, from.Address), DateTime.UtcNow);
                return null;

            case DiscoveryMessage.GetProviders when msg.Args.Length >= 1 && IsRepoKey(msg.Args[0]):
                string key = msg.Args[0];
                bool selfHas;
                lock (_gate) selfHas = _provided.ContainsKey(key);
                var known = _providers.Get(key, DateTime.UtcNow).Where(p => p.PeerId != sender?.PeerId);
                return $"{Header(DiscoveryMessage.Providers)} {key} {(selfHas ? 1 : 0)} " +
                       $"{DiscoveryMessage.Entries(known)} " +
                       DiscoveryMessage.Entries(_table.Closest(key, KademliaRoutingTable.K, except: sender?.PeerId));

            default:
                return null;
        }
    }

    // ---------- Week 11: discovery ----------

    public List<PeerNode> GetKnownPeers() => _table.All().Select(p =>
    {
        if (_links.TryGetValue(p.PeerId, out var link) && link.Rtt.Smoothed is { } rtt)
            p.RoundTripMs = (int)Math.Round(rtt.TotalMilliseconds);
        return p;
    }).ToList();

    // Long enough for the slowest peer heard from before, at least 1.5 s.
    private TimeSpan BroadcastWaitNow()
    {
        var slowest = _links.Values.Select(l => l.Rtt.Timeout).DefaultIfEmpty(TimeSpan.Zero).Max() * 1.5;
        return slowest < BroadcastWait ? BroadcastWait : slowest > MaxBroadcastWait ? MaxBroadcastWait : slowest;
    }

    /// <summary>
    /// Broadcasts to the local network, then looks up our own id in the DHT (Kademlia's way of
    /// joining: it fills the routing table with peers near us and tells them about us).
    /// Also re-announces the repositories this computer shares, since records expire.
    /// </summary>
    public List<PeerNode> DiscoverPeers()
    {
        RequireListening();
        Run(async () =>
        {
            await AskAsync(BroadcastTargets(), Header(DiscoveryMessage.Hello), DiscoveryMessage.HelloAnswer,
                           BroadcastWaitNow(), firstOnly: false);
            await LookupAsync(LocalPeerId);
            string[] keys;
            lock (_gate) keys = _provided.Keys.ToArray();
            foreach (string key in keys) await AnnounceAsync(key);
        });
        return GetKnownPeers();
    }

    /// <summary>
    /// For networks that block broadcasts: asks one address directly ("192.168.1.20" or
    /// "LAB-PC-02", optionally ":port" for a non-standard UDP port). Null if nobody answered.
    /// </summary>
    public PeerNode? AddPeerByAddress(string address)
    {
        RequireListening();
        string host = address.Trim();
        int port = DefaultUdpPort;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out int p) && p is > 0 and <= 65535)
            (host, port) = (host[..colon], p);
        if (host.Length == 0) throw new VentrailException("Type the other computer's IP address or name.");

        IPAddress? ip;
        if (!IPAddress.TryParse(host, out ip))
        {
            try { ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
            catch (SocketException) { ip = null; }
            if (ip is null) throw new VentrailException($"No computer called \"{host}\" was found on this network.");
        }
        if (!IsLocalNetworkAddress(ip))
            throw new VentrailException($"{ip} is not on your local network. Ventrail only shares with computers on the same network.");

        var replies = Run(() => AskAsync(new[] { new IPEndPoint(ip, port) }, Header(DiscoveryMessage.Hello),
                                         DiscoveryMessage.HelloAnswer, AddressWait, firstOnly: true));
        return replies.Count == 0 ? null : _table.Find(replies[0].Sender!.PeerId);
    }

    /// <summary>
    /// Announces in the DHT that this computer has a copy of the repository (provider record), and
    /// (Week 12) lets other computers download its versions over TCP.
    /// </summary>
    public void ProvideRepository(string repositoryKey, string repositoryPath, string name)
    {
        if (!IsRepoKey(repositoryKey)) throw new ArgumentException("Not a repository key", nameof(repositoryKey));
        RequireListening();
        lock (_gate) _provided[repositoryKey] = new Offer(repositoryPath, name);
        Run(() => AnnounceAsync(repositoryKey));
    }

    /// <summary>Peers that announced a copy of the repository (not including this computer).</summary>
    public List<PeerNode> FindProviders(string repositoryKey)
    {
        if (!IsRepoKey(repositoryKey)) throw new ArgumentException("Not a repository key", nameof(repositoryKey));
        RequireListening();
        return Run(async () =>
        {
            var found = new Dictionary<string, PeerNode>();
            void Add(PeerNode p) { if (p.PeerId != LocalPeerId) found[p.PeerId] = p; }
            foreach (var p in _providers.Get(repositoryKey, DateTime.UtcNow)) Add(p);

            var closest = await LookupAsync(repositoryKey);
            var answers = await Task.WhenAll(closest.Select(p =>
                QueryAsync(p, $"{Header(DiscoveryMessage.GetProviders)} {repositoryKey}", DiscoveryMessage.Providers)));
            foreach (var answer in answers)
            {
                if (answer is null || answer.Args.Length < 3 || answer.Args[0] != repositoryKey) continue;
                if (answer.Args[1] == "1") Add(answer.Sender!);
                foreach (var p in UsableEntries(answer.Args[2], answer.Sender!)) Add(p);
            }
            // Prefer the details in the routing table (fresher name/ports) where we have them.
            return found.Values.Select(p => _table.Find(p.PeerId) ?? p).ToList();
        });
    }

    /// <summary>
    /// Opens a TCP connection and checks the hello: the computer that answers must have the peer id
    /// discovery promised, so a wrong or reused address is noticed. Week 12 keeps this connection
    /// open and streams blocks over it.
    /// </summary>
    public bool EstablishConnection(string targetPeerId)
    {
        Run(async () =>
        {
            using var session = await OpenSessionAsync(targetPeerId, CancellationToken.None);
            await session.SendAsync("BYE", CancellationToken.None);
        });
        _table.Update(targetPeerId, p => p.IsConnected = true);
        lock (_gate)
        {
            ConnectedPeers.RemoveAll(p => p.PeerId == targetPeerId);
            ConnectedPeers.Add(_table.Find(targetPeerId)!);
        }
        PeersChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ---------- DHT internals ----------

    /// <summary>
    /// Kademlia iterative lookup: ask the closest known peers who they know near the target, then ask
    /// the closer ones they mention, until nobody closer turns up. Returns the closest peers that
    /// answered. Peers that never answer are dropped from the routing table.
    /// </summary>
    private async Task<List<PeerNode>> LookupAsync(string target)
    {
        ulong Dist(PeerNode p) => DhtKey.Distance(p.PeerId, target);
        var candidates = _table.Closest(target).ToDictionary(p => p.PeerId);
        var asked = new HashSet<string>();
        var answered = new Dictionary<string, PeerNode>();

        for (int round = 0; round < MaxLookupRounds; round++)
        {
            var batch = candidates.Values.OrderBy(Dist).Take(KademliaRoutingTable.K)
                                  .Where(p => !asked.Contains(p.PeerId)).Take(Alpha).ToList();
            if (batch.Count == 0) break;
            foreach (var p in batch) asked.Add(p.PeerId);

            var replies = await Task.WhenAll(batch.Select(p =>
                QueryAsync(p, $"{Header(DiscoveryMessage.Find)} {target}", DiscoveryMessage.Peers)));
            for (int i = 0; i < batch.Count; i++)
            {
                if (replies[i] is not { } reply)
                {
                    candidates.Remove(batch[i].PeerId);
                    // Week 13: only drop a peer after several lookups in a row got no answer, so a link that
                    // suddenly got slower keeps its peer while the waits adapt.
                    if (Link(batch[i].PeerId).MissedLookup() >= PeerLink.DropAfterMisses && _table.Remove(batch[i].PeerId))
                        PeersChanged?.Invoke(this, EventArgs.Empty);
                    continue;
                }
                Link(reply.Sender!.PeerId).AnsweredLookup();
                answered[reply.Sender.PeerId] = reply.Sender;
                foreach (var p in UsableEntries(reply.Args.FirstOrDefault(), reply.Sender))
                    candidates.TryAdd(p.PeerId, p);
            }
        }
        return answered.Values.OrderBy(Dist).Take(KademliaRoutingTable.K).ToList();
    }

    // Tells the K peers closest to the key that we have it.
    private async Task AnnounceAsync(string key)
    {
        var closest = await LookupAsync(key);
        if (closest.Count == 0) return;
        using var udp = NewUdp(new IPEndPoint(_bindAddress, 0));
        byte[] data = Encoding.UTF8.GetBytes($"{Header(DiscoveryMessage.Provide)} {key}");
        foreach (var p in closest)
        {
            try { await udp.SendAsync(data, new IPEndPoint(IPAddress.Parse(p.IPAddress), p.DiscoveryPort)); }
            catch (SocketException) { }
        }
    }

    // One question to one peer, asked twice before giving up (a single UDP packet can get lost).
    // The answer only counts if it comes from the peer id we asked.
    // Week 13: the wait is that peer's learnt round-trip timeout, doubled after each miss.
    private async Task<DiscoveryMessage?> QueryAsync(PeerNode peer, string message, string expectVerb)
    {
        var target = new IPEndPoint(IPAddress.Parse(peer.IPAddress), peer.DiscoveryPort);
        var link = Link(peer.PeerId);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var wait = link.Rtt.Timeout * (attempt + 1);   // second try: twice as patient
            var replies = await AskAsync(new[] { target }, message, expectVerb, wait, firstOnly: true);
            var reply = replies.FirstOrDefault(r => r.Sender!.PeerId == peer.PeerId);
            if (reply != null) return reply;
        }
        link.Rtt.Backoff();   // remembered for the next question to this peer
        return null;
    }

    /// <summary>Sends one message to the targets and collects the answers (each sender is learnt).</summary>
    private async Task<List<DiscoveryMessage>> AskAsync(IEnumerable<IPEndPoint> targets, string message,
                                                        string expectVerb, TimeSpan wait, bool firstOnly)
    {
        var replies = new List<DiscoveryMessage>();
        using var udp = NewUdp(new IPEndPoint(_bindAddress, 0));
        udp.EnableBroadcast = true;
        byte[] data = Encoding.UTF8.GetBytes(message);
        foreach (var target in targets)
        {
            try { await udp.SendAsync(data, target); }
            catch (SocketException) { }   // e.g. a network adapter that just went away
        }

        using var timeout = new CancellationTokenSource(wait);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                var msg = DiscoveryMessage.Parse(packet.Buffer);
                if (msg?.Sender is not { } sender || msg.Verb != expectVerb || sender.PeerId == LocalPeerId ||
                    !IsLocalNetworkAddress(packet.RemoteEndPoint.Address))
                    continue;
                Link(sender.PeerId).Rtt.Observe(clock.Elapsed);   // Week 13: every answer teaches the round-trip time
                sender.IPAddress = packet.RemoteEndPoint.Address.MapToIPv4().ToString();
                Learn(sender, packet.RemoteEndPoint.Address);
                replies.Add(msg);
                if (firstOnly) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        return replies;
    }

    // Peers listed in an answer that we may use: valid, not us, and on our local network. A loopback
    // address only makes sense if the peer that sent the list is on this computer too (tests).
    private IEnumerable<PeerNode> UsableEntries(string? entries, PeerNode from) =>
        DiscoveryMessage.ParseEntries(entries).Where(p =>
            p.PeerId != LocalPeerId &&
            IPAddress.TryParse(p.IPAddress, out var ip) && IsLocalNetworkAddress(ip) &&
            (!IPAddress.IsLoopback(ip) || IPAddress.IsLoopback(IPAddress.Parse(from.IPAddress))));

    private void Learn(PeerNode sender, IPAddress from)
    {
        if (_table.AddOrUpdate(WithAddress(sender, from), DateTime.UtcNow))
            PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    private static PeerNode WithAddress(PeerNode peer, IPAddress ip)
    {
        var copy = peer.Copy();
        copy.IPAddress = ip.MapToIPv4().ToString();
        return copy;
    }

    private string Header(string verb) => DiscoveryMessage.Header(verb, LocalPeerId, LocalPort, DiscoveryPort, DisplayName);

    private static bool IsRepoKey(string key) => DhtKey.IsValid(key) && key.StartsWith("rp-");

    private void RequireListening()
    {
        if (!IsListening) throw new VentrailException("Turn on P2P sharing first.");
    }

    // The interface is synchronous (the ViewModel already runs it in the background).
    private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
    private static void Run(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    private static UdpClient NewUdp(IPEndPoint bind)
    {
        var udp = new UdpClient(bind);
        if (OperatingSystem.IsWindows())
            udp.Client.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        return udp;
    }

    // The broadcast address of every connected IPv4 network, plus 255.255.255.255. (Windows only
    // sends 255.255.255.255 out of one adapter, so each network's own broadcast address is added.)
    private IReadOnlyList<IPEndPoint> BroadcastTargets() => _fixedTargets ?? DefaultBroadcastTargets(DiscoveryPort);

    public static IReadOnlyList<IPEndPoint> DefaultBroadcastTargets(int port)
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };
        foreach (var (address, mask) in LocalSubnets())
        {
            byte[] a = address.GetAddressBytes(), m = mask.GetAddressBytes();
            var broadcast = new IPAddress(a.Select((b, i) => (byte)(b | ~m[i])).ToArray());
            if (!targets.Any(t => t.Address.Equals(broadcast))) targets.Add(new IPEndPoint(broadcast, port));
        }
        return targets;
    }

    /// <summary>
    /// Same rule as the firewall's "localsubnet": this computer, or an address inside one of the
    /// networks this computer is connected to. Anything else is ignored.
    /// </summary>
    public static bool IsLocalNetworkAddress(IPAddress ip)
    {
        ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] x = ip.GetAddressBytes();
        foreach (var (address, mask) in LocalSubnets())
        {
            byte[] a = address.GetAddressBytes(), m = mask.GetAddressBytes();
            if (Enumerable.Range(0, 4).All(i => (x[i] & m[i]) == (a[i] & m[i]))) return true;
        }
        return false;
    }

    private static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalSubnets()
    {
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in adapters)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask is { } mask &&
                    !mask.Equals(IPAddress.Any))
                    yield return (u.Address, mask);
        }
    }

    // A short random id that stays the same between runs, so peers recognise this computer.
    private static string LoadOrCreatePeerId(string folder)
    {
        string file = Path.Combine(folder, "peer-id.txt");
        try
        {
            if (File.Exists(file))
            {
                string saved = File.ReadAllText(file).Trim();
                if (saved.StartsWith("vt-") && DhtKey.IsValid(saved)) return saved;
            }
            string id = "vt-" + Guid.NewGuid().ToString("N")[..12];
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, id);
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "vt-" + Guid.NewGuid().ToString("N")[..12];   // can't save: use one for this run
        }
    }
}

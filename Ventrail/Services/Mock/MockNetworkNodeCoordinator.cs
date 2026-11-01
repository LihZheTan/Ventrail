using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ventrail.Models;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Mock;

/// <summary>
/// Week 10: demo-mode network node. The firewall starts as "Needs permission" and "Allow" succeeds
/// straight away, so the permission flow (Fig 3.5) can be shown without an admin prompt and
/// without changing the PC's real firewall or opening real ports.
/// Week 11: discovery "finds" two made-up computers; the first already has a copy of any
/// repository that is shared, so the "Has a copy" badge can be shown.
/// </summary>
public class MockNetworkNodeCoordinator : INetworkNodeCoordinator
{
    private bool _allowed;
    private readonly List<PeerNode> _known = new();

    public int LocalPort => 4001;
    public int DiscoveryPort => 4002;
    public string LocalPeerId => "vt-demo0000000";
    public string DisplayName => "THIS-PC";
    public List<PeerNode> ConnectedPeers { get; } = new();
    public bool IsListening { get; private set; }

    public event EventHandler? PeersChanged;

    public FirewallStatus CheckFirewall() => new()
    {
        NetworkProfile = "Private",
        HasVentrailRules = _allowed,
        Ports = new()
        {
            Port(LocalPort, "TCP"),
            Port(DiscoveryPort, "UDP")
        }
    };

    private PortCheck Port(int port, string protocol) => new()
    {
        Port = port,
        Protocol = protocol,
        Access = _allowed ? PortAccess.Allowed : PortAccess.NeedsPermission,
        Detail = _allowed ? "Allowed (demo mode)." : "No firewall rule yet (demo mode)."
    };

    public bool RequestFirewallPermission(bool includePublicNetworks) => _allowed = true;

    public bool RemoveFirewallPermission()
    {
        _allowed = false;
        StopListening();
        return true;
    }

    public bool StartListening() => IsListening = true;

    public void StopListening()
    {
        IsListening = false;
        ConnectedPeers.Clear();
        foreach (var p in _known) p.IsConnected = false;
    }

    public List<PeerNode> GetKnownPeers() => _known.Select(p => p.Copy()).ToList();

    public List<PeerNode> DiscoverPeers()
    {
        Thread.Sleep(900);   // feels like a real search
        if (_known.Count == 0)
        {
            _known.Add(Peer("vt-a1b2c3d4e5f6", "LAB-PC-02", "192.168.1.24"));
            _known.Add(Peer("vt-0f9e8d7c6b5a", "SUPERVISOR-LAPTOP", "192.168.1.31"));
        }
        foreach (var p in _known) p.LastSeen = DateTime.UtcNow;
        return GetKnownPeers();
    }

    public PeerNode? AddPeerByAddress(string address)
    {
        Thread.Sleep(500);
        var existing = _known.FirstOrDefault(p => p.IPAddress == address.Trim());
        if (existing != null) return existing.Copy();
        var peer = Peer("vt-" + (0x1000000 + _known.Count).ToString("x12"), "DEMO-PC-" + (_known.Count + 1), address.Trim());
        _known.Add(peer);
        PeersChanged?.Invoke(this, EventArgs.Empty);
        return peer.Copy();
    }

    private readonly Dictionary<string, string> _shared = new();   // key -> name

    public void ProvideRepository(string repositoryKey, string repositoryPath, string name) => _shared[repositoryKey] = name;

    public List<PeerNode> FindProviders(string repositoryKey) => _known.Take(1).Select(p => p.Copy()).ToList();

    public bool EstablishConnection(string targetPeerId)
    {
        Thread.Sleep(600);
        var peer = _known.FirstOrDefault(p => p.PeerId == targetPeerId)
                   ?? throw new VentrailException("Ventrail no longer knows this computer. Search for peers again.");
        peer.IsConnected = true;
        ConnectedPeers.RemoveAll(p => p.PeerId == targetPeerId);
        ConnectedPeers.Add(peer.Copy());
        return true;
    }

    // ---------- Week 12 (demo): LAB-PC-02 has a newer version of whatever is shared here, and
    // both computers offer a "Group-Report" repository that can be downloaded as a new copy. ----------

    public const string GroupReportKey = "rp-00000000beef";
    private const int DemoBlocks = 8;

    public long BytesSent { get; private set; }

    public List<OfferedRepository> ListRepositories(string peerId)
    {
        Thread.Sleep(200);
        var peer = _known.FirstOrDefault(p => p.PeerId == peerId)
                   ?? throw new VentrailException("Ventrail no longer knows this computer. Search for peers again.");
        var list = new List<OfferedRepository>();
        if (peer == _known.FirstOrDefault())
            list.AddRange(_shared.Select(s => Offer(peer, s.Key, s.Value)));
        if (_known.IndexOf(peer) < 2)
            list.Add(Offer(peer, GroupReportKey, "Group-Report"));
        return list;
    }

    public TransferManifest RequestVersion(string peerId, string repositoryKey)
    {
        var offer = ListRepositories(peerId).FirstOrDefault(o => o.Key == repositoryKey)
                    ?? throw new VentrailException("This repository is not shared from that computer.");
        return new TransferManifest
        {
            PeerId = peerId, RepositoryKey = repositoryKey, Name = offer.Name, HeadHash = offer.HeadHash, Branch = "main",
            PackageHash = new string('d', 64), BlockSize = 1024 * 1024, Size = DemoBlocks * 1024L * 1024 - 300_000,
            BlockHashes = Enumerable.Range(0, DemoBlocks).Select(i => i.ToString("x64")).ToList()
        };
    }

    public string DownloadPackage(TransferManifest manifest, IProgress<TransferProgress>? progress, CancellationToken cancel)
    {
        for (int i = 1; i <= manifest.BlockCount; i++)
        {
            cancel.ThrowIfCancellationRequested();
            Thread.Sleep(250);   // feels like a real transfer
            progress?.Report(new TransferProgress
            {
                BlocksDone = i, BlockCount = manifest.BlockCount, TotalBytes = manifest.Size,
                BytesDone = Math.Min(manifest.Size, i * (long)manifest.BlockSize), BlocksRetried = i >= 5 ? 1 : 0,
                BytesPerSecond = 3.4 * 1024 * 1024
            });
        }
        return @"C:\Demo\inbox\" + manifest.PackageHash + ".vtpk";
    }

    public bool TransferData(string peerId, string dataHash) => true;

    // The demo peer's head is a fixed made-up version, so "Get latest" works once and then says "up to date".
    private static OfferedRepository Offer(PeerNode peer, string key, string name) => new()
    {
        PeerId = peer.PeerId, PeerName = peer.Title, Key = key, Name = name, Branch = "main",
        HeadHash = key == GroupReportKey ? new string('b', 40) : new string('a', 40)
    };

    private static PeerNode Peer(string id, string name, string ip) =>
        new() { PeerId = id, DisplayName = name, IPAddress = ip, Port = 4001, DiscoveryPort = 4002, LastSeen = DateTime.UtcNow,
                RoundTripMs = name.StartsWith("LAB") ? 4 : 23 };
}

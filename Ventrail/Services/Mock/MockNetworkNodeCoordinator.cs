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

    public void ProvideRepository(string repositoryKey) { }

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

    public bool TransferData(string peerId, string dataHash) =>
        throw new NotImplementedException("Planned for Week 12: data block streaming and hash verification.");

    private static PeerNode Peer(string id, string name, string ip) =>
        new() { PeerId = id, DisplayName = name, IPAddress = ip, Port = 4001, DiscoveryPort = 4002, LastSeen = DateTime.UtcNow };
}

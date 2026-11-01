using System;
using System.Collections.Generic;
using Ventrail.Models;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Mock;

/// <summary>
/// Week 10: demo-mode network node. The firewall starts as "Needs permission" and "Allow" succeeds
/// straight away, so the permission flow (Fig 3.5) can be shown without an admin prompt and
/// without changing the PC's real firewall or opening real ports.
/// </summary>
public class MockNetworkNodeCoordinator : INetworkNodeCoordinator
{
    private bool _allowed;

    public int LocalPort => 4001;
    public int DiscoveryPort => 4002;
    public string LocalPeerId => "vt-demo0000000";
    public List<PeerNode> ConnectedPeers { get; } = new();
    public bool IsListening { get; private set; }

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
        IsListening = false;
        return true;
    }

    public bool StartListening() => IsListening = true;
    public void StopListening() => IsListening = false;

    public List<PeerNode> DiscoverPeers() =>
        throw new NotImplementedException("Planned for Week 11: DHT / local broadcast peer discovery.");

    public bool EstablishConnection(string targetPeerId) =>
        throw new NotImplementedException("Planned for Week 12: secure socket connection.");

    public bool TransferData(string peerId, string dataHash) =>
        throw new NotImplementedException("Planned for Week 12: data block streaming and hash verification.");
}

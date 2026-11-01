using System.Collections.Generic;
using Ventrail.Models;

namespace Ventrail.Services.Contracts;

public interface INetworkNodeCoordinator
{
    int LocalPort { get; }        // TCP: version transfers (Week 12)
    int DiscoveryPort { get; }    // UDP: finding peers on the local network (Week 11)
    string LocalPeerId { get; }
    List<PeerNode> ConnectedPeers { get; }

    // Week 10: firewall audit and permission (Fig 3.5).
    FirewallStatus CheckFirewall();

    // Shows the Windows admin prompt and adds Ventrail's allow rules. False if the user says No
    // to the prompt. includePublicNetworks also allows sharing while on a "Public" network.
    bool RequestFirewallPermission(bool includePublicNetworks);

    // Removes Ventrail's allow rules (admin prompt). False if the user says No.
    bool RemoveFirewallPermission();

    // Week 10: open the two ports so other computers can reach this one.
    bool IsListening { get; }
    bool StartListening();
    void StopListening();

    List<PeerNode> DiscoverPeers();
    bool EstablishConnection(string targetPeerId);
    bool TransferData(string peerId, string dataHash);
}

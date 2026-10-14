using System;
using System.Collections.Generic;
using Ventrail.Models;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

public class NetworkNodeCoordinator : INetworkNodeCoordinator
{
    public int LocalPort { get; private set; } = 4001;   // libp2p's common default port
    public string LocalPeerId { get; private set; } = string.Empty;
    public List<PeerNode> ConnectedPeers { get; } = new();

    public bool StartListening() =>
        throw new NotImplementedException("Planned for Week 10: firewall check and local node listening.");

    public List<PeerNode> DiscoverPeers() =>
        throw new NotImplementedException("Planned for Week 11: DHT / local broadcast peer discovery.");

    public bool EstablishConnection(string targetPeerId) =>
        throw new NotImplementedException("Planned for Week 12: secure socket connection.");

    public bool TransferData(string peerId, string dataHash) =>
        throw new NotImplementedException("Planned for Week 12: data block streaming and hash verification.");
}

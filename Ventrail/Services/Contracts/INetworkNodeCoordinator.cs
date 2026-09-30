using System.Collections.Generic;
using Ventrail.Models;

namespace Ventrail.Services.Contracts;

public interface INetworkNodeCoordinator
{
    int LocalPort { get; }
    string LocalPeerId { get; }
    List<PeerNode> ConnectedPeers { get; }

    bool StartListening();
    List<PeerNode> DiscoverPeers();
    bool EstablishConnection(string targetPeerId);
    bool TransferData(string peerId, string dataHash);
}
using System;
using System.Collections.Generic;
using System.Threading;
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

    // Week 11: peer discovery (LAN broadcast + Kademlia DHT). All need listening to be on.
    // Raised (on a background thread) when a peer appears, disappears or connects.
    event EventHandler? PeersChanged;
    string DisplayName { get; }                     // this computer's name as other peers see it
    List<PeerNode> GetKnownPeers();                 // routing table snapshot, most recently seen first
    List<PeerNode> DiscoverPeers();                 // broadcast + DHT lookup, then GetKnownPeers()
    PeerNode? AddPeerByAddress(string address);     // for networks that block broadcasts
    // "This computer has a copy" (DhtKey.ForRepository); Week 12: also lets others download it.
    void ProvideRepository(string repositoryKey, string repositoryPath, string name);
    List<PeerNode> FindProviders(string repositoryKey);

    // Week 11: TCP hello check (the peer id must match). Week 12 streams blocks over it.
    bool EstablishConnection(string targetPeerId);

    // Week 12: block streaming with SHA-256 checks (TCP).
    List<OfferedRepository> ListRepositories(string peerId);              // what a peer shares
    TransferManifest RequestVersion(string peerId, string repositoryKey);  // sizes + block fingerprints
    // Downloads and verifies every block; returns the verified package file. Resumes if interrupted.
    string DownloadPackage(TransferManifest manifest, IProgress<TransferProgress>? progress, CancellationToken cancel);
    bool TransferData(string peerId, string dataHash);                     // class diagram: download a requested package
    long BytesSent { get; }
}

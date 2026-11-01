using System;

namespace Ventrail.Models;

public class PeerNode
{
    public string PeerId { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public int Port { get; set; }                  // TCP: version transfers (Week 12)
    public bool IsConnected { get; set; }

    // Week 11: discovery details.
    public int DiscoveryPort { get; set; }         // UDP: where this peer answers DHT questions
    public string DisplayName { get; set; } = string.Empty;   // computer name, e.g. "LAB-PC-02"
    public DateTime LastSeen { get; set; }

    // Set by the Network page: this peer already has a copy of the open repository.
    public bool HasCurrentRepository { get; set; }

    public string Title => string.IsNullOrEmpty(DisplayName) ? PeerId : DisplayName;

    public PeerNode Copy() => (PeerNode)MemberwiseClone();
}

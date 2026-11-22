using System;
using System.Collections.Generic;

namespace Ventrail.Models;

public class PeerNode
{
    public string PeerId { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public int Port { get; set; }                  // TCP: version transfers
    public bool IsConnected { get; set; }

    // Discovery details.
    public int DiscoveryPort { get; set; }         // UDP: where this peer answers DHT questions
    public string DisplayName { get; set; } = string.Empty;   // computer name, e.g. "LAB-PC-02"
    public DateTime LastSeen { get; set; }

    // Smoothed round-trip time to this peer (null until it has answered).
    public int? RoundTripMs { get; set; }
    public string RoundTripText => RoundTripMs is { } ms ? $"· {ms} ms" : string.Empty;

    // Set by the Network page: this peer already has a copy of the open repository.
    public bool HasCurrentRepository { get; set; }

    // Repositories this peer offers for download (its "LIST" answer).
    public List<OfferedRepository> Repositories { get; set; } = new();
    public bool HasRepositories => Repositories.Count > 0;

    public string Title => string.IsNullOrEmpty(DisplayName) ? PeerId : DisplayName;

    public PeerNode Copy()
    {
        return (PeerNode)MemberwiseClone();
    }
}

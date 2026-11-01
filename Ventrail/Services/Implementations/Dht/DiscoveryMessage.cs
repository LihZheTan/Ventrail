using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Ventrail.Models;

namespace Ventrail.Services.Implementations.Dht;

/// <summary>
/// Week 11: the UDP messages on port 4002. One line of plain text per packet, so they are easy to
/// read in a packet capture and in tests. Every message starts with the sender's details, so the
/// receiver can add the sender to its routing table (as Kademlia does):
///
///   VERB &lt;peer id&gt; &lt;tcp port&gt; &lt;udp port&gt; &lt;computer name&gt; [arguments]
///
///   VENTRAIL?           "is any Ventrail here?" (LAN broadcast)  → VENTRAIL!
///   VENTRAIL-FIND       target id: "who do you know close to it?" → VENTRAIL-PEERS entries
///   VENTRAIL-PROVIDE    repo key: "I have a copy of this repository" (no answer)
///   VENTRAIL-GETPROV    repo key: "who has it?"                   → VENTRAIL-PROVIDERS key providers closer
///
/// Entries are "id@ip:tcp:udp:name" separated by commas, or "-" for none. The bare Week 10 question
/// "VENTRAIL?" is still understood, and the answer still begins "VENTRAIL! &lt;id&gt; &lt;tcp port&gt;".
/// Anything malformed is ignored (Parse returns null).
/// </summary>
public sealed class DiscoveryMessage
{
    public const string Hello = "VENTRAIL?";
    public const string HelloAnswer = "VENTRAIL!";
    public const string Find = "VENTRAIL-FIND";
    public const string Peers = "VENTRAIL-PEERS";
    public const string Provide = "VENTRAIL-PROVIDE";
    public const string GetProviders = "VENTRAIL-GETPROV";
    public const string Providers = "VENTRAIL-PROVIDERS";
    public const int MaxBytes = 1400;          // fits in one Ethernet packet
    public const int MaxEntries = 10;

    private static readonly string[] Verbs = { Hello, HelloAnswer, Find, Peers, Provide, GetProviders, Providers };

    public string Verb { get; init; } = string.Empty;
    public PeerNode? Sender { get; init; }                  // null only for the bare Week 10 "VENTRAIL?"
    public string[] Args { get; init; } = Array.Empty<string>();

    // ---------- writing ----------

    public static string Header(string verb, string peerId, int tcpPort, int udpPort, string name) =>
        $"{verb} {peerId} {tcpPort} {udpPort} {CleanName(name)}";

    public static string Entries(IEnumerable<PeerNode> peers)
    {
        var list = peers.Take(MaxEntries)
                        .Select(p => $"{p.PeerId}@{p.IPAddress}:{p.Port}:{p.DiscoveryPort}:{CleanName(p.DisplayName)}")
                        .ToList();
        return list.Count == 0 ? "-" : string.Join(",", list);
    }

    // Names travel as one word: letters, digits, '-' and '_' only, at most 32 characters.
    public static string CleanName(string? name)
    {
        string clean = Regex.Replace((name ?? "").Replace(' ', '_'), "[^A-Za-z0-9_-]", "");
        if (clean.Length > 32) clean = clean[..32];
        return clean.Length == 0 ? "-" : clean;
    }

    // ---------- reading ----------

    public static DiscoveryMessage? Parse(byte[] packet)
    {
        if (packet.Length == 0 || packet.Length > MaxBytes) return null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(packet).Trim(); }
        catch (ArgumentException) { return null; }   // not valid UTF-8
        return Parse(text);
    }

    public static DiscoveryMessage? Parse(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Verbs.Contains(parts[0])) return null;
        if (parts.Length == 1) return parts[0] == Hello ? new DiscoveryMessage { Verb = Hello } : null;
        if (parts.Length < 5 || !DhtKey.IsValid(parts[1]) || !parts[1].StartsWith("vt-") ||
            !TryPort(parts[2], out int tcp) || !TryPort(parts[3], out int udp))
            return null;

        return new DiscoveryMessage
        {
            Verb = parts[0],
            Sender = new PeerNode { PeerId = parts[1], Port = tcp, DiscoveryPort = udp, DisplayName = NameOrEmpty(parts[4]) },
            Args = parts[5..]
        };
    }

    /// <summary>Reads "id@ip:tcp:udp:name,..." ("-" = none). Bad entries are skipped.</summary>
    public static List<PeerNode> ParseEntries(string? text)
    {
        var peers = new List<PeerNode>();
        if (string.IsNullOrEmpty(text) || text == "-") return peers;
        foreach (string entry in text.Split(',').Take(MaxEntries))
        {
            int at = entry.IndexOf('@');
            if (at < 0) continue;
            string id = entry[..at];
            string[] rest = entry[(at + 1)..].Split(':');
            if (rest.Length != 4 || !DhtKey.IsValid(id) || !id.StartsWith("vt-") ||
                !System.Net.IPAddress.TryParse(rest[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                !TryPort(rest[1], out int tcp) || !TryPort(rest[2], out int udp))
                continue;
            peers.Add(new PeerNode { PeerId = id, IPAddress = ip.ToString(), Port = tcp, DiscoveryPort = udp, DisplayName = NameOrEmpty(rest[3]) });
        }
        return peers;
    }

    private static bool TryPort(string s, out int port) => int.TryParse(s, out port) && port is > 0 and <= 65535;

    private static string NameOrEmpty(string s) => s == "-" ? string.Empty : CleanName(s);
}

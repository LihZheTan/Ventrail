using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ventrail.Models;

namespace Ventrail.Services.Implementations.Dht;

/// <summary>
/// Ids in Ventrail's DHT: peers are "vt-" + 12 hex digits (Week 10) and repositories are
/// "rp-" + 12 hex digits, so both live in the same 48-bit space and can be compared by XOR
/// distance, as in Kademlia (the DHT used by libp2p and IPFS).
/// </summary>
public static class DhtKey
{
    public const int Bits = 48;

    public static bool IsValid(string? id) =>
        id is { Length: 15 } && (id.StartsWith("vt-") || id.StartsWith("rp-")) &&
        id.Skip(3).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static ulong Value(string id) => Convert.ToUInt64(id[3..], 16);

    public static ulong Distance(string a, string b) => Value(a) ^ Value(b);

    /// <summary>
    /// The DHT key for a repository: a hash of its very first version. Every copy of a repository
    /// shares that first version, so all copies get the same key, and the key reveals nothing about
    /// the folder name or files.
    /// </summary>
    public static string ForRepository(string rootCommitHash)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("ventrail-repo:" + rootCommitHash.ToLowerInvariant()));
        return "rp-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}

/// <summary>
/// Kademlia routing table: known peers sorted into 48 "buckets" by how many leading bits their id
/// shares with ours. Each bucket keeps at most K peers, so the table stays small however many
/// computers are on the network, while still knowing someone "close" to any id.
/// Pure logic (no sockets) so it can be tested directly.
/// </summary>
public class KademliaRoutingTable
{
    public const int K = 8;                                   // bucket size and lookup width
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly List<PeerNode>[] _buckets = Enumerable.Range(0, DhtKey.Bits).Select(_ => new List<PeerNode>()).ToArray();

    public string LocalId { get; }

    public KademliaRoutingTable(string localId) => LocalId = localId;

    // 0 = differs only in the last bit (closest), 47 = differs in the first bit (furthest).
    public static int BucketIndex(string localId, string otherId)
    {
        ulong distance = DhtKey.Distance(localId, otherId);
        return distance == 0 ? -1 : 63 - System.Numerics.BitOperations.LeadingZeroCount(distance);
    }

    public int Count { get { lock (_gate) return _buckets.Sum(b => b.Count); } }

    /// <summary>
    /// Adds or refreshes a peer (newest at the end of its bucket). Returns true if the peer is new.
    /// A full bucket only makes room by dropping a peer that has been silent for a long time:
    /// Kademlia prefers peers that have stayed online.
    /// </summary>
    public bool AddOrUpdate(PeerNode peer, DateTime now)
    {
        if (!DhtKey.IsValid(peer.PeerId) || peer.PeerId == LocalId) return false;
        lock (_gate)
        {
            var bucket = _buckets[BucketIndex(LocalId, peer.PeerId)];
            int existing = bucket.FindIndex(p => p.PeerId == peer.PeerId);
            var entry = peer.Copy();
            entry.LastSeen = now;
            if (existing >= 0)
            {
                // Keep a name learnt earlier if this message didn't carry one.
                if (string.IsNullOrEmpty(entry.DisplayName)) entry.DisplayName = bucket[existing].DisplayName;
                entry.IsConnected = bucket[existing].IsConnected;
                bucket.RemoveAt(existing);
                bucket.Add(entry);
                return false;
            }
            if (bucket.Count >= K)
            {
                if (now - bucket[0].LastSeen < StaleAfter) return false;   // all still active: ignore newcomer
                bucket.RemoveAt(0);
            }
            bucket.Add(entry);
            return true;
        }
    }

    public bool Remove(string peerId)
    {
        if (!DhtKey.IsValid(peerId) || peerId == LocalId) return false;
        lock (_gate) return _buckets[BucketIndex(LocalId, peerId)].RemoveAll(p => p.PeerId == peerId) > 0;
    }

    public PeerNode? Find(string peerId)
    {
        if (!DhtKey.IsValid(peerId) || peerId == LocalId) return null;
        lock (_gate) return _buckets[BucketIndex(LocalId, peerId)].FirstOrDefault(p => p.PeerId == peerId)?.Copy();
    }

    public void Update(string peerId, Action<PeerNode> change)
    {
        if (!DhtKey.IsValid(peerId) || peerId == LocalId) return;
        lock (_gate)
        {
            var peer = _buckets[BucketIndex(LocalId, peerId)].FirstOrDefault(p => p.PeerId == peerId);
            if (peer != null) change(peer);
        }
    }

    /// <summary>The <paramref name="count"/> known peers closest to <paramref name="target"/> (by XOR).</summary>
    public List<PeerNode> Closest(string target, int count = K, string? except = null)
    {
        lock (_gate)
            return _buckets.SelectMany(b => b)
                           .Where(p => p.PeerId != except)
                           .OrderBy(p => DhtKey.Distance(p.PeerId, target))
                           .Take(count)
                           .Select(p => p.Copy())
                           .ToList();
    }

    public List<PeerNode> All()
    {
        lock (_gate) return _buckets.SelectMany(b => b).OrderByDescending(p => p.LastSeen).Select(p => p.Copy()).ToList();
    }
}

/// <summary>
/// Provider records (IPFS "content routing"): which peers have announced that they hold a copy of a
/// repository key. Records expire, so a computer that goes offline disappears on its own.
/// </summary>
public class ProviderStore
{
    public static readonly TimeSpan RecordLifetime = TimeSpan.FromMinutes(30);
    public const int MaxProvidersPerKey = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, (PeerNode Peer, DateTime Expires)>> _records = new();

    public void Add(string key, PeerNode provider, DateTime now)
    {
        if (!DhtKey.IsValid(key) || !DhtKey.IsValid(provider.PeerId)) return;
        lock (_gate)
        {
            if (!_records.TryGetValue(key, out var providers)) _records[key] = providers = new();
            providers[provider.PeerId] = (provider.Copy(), now + RecordLifetime);
            if (providers.Count > MaxProvidersPerKey)   // keep the most recently announced
                providers.Remove(providers.MinBy(p => p.Value.Expires).Key);
        }
    }

    public List<PeerNode> Get(string key, DateTime now)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(key, out var providers)) return new();
            foreach (var expired in providers.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList())
                providers.Remove(expired);
            return providers.Values.Select(p => p.Peer.Copy()).ToList();
        }
    }
}

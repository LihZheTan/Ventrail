using System;
using System.Threading;

namespace Ventrail.Services.Implementations.Transfer;

/// <summary>
/// Week 13: how long to wait for an answer, learnt from how long answers actually take.
/// Same method TCP itself uses (Jacobson/Karels, RFC 6298): keep a smoothed round-trip time (SRTT)
/// and how much it varies (RTTVAR); wait SRTT + 4 x RTTVAR. When a wait runs out, the next wait is
/// doubled (back-off), so a slow link gets more patience and a fast one gives up on a dead peer quickly.
/// Pure logic, so it can be tested without a network.
/// </summary>
public class RttEstimator
{
    private readonly object _gate = new();
    private double? _srtt, _rttvar;   // milliseconds
    private double _rto;

    public TimeSpan Min { get; }
    public TimeSpan Max { get; }

    public RttEstimator(TimeSpan initial, TimeSpan min, TimeSpan max)
    {
        Min = min;
        Max = max;
        _rto = initial.TotalMilliseconds;
    }

    /// <summary>How long to wait for the next answer.</summary>
    public TimeSpan Timeout { get { lock (_gate) return TimeSpan.FromMilliseconds(_rto); } }

    /// <summary>Smoothed round-trip time, or null before the first answer.</summary>
    public TimeSpan? Smoothed { get { lock (_gate) return _srtt is { } s ? TimeSpan.FromMilliseconds(s) : null; } }

    public int Samples { get; private set; }

    public void Observe(TimeSpan sample)
    {
        double r = Math.Max(1, sample.TotalMilliseconds);
        lock (_gate)
        {
            if (_srtt is null)
            {
                _srtt = r;
                _rttvar = r / 2;
            }
            else
            {
                _rttvar = 0.75 * _rttvar!.Value + 0.25 * Math.Abs(_srtt.Value - r);
                _srtt = 0.875 * _srtt.Value + 0.125 * r;
            }
            _rto = Clamp(_srtt.Value + 4 * _rttvar!.Value);
            Samples++;
        }
    }

    /// <summary>A wait ran out: be twice as patient next time (until a real answer arrives).</summary>
    public void Backoff()
    {
        lock (_gate) _rto = Clamp(_rto * 2);
    }

    private double Clamp(double ms) => Math.Clamp(ms, Min.TotalMilliseconds, Max.TotalMilliseconds);
}

/// <summary>
/// Week 13: how fast blocks arrive from one peer (bytes per second, smoothed), used to decide how long
/// one 1 MB block may take before the connection is treated as stuck.
/// </summary>
public class ThroughputEstimator
{
    private readonly object _gate = new();
    private double? _bytesPerSecond;

    public double? BytesPerSecond { get { lock (_gate) return _bytesPerSecond; } }

    public void Observe(long bytes, TimeSpan elapsed)
    {
        if (bytes <= 0) return;
        double rate = bytes / Math.Max(0.001, elapsed.TotalSeconds);
        lock (_gate) _bytesPerSecond = _bytesPerSecond is { } old ? 0.7 * old + 0.3 * rate : rate;
    }

    /// <summary>
    /// A block didn't arrive within <paramref name="waited"/>, so the link is slower than
    /// bytes/waited. Drop the estimate to half of that at once (like TCP's multiplicative decrease)
    /// instead of averaging down slowly over many timeouts.
    /// </summary>
    public void TimedOut(long bytes, TimeSpan waited)
    {
        double ceiling = bytes / Math.Max(0.001, waited.TotalSeconds) / 2;
        lock (_gate) _bytesPerSecond = _bytesPerSecond is { } old ? Math.Min(old, ceiling) : ceiling;
    }

    /// <summary>
    /// Time allowed for <paramref name="bytes"/>: three times the expected time plus the round-trip
    /// wait, between <paramref name="min"/> and <paramref name="max"/>. Before any block has arrived,
    /// <paramref name="unknown"/>.
    /// </summary>
    public TimeSpan TimeoutFor(long bytes, TimeSpan roundTrip, TimeSpan min, TimeSpan max, TimeSpan unknown)
    {
        double? rate = BytesPerSecond;
        if (rate is null) return unknown;
        double ms = roundTrip.TotalMilliseconds + 3 * 1000 * bytes / rate.Value;
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, min.TotalMilliseconds, max.TotalMilliseconds));
    }
}

/// <summary>What Ventrail has learnt about the connection to one peer.</summary>
public class PeerLink
{
    // UDP questions: start at 0.8 s (the Week 11 value), never below 0.25 s (a busy PC needs a
    // moment to answer even on a fast network) or above 8 s.
    public RttEstimator Rtt { get; } = new(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(8));
    public ThroughputEstimator Throughput { get; } = new();

    // Lookups in a row this peer didn't answer; it leaves the routing table after DropAfterMisses.
    public const int DropAfterMisses = 3;
    private int _missedLookups;
    public int MissedLookup() => Interlocked.Increment(ref _missedLookups);
    public void AnsweredLookup() => Interlocked.Exchange(ref _missedLookups, 0);

    // Connecting takes about two round trips (handshake + hello), plus a second for a busy PC.
    public TimeSpan ConnectTimeout => Clamp(Rtt.Timeout * 2 + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));

    // Waiting for one line (LIST answer, manifest line): the sender says "WAIT" every 2 s while busy.
    public TimeSpan LineTimeout => Clamp(TimeSpan.FromSeconds(2) + Rtt.Timeout * 4, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(40));

    // One 1 MB block: 30 s (the Week 12 value) until the speed is known, then 2-120 s.
    public TimeSpan BlockTimeout(long bytes) =>
        Throughput.TimeoutFor(bytes, Rtt.Timeout, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30));

    private static TimeSpan Clamp(TimeSpan t, TimeSpan min, TimeSpan max) => t < min ? min : t > max ? max : t;
}

/// <summary>
/// Week 13, tests only: makes a node behave like one on a poor network, to check the adaptive waits
/// end to end. Applied by the answering side.
/// </summary>
public class NetworkConditions
{
    public TimeSpan Latency { get; init; }        // delay before every answer (UDP and TCP)
    public long BytesPerSecond { get; init; }     // 0 = unlimited; otherwise block data is sent this slowly
    public bool Stall { get; set; }               // stop answering, but keep connections open (a "frozen" peer)
    public TimeSpan ExtraPackingTime { get; init; }   // pretend packing the repository takes this much longer
}

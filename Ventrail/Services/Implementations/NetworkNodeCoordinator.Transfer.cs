using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Ventrail.Models;
using TransferProgress = Ventrail.Models.TransferProgress;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations.Dht;
using Ventrail.Services.Implementations.Transfer;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Week 12: sending versions between computers over TCP 4001 (proposal: sockets, block streaming,
/// hash verification). After the hello line the receiving computer sends one request per line:
///
///   LIST                     → REPOS n, then n lines "key head branch name"
///   MANIFEST &lt;repo key&gt;      → MANIFEST hash size blockSize blockCount head branch name,
///                              then one SHA-256 per 1 MB block
///   BLOCK &lt;hash&gt; &lt;index&gt;     → DATA index length, then exactly that many bytes
///   BYE
///
/// Any problem is answered with "ERROR &lt;message&gt;". The receiver checks every block against the
/// manifest as it arrives (a damaged block is asked for again), keeps finished blocks on disk so an
/// interrupted download resumes, checks the whole package at the end, and Git then checks every
/// version inside it (RepositoryManager.ImportVersionPackage).
/// </summary>
public partial class NetworkNodeCoordinator
{
    private const long MaxPackageBytes = 16L * 1024 * 1024 * 1024;
    private const int MaxBlockAttempts = 3;
    // Fixed for now; Week 13 makes these adaptive.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ManifestWait = TimeSpan.FromMinutes(3);   // the sender may be packing a big repository
    private static readonly TimeSpan BlockWait = TimeSpan.FromSeconds(30);

    private record Offer(string Path, string Name);
    private record CachedPackage(PackageInfo Info, List<string> BlockHashes);

    private readonly string _dataFolder;
    private readonly Dictionary<string, CachedPackage> _packages = new();        // by repository key
    private readonly Dictionary<string, TransferManifest> _manifests = new();    // by package hash (receiving side)
    private readonly SemaphoreSlim _packing = new(1, 1);
    private long _bytesSent;

    /// <summary>Bytes of version data this computer has sent to others since it started.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>
    /// For tests only: lets a test damage blocks on their way out, to prove the receiver notices.
    /// Gets the block index and its bytes, returns the bytes to send.
    /// </summary>
    public Func<int, byte[], byte[]>? TestBlockFilter { get; set; }

    private string Outbox => Path.Combine(_dataFolder, "outbox");
    private string Inbox => Path.Combine(_dataFolder, "inbox");

    // ---------- receiving side ----------

    /// <summary>The repositories a peer currently shares, with its latest version of each.</summary>
    public List<OfferedRepository> ListRepositories(string peerId) => Run(async () =>
    {
        using var timeout = new CancellationTokenSource(ConnectWait + QueryWait * 4);
        using var session = await OpenSessionAsync(peerId, timeout.Token);
        await session.SendAsync("LIST", timeout.Token);
        string[] head = Words(await session.ReadLineAsync(timeout.Token));
        if (head.Length != 2 || head[0] != "REPOS" || !int.TryParse(head[1], out int count) || count is < 0 or > 100)
            throw ProtocolError(session.Peer, head);

        var list = new List<OfferedRepository>();
        for (int i = 0; i < count; i++)
        {
            string[] w = Words(await session.ReadLineAsync(timeout.Token), 4);
            if (w.Length == 4 && DhtKey.IsValid(w[0]) && w[0].StartsWith("rp-") && IsCommitHash(w[1]))
                list.Add(new OfferedRepository
                {
                    PeerId = peerId, PeerName = session.Peer.Title, Key = w[0], HeadHash = w[1], Branch = w[2], Name = w[3]
                });
        }
        await session.SendAsync("BYE", timeout.Token);
        return list;
    });

    /// <summary>Asks the peer to prepare its latest version of a repository and describe it (no data yet).</summary>
    public TransferManifest RequestVersion(string peerId, string repositoryKey) => Run(async () =>
    {
        using var timeout = new CancellationTokenSource(ManifestWait);
        using var session = await OpenSessionAsync(peerId, timeout.Token);
        await session.SendAsync($"MANIFEST {repositoryKey}", timeout.Token);
        string[] w = Words(await session.ReadLineAsync(timeout.Token), 8);
        if (w.Length >= 2 && w[0] == "ERROR")
            throw new VentrailException($"{session.Peer.Title}: {string.Join(' ', w[1..])}");
        if (w.Length != 8 || w[0] != "MANIFEST" || !BlockHashing.IsHash(w[1]) ||
            !long.TryParse(w[2], out long size) || size <= 0 || size > MaxPackageBytes ||
            !int.TryParse(w[3], out int blockSize) || blockSize != BlockHashing.BlockSize ||
            !int.TryParse(w[4], out int blocks) || blocks != (int)((size + blockSize - 1) / blockSize) ||
            !IsCommitHash(w[5]))
            throw ProtocolError(session.Peer, w);

        var manifest = new TransferManifest
        {
            PeerId = peerId, RepositoryKey = repositoryKey, PackageHash = w[1], Size = size, BlockSize = blockSize,
            HeadHash = w[5], Branch = w[6], Name = w[7]
        };
        for (int i = 0; i < blocks; i++)
        {
            string? line = (await session.ReadLineAsync(timeout.Token))?.Trim();
            if (!BlockHashing.IsHash(line)) throw ProtocolError(session.Peer, new[] { line ?? "" });
            manifest.BlockHashes.Add(line!);
        }
        await session.SendAsync("BYE", timeout.Token);
        lock (_gate) _manifests[manifest.PackageHash] = manifest;
        return manifest;
    });

    /// <summary>Class diagram: TransferData(peerId, dataHash): downloads a package described earlier by RequestVersion.</summary>
    public bool TransferData(string peerId, string dataHash)
    {
        TransferManifest? manifest;
        lock (_gate) _manifests.TryGetValue(dataHash, out manifest);
        if (manifest is null || manifest.PeerId != peerId)
            throw new VentrailException("Ask that computer for its latest version first.");
        DownloadPackage(manifest, null, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Streams the package block by block and returns the path of the verified file (in the inbox).
    /// Blocks already on disk from an interrupted download are checked and kept.
    /// </summary>
    public string DownloadPackage(TransferManifest manifest, IProgress<TransferProgress>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Inbox);
        string final = Path.Combine(Inbox, manifest.PackageHash + ".vtpk");
        if (File.Exists(final) && BlockHashing.FileSha256(final) == manifest.PackageHash) return final;
        string part = final + ".part";

        return Run(async () =>
        {
            int count = manifest.BlockCount, done = 0, resumed = 0, retried = 0;
            long bytesDone = 0;
            var missing = new List<int>();
            using (var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                bool resuming = file.Length == manifest.Size;
                file.SetLength(manifest.Size);
                var buffer = new byte[manifest.BlockSize];
                for (int i = 0; i < count; i++)
                {
                    int length = BlockLength(manifest, i);
                    if (resuming)
                    {
                        file.Position = (long)i * manifest.BlockSize;
                        if (BlockHashing.ReadFull(file, buffer) >= length &&
                            BlockHashing.Sha256(buffer.AsSpan(0, length)) == manifest.BlockHashes[i])
                        {
                            done++; resumed++; bytesDone += length;
                            continue;
                        }
                    }
                    missing.Add(i);
                }
                Report();

                Session? session = null;
                try
                {
                    foreach (int i in missing)
                    {
                        int length = BlockLength(manifest, i);
                        for (int attempt = 1; ; attempt++)
                        {
                            cancel.ThrowIfCancellationRequested();
                            try
                            {
                                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                                timeout.CancelAfter(BlockWait);
                                session ??= await OpenSessionAsync(manifest.PeerId, timeout.Token);
                                byte[]? data = await FetchBlockAsync(session, manifest.PackageHash, i, length, timeout.Token);
                                if (data != null && BlockHashing.Sha256(data) == manifest.BlockHashes[i])
                                {
                                    file.Position = (long)i * manifest.BlockSize;
                                    await file.WriteAsync(data, cancel);
                                    break;
                                }
                            }
                            catch (Exception ex) when (ex is IOException or SocketException ||
                                                       ex is OperationCanceledException && !cancel.IsCancellationRequested)
                            {
                                session?.Dispose();   // broken or silent connection: open a new one
                                session = null;
                            }
                            retried++;
                            if (attempt >= MaxBlockAttempts)
                                throw new VentrailException(
                                    $"Block {i + 1} of {count} kept arriving damaged or not at all, so the download " +
                                    "stopped. The blocks received so far are kept; try again to continue.");
                        }
                        done++;
                        bytesDone += length;
                        Report();
                    }
                    if (session != null) await session.SendAsync("BYE", cancel);
                }
                finally
                {
                    session?.Dispose();
                }
            }

            if (BlockHashing.FileSha256(part) != manifest.PackageHash)
            {
                File.Delete(part);
                throw new VentrailException("The downloaded version package didn't match its fingerprint, so it was thrown away.");
            }
            File.Move(part, final, overwrite: true);
            return final;

            void Report() => progress?.Report(new TransferProgress
            {
                BlocksDone = done, BlockCount = count, BytesDone = bytesDone, TotalBytes = manifest.Size,
                BlocksResumed = resumed, BlocksRetried = retried
            });
        });
    }

    // One block, or null if the answer was wrong (it is then asked for again).
    private static async Task<byte[]?> FetchBlockAsync(Session session, string packageHash, int index, int length,
                                                       CancellationToken cancel)
    {
        await session.SendAsync($"BLOCK {packageHash} {index}", cancel);
        string[] w = Words(await session.ReadLineAsync(cancel));
        if (w.Length == 3 && w[0] == "DATA" && w[1] == index.ToString() && int.TryParse(w[2], out int n) && n == length)
        {
            var data = new byte[length];
            await session.ReadExactlyAsync(data, cancel);
            return data;
        }
        if (w.Length > 0 && w[0] == "ERROR")
            throw new VentrailException($"{session.Peer.Title}: {string.Join(' ', w[1..])}");
        throw new IOException("Unexpected answer");   // out of step: reconnect
    }

    private static int BlockLength(TransferManifest m, int index) =>
        (int)Math.Min(m.BlockSize, m.Size - (long)index * m.BlockSize);

    /// <summary>
    /// Connects to a peer and checks its hello: the computer that answers must have the peer id
    /// discovery promised, so a wrong or reused address is noticed.
    /// </summary>
    private async Task<Session> OpenSessionAsync(string peerId, CancellationToken cancel)
    {
        var peer = _table.Find(peerId)
                   ?? throw new VentrailException("Ventrail no longer knows this computer. Search for peers again.");
        var client = new TcpClient(AddressFamily.InterNetwork);
        var session = new Session(client, peer);
        string? hello;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ConnectWait);
            await client.ConnectAsync(IPAddress.Parse(peer.IPAddress), peer.Port, timeout.Token);
            session.Start();
            hello = await session.ReadLineAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException ||
                                   ex is OperationCanceledException && !cancel.IsCancellationRequested)
        {
            hello = null;
        }
        catch
        {
            session.Dispose();
            throw;
        }

        if (hello is null)
        {
            session.Dispose();
            throw new VentrailException($"{peer.Title} didn't answer on port {peer.Port}. Check that sharing is " +
                                        "switched on there and that its firewall allows Ventrail.");
        }
        if (hello.Trim() != $"{HelloPrefix} {peerId}")
        {
            session.Dispose();
            _table.Remove(peerId);
            PeersChanged?.Invoke(this, EventArgs.Empty);
            throw new VentrailException($"The computer at {peer.IPAddress} is no longer {peer.Title}. " +
                                        "Search for peers again.");
        }
        return session;
    }

    private static VentrailException ProtocolError(PeerNode peer, string[] words) =>
        new($"{peer.Title} sent an answer Ventrail doesn't understand ({string.Join(' ', words).Truncate(60)}). " +
            "Both computers may need the same Ventrail version.");

    private static bool IsCommitHash(string s) => s.Length == 40 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Splits a line into at most max words; the last word keeps any spaces (used for names).
    private static string[] Words(string? line, int max = int.MaxValue) =>
        line is null ? Array.Empty<string>() : line.Trim().Split(' ', max, StringSplitOptions.RemoveEmptyEntries);

    // ---------- sending side ----------

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using var session = new Session(client, new PeerNode());
        try
        {
            session.Start();
            await session.SendAsync($"{HelloPrefix} {LocalPeerId}", stop);
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                idle.CancelAfter(IdleTimeout);
                string[] w = Words(await session.ReadLineAsync(idle.Token));
                if (w.Length == 0 || w[0] == "BYE") return;

                switch (w[0])
                {
                    case "LIST":
                        await SendListAsync(session, stop);
                        break;
                    case "MANIFEST" when w.Length == 2:
                        await SendManifestAsync(session, w[1], stop);
                        break;
                    case "BLOCK" when w.Length == 3 && int.TryParse(w[2], out int index):
                        await SendBlockAsync(session, w[1], index, stop);
                        break;
                    default:
                        await session.SendAsync("ERROR Unknown request.", stop);
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The other computer went away, was silent too long, or sharing was switched off.
        }
    }

    private async Task SendListAsync(Session session, CancellationToken stop)
    {
        KeyValuePair<string, Offer>[] offers;
        lock (_gate) offers = _provided.ToArray();
        var lines = new List<string>();
        foreach (var (key, offer) in offers)
        {
            try
            {
                using var repo = new Repository(offer.Path);
                if (repo.Head.Tip is { } tip)
                    lines.Add($"{key} {tip.Sha} {SafeWord(repo.Head.FriendlyName)} {offer.Name}");
            }
            catch (Exception ex) when (ex is LibGit2SharpException or IOException) { }   // folder moved or deleted
        }
        await session.SendAsync($"REPOS {lines.Count}", stop);
        foreach (string line in lines) await session.SendAsync(line, stop);
    }

    private async Task SendManifestAsync(Session session, string key, CancellationToken stop)
    {
        Offer? offer;
        lock (_gate) _provided.TryGetValue(key, out offer);
        if (offer is null)
        {
            await session.SendAsync("ERROR This repository is not shared from this computer.", stop);
            return;
        }

        CachedPackage package;
        try
        {
            package = await PreparePackageAsync(key, offer, stop);
        }
        catch (Exception ex) when (ex is VentrailException or LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            await session.SendAsync("ERROR Could not prepare the versions: " + OneLine(ex.Message), stop);
            return;
        }
        var info = package.Info;
        await session.SendAsync($"MANIFEST {info.Sha256} {info.Size} {BlockHashing.BlockSize} {package.BlockHashes.Count} " +
                                $"{info.HeadHash} {SafeWord(info.Branch)} {offer.Name}", stop);
        foreach (string hash in package.BlockHashes) await session.SendAsync(hash, stop);
    }

    private async Task SendBlockAsync(Session session, string packageHash, int index, CancellationToken stop)
    {
        CachedPackage? package;
        lock (_gate) package = _packages.Values.FirstOrDefault(p => p.Info.Sha256 == packageHash);
        if (package is null || index < 0 || index >= package.BlockHashes.Count)
        {
            await session.SendAsync("ERROR That version is no longer offered. Ask for the latest version again.", stop);
            return;
        }

        int length = (int)Math.Min(BlockHashing.BlockSize, package.Info.Size - (long)index * BlockHashing.BlockSize);
        var data = new byte[length];
        await using (var file = new FileStream(package.Info.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            file.Position = (long)index * BlockHashing.BlockSize;
            await file.ReadExactlyAsync(data, stop);
        }
        data = TestBlockFilter?.Invoke(index, data) ?? data;
        await session.SendAsync($"DATA {index} {data.Length}", stop, flush: false);
        await session.WriteAsync(data, stop);
        Interlocked.Add(ref _bytesSent, data.Length);
    }

    // Packs the repository once per latest version; later requests reuse the same package.
    private async Task<CachedPackage> PreparePackageAsync(string key, Offer offer, CancellationToken stop)
    {
        await _packing.WaitAsync(stop);
        try
        {
            string head;
            using (var repo = new Repository(offer.Path))
                head = repo.Head.Tip?.Sha ?? throw new VentrailException("Nothing has been saved yet.");

            CachedPackage? cached;
            lock (_gate) _packages.TryGetValue(key, out cached);
            if (cached != null && cached.Info.HeadHash == head && File.Exists(cached.Info.FilePath)) return cached;

            var package = await Task.Run(() =>
            {
                var info = VersionPackage.Create(offer.Path, Outbox);
                return new CachedPackage(info, BlockHashing.BlockHashes(info.FilePath));
            }, stop);
            lock (_gate) _packages[key] = package;
            if (cached != null && cached.Info.FilePath != package.Info.FilePath)
                TryDelete(cached.Info.FilePath);   // our own outdated package in the outbox
            return package;
        }
        finally
        {
            _packing.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string SafeWord(string s) => string.IsNullOrWhiteSpace(s) ? "main" : s.Replace(' ', '-');
    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>One TCP connection: text lines and raw block bytes over the same buffered stream.</summary>
    private sealed class Session : IDisposable
    {
        private const int MaxLine = 4096;
        private readonly TcpClient _client;
        private BufferedStream? _io;

        public PeerNode Peer { get; }

        public Session(TcpClient client, PeerNode peer)
        {
            _client = client;
            Peer = peer;
        }

        public void Start()
        {
            _client.NoDelay = true;
            _io = new BufferedStream(_client.GetStream(), 64 * 1024);
        }

        private BufferedStream Io => _io ?? throw new IOException("Not connected");

        public async Task SendAsync(string line, CancellationToken cancel, bool flush = true)
        {
            await Io.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancel);
            if (flush) await Io.FlushAsync(cancel);
        }

        public async Task WriteAsync(byte[] data, CancellationToken cancel)
        {
            await Io.WriteAsync(data, cancel);
            await Io.FlushAsync(cancel);
        }

        // Null when the other side closed the connection.
        public async Task<string?> ReadLineAsync(CancellationToken cancel)
        {
            var bytes = new List<byte>(80);
            var one = new byte[1];
            while (true)
            {
                if (await Io.ReadAsync(one, cancel) == 0) return bytes.Count == 0 ? null : throw new IOException("Connection closed mid-line");
                if (one[0] == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(one[0]);
                if (bytes.Count > MaxLine) throw new IOException("Line too long");
            }
        }

        public Task ReadExactlyAsync(byte[] buffer, CancellationToken cancel) => Io.ReadExactlyAsync(buffer, cancel).AsTask();

        public void Dispose()
        {
            _io?.Dispose();
            _client.Dispose();
        }
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max] + "...";
}

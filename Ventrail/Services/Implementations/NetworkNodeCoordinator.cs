using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ventrail.Models;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

/// <summary>
/// P2P node (Fig 3.8).
/// Week 10: firewall audit + permission (FirewallAuditor) and local listening:
///  - TCP 4001 accepts connections and answers with a hello line ("VENTRAIL/1 &lt;peer id&gt;").
///    Week 12 replaces the hello with block streaming.
///  - UDP 4002 answers "VENTRAIL?" with "VENTRAIL! &lt;peer id&gt; &lt;tcp port&gt;". Week 11's peer
///    discovery broadcasts that question.
/// </summary>
public class NetworkNodeCoordinator : INetworkNodeCoordinator, IDisposable
{
    public const int DefaultTcpPort = 4001;   // libp2p's common default port
    public const int DefaultUdpPort = 4002;
    public const string HelloPrefix = "VENTRAIL/1";
    public const string DiscoveryQuestion = "VENTRAIL?";
    public const string DiscoveryAnswer = "VENTRAIL!";

    private readonly IPAddress _bindAddress;
    private readonly object _gate = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private CancellationTokenSource? _stop;

    public int LocalPort { get; }
    public int DiscoveryPort { get; }
    public string LocalPeerId { get; }
    public List<PeerNode> ConnectedPeers { get; } = new();

    // The exe the firewall rules are made for (the running Ventrail.exe).
    public string ExePath { get; }

    /// <param name="bindAddress">Any (default) = reachable from the network. Tests use Loopback,
    /// which Windows Firewall never asks about.</param>
    /// <param name="dataFolder">Where the peer id is kept (default %LocalAppData%\Ventrail).</param>
    public NetworkNodeCoordinator(IPAddress? bindAddress = null, int tcpPort = DefaultTcpPort,
                                  int udpPort = DefaultUdpPort, string? dataFolder = null, string? exePath = null)
    {
        _bindAddress = bindAddress ?? IPAddress.Any;
        LocalPort = tcpPort;
        DiscoveryPort = udpPort;
        ExePath = exePath ?? Environment.ProcessPath ?? string.Empty;
        LocalPeerId = LoadOrCreatePeerId(dataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ventrail"));
    }

    private IReadOnlyList<(int Port, string Protocol)> Ports => new[] { (LocalPort, "TCP"), (DiscoveryPort, "UDP") };

    // ---------- Week 10: firewall ----------

    public FirewallStatus CheckFirewall() => FirewallAuditor.Audit(ExePath, Ports);

    public bool RequestFirewallPermission(bool includePublicNetworks)
    {
        var before = CheckFirewall();
        bool ownBlocks = before.Ports.Exists(p => p.BlockedByOwnRule);
        return FirewallAuditor.RunElevated(
            FirewallAuditor.BuildAllowScript(ExePath, Ports, includePublicNetworks, ownBlocks));
    }

    public bool RemoveFirewallPermission() => FirewallAuditor.RunElevated(FirewallAuditor.BuildRemoveScript(Ports));

    // ---------- Week 10: listening ----------

    public bool IsListening { get { lock (_gate) return _tcp != null; } }

    public bool StartListening()
    {
        lock (_gate)
        {
            if (_tcp != null) return true;
            TcpListener? tcp = null;
            UdpClient? udp = null;
            try
            {
                tcp = new TcpListener(_bindAddress, LocalPort);
                tcp.Start();
                udp = new UdpClient(new IPEndPoint(_bindAddress, DiscoveryPort));
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                int port = tcp?.Server.IsBound == true ? DiscoveryPort : LocalPort;   // TCP worked, so UDP failed
                tcp?.Stop();
                udp?.Dispose();
                throw new VentrailException(
                    $"Port {port} is already used by another program (maybe Ventrail is open twice). " +
                    "Close the other program and try again.", ex);
            }

            _tcp = tcp;
            _udp = udp;
            _stop = new CancellationTokenSource();
            _ = AcceptLoopAsync(tcp, _stop.Token);
            _ = DiscoveryLoopAsync(udp, _stop.Token);
            return true;
        }
    }

    public void StopListening()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            _tcp?.Stop();
            _udp?.Dispose();
            _tcp = null;
            _udp = null;
            _stop = null;
        }
    }

    public void Dispose() => StopListening();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        byte[] hello = Encoding.UTF8.GetBytes($"{HelloPrefix} {LocalPeerId}\n");
                        await client.GetStream().WriteAsync(hello, stop);
                    }
                }, stop);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }   // StopListening
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task DiscoveryLoopAsync(UdpClient udp, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(stop);
                if (Encoding.UTF8.GetString(packet.Buffer).Trim() != DiscoveryQuestion) continue;   // ignore anything else
                byte[] answer = Encoding.UTF8.GetBytes($"{DiscoveryAnswer} {LocalPeerId} {LocalPort}");
                await udp.SendAsync(answer, packet.RemoteEndPoint, stop);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    // A short random id that stays the same between runs, so peers recognise this computer.
    private static string LoadOrCreatePeerId(string folder)
    {
        string file = Path.Combine(folder, "peer-id.txt");
        try
        {
            if (File.Exists(file))
            {
                string saved = File.ReadAllText(file).Trim();
                if (saved.StartsWith("vt-") && saved.Length == 15) return saved;
            }
            string id = "vt-" + Guid.NewGuid().ToString("N")[..12];
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, id);
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "vt-" + Guid.NewGuid().ToString("N")[..12];   // can't save: use one for this run
        }
    }

    // ---------- later weeks ----------

    public List<PeerNode> DiscoverPeers() =>
        throw new NotImplementedException("Planned for Week 11: DHT / local broadcast peer discovery.");

    public bool EstablishConnection(string targetPeerId) =>
        throw new NotImplementedException("Planned for Week 12: secure socket connection.");

    public bool TransferData(string peerId, string dataHash) =>
        throw new NotImplementedException("Planned for Week 12: data block streaming and hash verification.");
}

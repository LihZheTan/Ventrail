using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations.Dht;

namespace Ventrail.ViewModels;

/// <summary>
/// Week 10: the Peer-to-Peer page — firewall audit, permission prompt and local listening
/// (proposal Fig 3.5: check the firewall; if the user denies permission, abort; if allowed, open
/// the ports and continue). Discovery (Week 11) and transfers (Week 12) build on this.
/// </summary>
public partial class MainDashboardViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TcpCheck), nameof(UdpCheck), nameof(FirewallSummary), nameof(CanRemoveFirewallPermission))]
    private FirewallStatus? _firewall;

    [ObservableProperty] private bool _isCheckingFirewall;

    // Bound two-way to the "Enable P2P Sharing" switch and drives the Online/Offline pill.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkStateText), nameof(PeersSummary))]
    private bool _isSharingEnabled;

    public PortCheck? TcpCheck => Firewall?.Ports.FirstOrDefault(p => p.Protocol == "TCP");
    public PortCheck? UdpCheck => Firewall?.Ports.FirstOrDefault(p => p.Protocol == "UDP");
    public string FirewallSummary => Firewall?.Summary ?? "Checking Windows Firewall...";
    public bool CanRemoveFirewallPermission => Firewall?.HasVentrailRules == true;
    public string NetworkStateText => IsSharingEnabled ? "Online" : "Offline";
    public string LocalPeerId => _network.LocalPeerId;

    // Check the firewall the first time the Network page is opened (read-only, no prompt).
    partial void OnCurrentPageChanged(AppPage value)
    {
        if (value == AppPage.Network && Firewall is null && !IsCheckingFirewall)
            _ = CheckFirewallAsync();
    }

    [RelayCommand]
    private async Task CheckFirewallAsync()
    {
        IsCheckingFirewall = true;
        try
        {
            Firewall = await Background(_network.CheckFirewall);
        }
        catch (Exception ex)
        {
            ShowToast($"Could not check the firewall: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsCheckingFirewall = false;
        }
    }

    // ---------- Week 11: peer discovery ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeersSummary))]
    private bool _isSearchingPeers;

    // "Add by address" box, for networks that block broadcasts.
    [ObservableProperty] private string _peerAddress = string.Empty;

    // Peers that announced a copy of the open repository (DHT provider records).
    private readonly HashSet<string> _providerIds = new();
    private SynchronizationContext? _uiContext;

    public string LocalDisplayName => _network.DisplayName;

    // Week 14: "THIS-PC (192.168.1.20)", so the other person can type it into "Add".
    public string LocalComputerText
    {
        get
        {
            string[] addresses;
            try
            {
                addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                                n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(u => u.Address.ToString())
                    .Where(a => !a.StartsWith("169.254."))   // "no network" fallback addresses
                    .Distinct().Take(2).ToArray();
            }
            catch (System.Net.NetworkInformation.NetworkInformationException) { addresses = Array.Empty<string>(); }
            return addresses.Length == 0 ? LocalDisplayName : $"{LocalDisplayName} ({string.Join(", ", addresses)})";
        }
    }

    public string PeersSummary =>
        IsSearchingPeers ? "Searching your network..."
        : !IsSharingEnabled ? "Turn on P2P sharing to find other computers running Ventrail on your network."
        : ActivePeers.Count == 0 ? "No other computers found yet. Make sure Ventrail is open with sharing on there, " +
                                   "and that both computers are on the same network."
        : $"{ActivePeers.Count} computer(s) found on your network.";

    // Called from the constructor: other computers can find this one at any time, so the list also
    // updates when a peer appears on its own (the event comes from a background thread).
    private void HookNetworkEvents()
    {
        _uiContext = SynchronizationContext.Current;
        _network.PeersChanged += (_, _) =>
        {
            if (_uiContext is null) RefreshPeerList();
            else _uiContext.Post(_ => RefreshPeerList(), null);
        };
    }

    private void RefreshPeerList()
    {
        ActivePeers.Clear();
        string? currentKey = CurrentRepositoryKey();
        if (IsSharingEnabled)
            foreach (var p in _network.GetKnownPeers())
            {
                p.Repositories = (_offersByPeer.TryGetValue(p.PeerId, out var offers) ? offers : new())
                    .Select(o => { o.IsCurrentRepository = HasRepo && o.Key == currentKey; return o; }).ToList();
                p.HasCurrentRepository = _providerIds.Contains(p.PeerId) || p.Repositories.Any(o => o.IsCurrentRepository);
                ActivePeers.Add(p);
            }
        OnPropertyChanged(nameof(HasPeers));
        OnPropertyChanged(nameof(PeersSummary));
    }

    // Off: the list empties. On: it shows the peers still in the routing table.
    partial void OnIsSharingEnabledChanged(bool value) => RefreshPeerList();

    // A different repository: who has a copy must be looked up again.
    partial void OnCurrentDirectoryChanged(string value)
    {
        _providerIds.Clear();
        RefreshPeerList();
    }

    // The DHT key for the open repository (a hash of its first version), or null before the first save.
    private string? CurrentRepositoryKey()
    {
        var root = History.LastOrDefault(c => c.ParentHashes.Count == 0);
        return root is null ? null : DhtKey.ForRepository(root.Hash);
    }

    private async Task SearchPeersAsync()
    {
        IsSearchingPeers = true;
        try
        {
            var peers = await Background(_network.DiscoverPeers);
            await LoadOffersAsync(peers);
            RefreshPeerList();
            LogActivity($"Peer discovery found {peers.Count} computer(s)");
        }
        finally
        {
            IsSearchingPeers = false;
        }
    }

    [RelayCommand]
    private Task FindPeersAsync() => RunBusyAsync("Searching for other computers...", async () =>
    {
        if (!await EnsureSharingReadyAsync()) return;
        await SearchPeersAsync();
        StatusText = PeersSummary;
    });

    [RelayCommand]
    private Task AddPeerByAddressAsync() => RunBusyAsync("Contacting that computer...", async () =>
    {
        string address = PeerAddress.Trim();
        if (address.Length == 0)
        {
            ShowToast("Type the other computer's IP address or name first.");
            return;
        }
        if (!await EnsureSharingReadyAsync()) return;
        var peer = await Background(() => _network.AddPeerByAddress(address));
        if (peer is null)
        {
            ShowToast($"No Ventrail answered at {address}. Check that it is open with sharing on.", ToastKind.Error);
            StatusText = $"No answer from {address}.";
            return;
        }
        PeerAddress = string.Empty;
        RefreshPeerList();
        LogActivity($"Added peer {peer.Title} ({peer.IPAddress}) by address");
        ShowToast($"Found {peer.Title}.", ToastKind.Success);
        StatusText = PeersSummary;
    });

    // The Connect button: checks that the computer really answers as that peer (TCP hello).
    [RelayCommand]
    private Task ConnectPeerAsync(PeerNode? peer) => peer is null ? Task.CompletedTask
        : RunBusyAsync($"Connecting to {peer.Title}...", async () =>
        {
            await Background(() => _network.EstablishConnection(peer.PeerId));
            RefreshPeerList();
            LogActivity($"Connected to {peer.Title} ({peer.IPAddress})");
            ShowToast($"Connected to {peer.Title}.", ToastKind.Success);
            StatusText = $"Connected to {peer.Title}.";
        });

    // ---------- Week 12: receiving versions (block streaming + hash checks) ----------

    // What each peer shares (its "LIST" answer), refreshed after every search.
    private readonly Dictionary<string, List<OfferedRepository>> _offersByPeer = new();
    private CancellationTokenSource? _transferCancel;
    private long _bytesReceived;

    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private string _transferTitle = string.Empty;
    [ObservableProperty] private string _transferDetail = string.Empty;
    [ObservableProperty] private double _transferPercent;

    /// <summary>Where a downloaded copy goes. Replaced in tests; normally asks with the folder dialog.</summary>
    public Func<string, string?> PickFolder { get; set; } = title =>
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    };

    private async Task LoadOffersAsync(IEnumerable<PeerNode> peers)
    {
        var lists = await Task.WhenAll(peers.Select(async p =>
        {
            try { return (p.PeerId, Offers: await Background(() => _network.ListRepositories(p.PeerId))); }
            catch (Exception) { return (p.PeerId, Offers: new List<OfferedRepository>()); }   // older Ventrail or busy: no list
        }));
        _offersByPeer.Clear();
        foreach (var (peerId, offers) in lists) _offersByPeer[peerId] = offers;
    }

    /// <summary>
    /// "Get latest" (same repository as the one open here) or "Download a copy" (a new folder).
    /// Fig 3.8 TransferData: ask for the manifest, stream every block with its SHA-256 check, then let
    /// Git check every version before anything on disk changes.
    /// </summary>
    [RelayCommand]
    private async Task DownloadOfferAsync(OfferedRepository? offer)
    {
        if (offer is null || IsBusy) return;
        bool intoCurrent = offer.IsCurrentRepository && HasRepo;
        string? newCopy = null;
        if (!intoCurrent)
        {
            string? parent = PickFolder($"Choose where to put the copy of {offer.Name}");
            if (parent is null) return;
            newCopy = FreeFolderName(Path.Combine(parent, offer.Name));
        }
        else if (History.Count > 0 && History[0].Hash == offer.HeadHash)
        {
            ShowToast($"You already have {offer.PeerName}'s latest version of {offer.Name}.", ToastKind.Success);
            return;
        }

        bool copied = false;
        await RunBusyAsync($"Asking {offer.PeerName} for {offer.Name}...", async () =>
        {
            var manifest = await Background(() => _network.RequestVersion(offer.PeerId, offer.Key));
            LogActivity($"{offer.PeerName} offered {offer.Name} ({FormatBytes(manifest.Size)}, {manifest.BlockCount} block(s), " +
                        $"SHA-256 {manifest.PackageHash[..12]}...)");
            string package = await ReceiveAsync(manifest, offer.PeerName);

            if (newCopy != null)
            {
                await Background(() => _repo.InitFromPackage(newCopy, package, manifest.HeadHash, manifest.Branch));
                DeletePackage(package);
                LogActivity($"Downloaded a copy of {offer.Name} from {offer.PeerName} into {newCopy}");
                SessionTransfers++;
                copied = true;
                return;
            }

            var result = await Background(() =>
                _repo.ImportVersionPackage(package, manifest.HeadHash, manifest.Branch, offer.PeerName));
            if (result.Outcome != ImportOutcome.WaitingForSave) DeletePackage(package);   // kept to retry instantly
            await ReloadAsync();
            var (text, kind) = DescribeImport(result, offer);
            StatusText = text;
            ShowToast(text, kind, 7);
            LogActivity($"Get latest from {offer.PeerName}: {result.Outcome} ({result.HeadHash[..7]})");
            SessionTransfers++;
        });

        if (copied && newCopy != null)
        {
            await InitializeDashboardAsync(newCopy);
            ShowToast($"Downloaded {offer.Name} from {offer.PeerName}. Every block and version was checked.", ToastKind.Success, 6);
        }
    }

    [RelayCommand]
    private void CancelTransfer() => _transferCancel?.Cancel();

    private async Task<string> ReceiveAsync(TransferManifest manifest, string peerName)
    {
        _transferCancel = new CancellationTokenSource();
        TransferTitle = $"Receiving {manifest.Name} from {peerName}";
        TransferDetail = $"{manifest.BlockCount} block(s), {FormatBytes(manifest.Size)}";
        TransferPercent = 0;
        IsTransferring = true;
        TransferProgress? last = null;
        var progress = new Progress<TransferProgress>(p =>
        {
            last = p;
            TransferPercent = p.Percent;
            TransferDetail = $"{p.BlocksDone} of {p.BlockCount} blocks checked · {FormatBytes(p.BytesDone)} of {FormatBytes(p.TotalBytes)}" +
                             (p.BlocksResumed > 0 ? $" · {p.BlocksResumed} kept from last time" : "") +
                             (p.BlocksRetried > 0 ? $" · {p.BlocksRetried} block(s) asked for again" : "") +
                             (p.BytesPerSecond > 0 ? $" · {FormatBytes((long)p.BytesPerSecond)}/s" : "") +
                             (p.TimeLeft is { } left && p.BlocksDone < p.BlockCount ? $" · about {FormatDuration(left)} left" : "");
        });
        try
        {
            string package = await Background(() => _network.DownloadPackage(manifest, progress, _transferCancel.Token));
            _bytesReceived += manifest.Size;
            SessionBytesShared = _bytesReceived + _network.BytesSent;
            if (last?.BlocksRetried > 0)
                LogActivity($"{last.BlocksRetried} block(s) from {peerName} were damaged (failed the SHA-256 check) or too slow, and were asked for again");
            return package;
        }
        catch (OperationCanceledException)
        {
            throw new VentrailException("Download cancelled. The blocks received so far are kept, so trying again " +
                                        "continues where it stopped.");
        }
        finally
        {
            IsTransferring = false;
            _transferCancel.Dispose();
            _transferCancel = null;
        }
    }

    private static (string, ToastKind) DescribeImport(ImportResult result, OfferedRepository offer) => result.Outcome switch
    {
        ImportOutcome.Updated => ($"Your files now match {offer.PeerName}'s latest version ({result.HeadHash[..7]}).", ToastKind.Success),
        ImportOutcome.AlreadyUpToDate => ($"You already have {offer.PeerName}'s latest version.", ToastKind.Success),
        ImportOutcome.AlreadyNewer => ($"You already have {offer.PeerName}'s version, plus newer ones of your own.", ToastKind.Success),
        ImportOutcome.WaitingForSave => ($"{offer.PeerName}'s newer version arrived, but you have unsaved changes, so your files " +
                                         "were not touched. Save or discard your changes, then click Get latest again.", ToastKind.Info),
        ImportOutcome.Diverged => ($"You and {offer.PeerName} each saved different versions. Theirs was kept separately, so " +
                                   "nothing of yours was overwritten. Combining both isn't supported yet.", ToastKind.Info),
        _ => ($"Received {offer.Name} from {offer.PeerName}.", ToastKind.Success)
    };

    // "Thesis", or "Thesis (2)" if a non-empty "Thesis" folder is already there.
    private static string FreeFolderName(string path)
    {
        string candidate = path;
        for (int n = 2; Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any(); n++)
            candidate = $"{path} ({n})";
        return candidate;
    }

    // The package was only a carrier: its versions are now inside the repository.
    private static void DeletePackage(string package)
    {
        try { File.Delete(package); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} s"
        : t.TotalMinutes < 60 ? $"{(int)Math.Ceiling(t.TotalMinutes)} min"
        : $"{t.TotalHours:F1} h";

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    // Class diagram: ShareRepository(String peerId). Week 10 made the firewall part real; Week 11
    // announces the repository in the DHT and finds who else already has a copy; Week 12 lets them
    // download its versions (served by NetworkNodeCoordinator.Transfer).
    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task ShareRepositoryAsync() => RunBusyAsync("Checking network permissions...", async () =>
    {
        if (!await EnsureSharingReadyAsync()) return;
        string? key = CurrentRepositoryKey();
        if (key is null)
        {
            ShowToast("Save a first version before sharing this repository.");
            StatusText = "Nothing to share yet: save a first version.";
            return;
        }

        StatusText = $"Announcing {RepoName} on your network...";
        string folder = CurrentDirectory, name = RepoName;
        await Background(() => _network.ProvideRepository(key, folder, name));
        await SearchPeersAsync();
        var providers = await Background(() => _network.FindProviders(key));
        _providerIds.Clear();
        foreach (var p in providers) _providerIds.Add(p.PeerId);
        RefreshPeerList();

        LogActivity($"Shared {RepoName} on the network (key {key}); {providers.Count} other computer(s) have a copy");
        StatusText = providers.Count == 0
            ? $"{RepoName} is shared. {PeersSummary}"
            : $"{RepoName} is shared. {providers.Count} computer(s) already have a copy.";
        ShowToast($"{RepoName} is now offered to computers on your network.", ToastKind.Success);
    });

    // The "Enable P2P Sharing" switch (its IsChecked has already flipped through the binding).
    [RelayCommand]
    private Task ToggleSharingAsync()
    {
        if (!IsSharingEnabled)
        {
            _network.StopListening();
            LogActivity("Sharing switched off: Ventrail stopped listening");
            StatusText = "Sharing is off.";
            return Task.CompletedTask;
        }
        return RunBusyAsync("Checking network permissions...", async () =>
        {
            if (!await EnsureSharingReadyAsync()) return;
            ShowToast("Sharing is on. Other computers on your network can now reach Ventrail.", ToastKind.Success);
            await SearchPeersAsync();
            StatusText = PeersSummary;
        });
    }

    [RelayCommand]
    private Task RemoveFirewallPermissionAsync() => RunBusyAsync("Removing firewall permission...", async () =>
    {
        if (!await AskAsync(
                "Remove firewall permission?",
                "Ventrail stops sharing and its two Windows Firewall rules are deleted. Windows will ask you to " +
                "confirm. You can allow sharing again at any time.",
                "Remove", "Cancel", ""))
            return;

        _network.StopListening();
        IsSharingEnabled = false;
        if (!await Background(_network.RemoveFirewallPermission))
        {
            ShowToast("Nothing was changed: the Windows prompt was cancelled.");
            return;
        }
        Firewall = await Background(_network.CheckFirewall);
        LogActivity("Firewall permission removed");
        ShowToast("Firewall permission removed. Sharing is off.", ToastKind.Success);
    });

    /// <summary>
    /// Fig 3.5: audit the firewall, ask the user if anything is missing, and only then open the
    /// ports. Returns false (and leaves sharing off) whenever permission is not given.
    /// </summary>
    private async Task<bool> EnsureSharingReadyAsync()
    {
        var status = Firewall = await Background(_network.CheckFirewall);

        if (!status.AllAllowed)
        {
            if (!await AskAsync("Allow peer-to-peer sharing?", PermissionMessage(status), "Allow", "Not now", ""))
                return Abort("Sharing cancelled. Ventrail needs firewall permission to share.",
                             "Sharing cancelled: firewall permission not given");

            if (!await Background(() => _network.RequestFirewallPermission(status.IsPublicNetwork)))
                return Abort("Sharing cancelled: Windows' permission prompt was closed without allowing it.",
                             "Sharing cancelled: Windows admin prompt declined");

            status = Firewall = await Background(_network.CheckFirewall);
            if (!status.AllAllowed)
            {
                var stillBlocked = status.Ports.First(p => p.Access != PortAccess.Allowed);
                IsSharingEnabled = false;
                StatusText = "Windows Firewall still blocks Ventrail.";
                ShowToast($"Windows Firewall still blocks {stillBlocked.Name}. {stillBlocked.Detail}", ToastKind.Error);
                LogActivity($"Firewall still blocks {stillBlocked.Name} after permission: {stillBlocked.Detail}");
                return false;
            }
            LogActivity($"Firewall permission given for port {_network.LocalPort} (TCP) and {_network.DiscoveryPort} (UDP)");
        }

        if (!_network.IsListening)
        {
            try
            {
                await Background(_network.StartListening);
            }
            catch (Exception)
            {
                IsSharingEnabled = false;
                throw;   // RunBusyAsync shows it (e.g. "Port 4001 is already used by another program")
            }
            LogActivity($"Sharing on: listening on port {_network.LocalPort} (TCP) and {_network.DiscoveryPort} (UDP)");
        }
        IsSharingEnabled = true;
        return true;
    }

    private bool Abort(string toast, string log)
    {
        IsSharingEnabled = false;
        LogActivity(log);
        ShowToast(toast);
        StatusText = "Sharing is off.";
        return false;
    }

    private string PermissionMessage(FirewallStatus status)
    {
        string text = $"To share versions directly with other computers, Windows Firewall must let Ventrail use " +
                      $"port {_network.LocalPort} (TCP) and port {_network.DiscoveryPort} (UDP).\n\n" +
                      "Only computers on the same local network can connect, and nothing is uploaded to the cloud. " +
                      "Windows will ask you to confirm (admin prompt), and you can remove the permission at any time.";
        if (status.IsPublicNetwork)
            text += "\n\nThis network is set to Public (for example café or campus Wi-Fi). Allowing makes Ventrail " +
                    "reachable by other devices on this same network. On a network you trust, you can set it to " +
                    "Private in Windows Settings > Network & internet.";
        if (status.Ports.Any(p => p.BlockedByOwnRule))
            text += "\n\nAn earlier \"block\" rule for Ventrail will be replaced.";
        else if (status.Ports.Any(p => p.Access == PortAccess.Blocked))
            text += "\n\nNote: another firewall setting is blocking these ports, so sharing may still not work.";
        return text;
    }
}

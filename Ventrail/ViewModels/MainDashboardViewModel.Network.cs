using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ventrail.Models;
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
        if (IsSharingEnabled)
            foreach (var p in _network.GetKnownPeers())
            {
                p.HasCurrentRepository = _providerIds.Contains(p.PeerId);
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
            StatusText = $"Connected to {peer.Title}. Sending versions arrives in Week 12.";
        });

    // Class diagram: ShareRepository(String peerId). Week 10 made the firewall part real; Week 11
    // announces the repository in the DHT and finds who else already has a copy.
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
        await Background(() => _network.ProvideRepository(key));
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

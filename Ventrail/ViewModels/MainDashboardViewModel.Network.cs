using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ventrail.Models;

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
    [NotifyPropertyChangedFor(nameof(NetworkStateText))]
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

    // Class diagram: ShareRepository(String peerId). Week 10 makes the firewall part real.
    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private Task ShareRepositoryAsync() => RunBusyAsync("Checking network permissions...", async () =>
    {
        if (!await EnsureSharingReadyAsync()) return;
        try
        {
            var peers = await Background(_network.DiscoverPeers);
            ActivePeers.Clear();
            foreach (var p in peers) ActivePeers.Add(p);
            OnPropertyChanged(nameof(HasPeers));
            StatusText = $"Found {ActivePeers.Count} peer(s).";
            LogActivity($"Peer discovery found {ActivePeers.Count} peer(s)");
        }
        catch (NotImplementedException)
        {
            StatusText = "Ventrail is online. Finding other computers arrives in Week 11.";
            ShowToast("Sharing is on. Finding other computers arrives in Week 11.", ToastKind.Success);
        }
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
            if (await EnsureSharingReadyAsync())
                ShowToast("Sharing is on. Other computers on your network can now reach Ventrail.", ToastKind.Success);
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

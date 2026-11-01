using System.Collections.Generic;
using System.Linq;

namespace Ventrail.Models;

public enum PortAccess { Unknown, Allowed, NeedsPermission, Blocked }

/// <summary>Week 10: whether other computers can reach one of Ventrail's ports.</summary>
public class PortCheck
{
    public int Port { get; set; }
    public string Protocol { get; set; } = "TCP";   // "TCP" or "UDP"
    public PortAccess Access { get; set; }
    public string Detail { get; set; } = string.Empty;   // plain-language reason, shown as a tooltip

    // True when the block comes from a rule made for Ventrail itself (usually created when someone
    // pressed "Cancel" on Windows' own "allow access?" pop-up). Ventrail may replace those rules.
    public bool BlockedByOwnRule { get; set; }

    public string Name => $"Port {Port} ({Protocol})";

    public string Label => Access switch
    {
        PortAccess.Allowed => "Allowed",
        PortAccess.NeedsPermission => "Needs permission",
        PortAccess.Blocked => "Blocked",
        _ => "Unknown"
    };
}

/// <summary>
/// Week 10: result of the firewall audit (Fig 3.5) — one PortCheck per port, plus the network
/// type, which decides which Windows Firewall rules apply.
/// </summary>
public class FirewallStatus
{
    public bool FirewallOn { get; set; } = true;
    public string NetworkProfile { get; set; } = "Unknown";   // "Private", "Public", "Domain" (work)
    public bool IsPublicNetwork { get; set; }
    public bool HasVentrailRules { get; set; }                // Ventrail's own allow rules exist
    public string? Error { get; set; }                        // set when the settings could not be read
    public List<PortCheck> Ports { get; set; } = new();

    public bool AllAllowed => Ports.Count > 0 && Ports.All(p => p.Access == PortAccess.Allowed);

    public string Summary
    {
        get
        {
            if (Error != null) return Error;
            if (!FirewallOn) return "Windows Firewall is off on this network, so nothing is blocked.";
            if (AllAllowed) return $"Other computers on your {NetworkProfile.ToLowerInvariant()} network can reach Ventrail.";
            if (Ports.Any(p => p.Access == PortAccess.Blocked))
                return "Windows Firewall is blocking Ventrail. Turning on sharing will ask Windows to allow it.";
            return "Windows Firewall will block other computers until you allow Ventrail.";
        }
    }
}

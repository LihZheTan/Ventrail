using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Ventrail.Models;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

/// <summary>One Windows Firewall rule, reduced to what the audit needs.</summary>
public record FirewallRuleInfo(string Name, bool Enabled, bool Inbound, bool Allow, int Protocol,
                               string? LocalPorts, int Profiles, string? Application,
                               string? Service = null, string? UserOwner = null);

/// <summary>
/// Week 10: firewall audit and permission (proposal Fig 3.5).
///
/// Reading: uses Windows Firewall's own COM API (HNetCfg.FwPolicy2), which works without admin
/// rights and in any Windows language (unlike parsing netsh output).
/// Changing: adds two inbound "allow" rules with netsh, run through the normal Windows admin
/// prompt (UAC), so the user always sees and approves the change. Rules are limited to Ventrail's
/// exe, its two ports and devices on the same local network (remoteip=localsubnet).
/// </summary>
public static class FirewallAuditor
{
    public const int Tcp = 6, Udp = 17, AnyProtocol = 256;   // NET_FW_IP_PROTOCOL
    private const int Domain = 1, Private = 2, Public = 4;   // NET_FW_PROFILE_TYPE2

    public static string RuleName(string protocol, int port) => $"Ventrail P2P ({protocol} {port})";

    // ---------- reading ----------

    public static FirewallStatus Audit(string exePath, IReadOnlyList<(int Port, string Protocol)> ports)
    {
        try
        {
            Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                        ?? throw new InvalidOperationException("Windows Firewall API not found");
            dynamic policy = Activator.CreateInstance(type)!;
            int current = policy.CurrentProfileTypes;

            // Per-profile settings are indexed COM properties, which "dynamic" can't call directly.
            object Get(string name, int profile) =>
                type.InvokeMember(name, BindingFlags.GetProperty, null, policy, new object[] { profile })!;

            var active = new[] { Domain, Private, Public }.Where(p => (current & p) != 0).ToList();
            bool on = active.Any(p => (bool)Get("FirewallEnabled", p));
            bool blockAll = active.Any(p => (bool)Get("FirewallEnabled", p) && (bool)Get("BlockAllInboundTraffic", p));
            bool defaultAllow = active.Count > 0 && active.All(p => Convert.ToInt32(Get("DefaultInboundAction", p)) == 1);

            var rules = new List<FirewallRuleInfo>();
            foreach (dynamic r in policy.Rules)
            {
                bool enabled = r.Enabled, inbound = (int)r.Direction == 1;
                string? app = r.ApplicationName, service = r.serviceName;

                // The owner (INetFwRule3) only matters for enabled inbound rules with no program or
                // service. "dynamic" only sees the base interface and would throw, and a direct
                // IDispatch call is slow, so it is fetched for those few rules only.
                string? owner = null;
                if (enabled && inbound && string.IsNullOrEmpty(app) && string.IsNullOrEmpty(service))
                {
                    object rule = r;
                    owner = rule.GetType().InvokeMember("LocalUserOwner", BindingFlags.GetProperty, null, rule, null) as string;
                }
                rules.Add(new FirewallRuleInfo((string)r.Name ?? "", enabled, inbound, (int)r.Action == 1,
                    (int)r.Protocol, (string?)r.LocalPorts, (int)r.Profiles, app, service, owner));
            }
            return Evaluate(exePath, current, on, blockAll, defaultAllow, rules, ports);
        }
        catch (Exception ex)
        {
            // Another security program may manage the firewall, or the service is stopped.
            return new FirewallStatus
            {
                Error = "Ventrail couldn't read the Windows Firewall settings, so it can't tell whether other " +
                        $"computers can reach it ({ex.Message}).",
                Ports = ports.Select(p => new PortCheck
                {
                    Port = p.Port, Protocol = p.Protocol, Access = PortAccess.Unknown,
                    Detail = "The firewall settings could not be read."
                }).ToList()
            };
        }
    }

    /// <summary>
    /// Works out, like Windows does, whether incoming connections to each port reach Ventrail:
    /// a matching block rule always wins, then a matching allow rule, then the network's default.
    /// Kept separate from the COM code so it can be tested with made-up rules.
    /// </summary>
    public static FirewallStatus Evaluate(string exePath, int currentProfiles, bool firewallOn, bool blockAllInbound,
                                          bool defaultInboundAllow, IReadOnlyList<FirewallRuleInfo> rules,
                                          IReadOnlyList<(int Port, string Protocol)> ports)
    {
        var status = new FirewallStatus
        {
            FirewallOn = firewallOn,
            NetworkProfile = (currentProfiles & Public) != 0 ? "Public"
                           : (currentProfiles & Private) != 0 ? "Private"
                           : (currentProfiles & Domain) != 0 ? "Domain" : "Unknown",
            IsPublicNetwork = (currentProfiles & Public) != 0,
            HasVentrailRules = rules.Any(r => ports.Any(p => r.Name == RuleName(p.Protocol, p.Port)))
        };

        foreach (var (port, protocol) in ports)
        {
            int proto = protocol == "UDP" ? Udp : Tcp;
            var check = new PortCheck { Port = port, Protocol = protocol };
            var matching = rules.Where(r => r.Enabled && r.Inbound && (r.Profiles & currentProfiles) != 0 &&
                                            AppliesToDesktopApps(r) &&
                                            (r.Protocol == AnyProtocol || r.Protocol == proto) &&
                                            PortMatches(r.LocalPorts, port) && AppMatches(r.Application, exePath))
                                .ToList();
            var block = matching.FirstOrDefault(r => !r.Allow);
            var allow = matching.FirstOrDefault(r => r.Allow);

            if (!firewallOn)
                (check.Access, check.Detail) = (PortAccess.Allowed, "Windows Firewall is off on this network.");
            else if (blockAllInbound)
                (check.Access, check.Detail) = (PortAccess.Blocked,
                    "Windows is set to block ALL incoming connections on this network (Windows Security > Firewall).");
            else if (block != null)
            {
                (check.Access, check.Detail) = (PortAccess.Blocked, $"Blocked by the firewall rule \"{block.Name}\".");
                check.BlockedByOwnRule = !string.IsNullOrEmpty(block.Application) && AppMatches(block.Application, exePath);
            }
            else if (allow != null)
                (check.Access, check.Detail) = (PortAccess.Allowed, $"Allowed by the firewall rule \"{allow.Name}\".");
            else if (defaultInboundAllow)
                (check.Access, check.Detail) = (PortAccess.Allowed, "This network lets all incoming connections through.");
            else
                (check.Access, check.Detail) = (PortAccess.NeedsPermission,
                    "No firewall rule lets other computers reach Ventrail on this port yet.");
            status.Ports.Add(check);
        }
        return status;
    }

    // "4001", "4000-4010", "80,443,4001", "*" or empty (= any). Keywords like "RPC" never match.
    public static bool PortMatches(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts) || localPorts.Trim() == "*") return true;
        foreach (string part in localPorts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out int lo) && int.TryParse(part[(dash + 1)..], out int hi))
            {
                if (port >= lo && port <= hi) return true;
            }
            else if (int.TryParse(part, out int single) && single == port) return true;
        }
        return false;
    }

    // Rules Windows only applies to something else, even though they name no program:
    //  - service rules (e.g. "Remote Desktop" for the TermService service);
    //  - Store-app rules (e.g. "Game Bar"), which have an owning user and are tied to the app package;
    //  - "any program, any port" allow rules: in practice these are Store-app rules too. Ignoring them
    //    can only make Ventrail ask for permission it didn't strictly need, never the reverse.
    private static bool AppliesToDesktopApps(FirewallRuleInfo r)
    {
        if (!string.IsNullOrEmpty(r.Service)) return false;
        bool noProgram = string.IsNullOrWhiteSpace(r.Application) || r.Application == "*";
        if (noProgram && !string.IsNullOrEmpty(r.UserOwner)) return false;
        bool anyPort = string.IsNullOrWhiteSpace(r.LocalPorts) || r.LocalPorts.Trim() == "*";
        if (noProgram && anyPort && r.Allow) return false;
        return true;
    }

    // A rule with no program applies to every program; otherwise it must be Ventrail's exe.
    private static bool AppMatches(string? ruleApp, string exePath)
    {
        if (string.IsNullOrWhiteSpace(ruleApp) || ruleApp == "*") return true;
        try
        {
            string a = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ruleApp));
            string b = Path.GetFullPath(exePath);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }   // malformed path in someone else's rule
    }

    // ---------- changing (admin prompt) ----------

    /// <summary>
    /// The netsh commands that give permission. Old Ventrail rules are replaced so they never pile
    /// up; with replaceOwnBlockRules, inbound rules for Ventrail's own exe (e.g. the block rule
    /// Windows creates when its pop-up is cancelled) are removed first. Other programs' rules are
    /// never touched.
    /// </summary>
    public static string BuildAllowScript(string exePath, IReadOnlyList<(int Port, string Protocol)> ports,
                                          bool includePublic, bool replaceOwnBlockRules)
    {
        string profiles = includePublic ? "private,domain,public" : "private,domain";
        var steps = new List<string>();
        if (replaceOwnBlockRules)
            steps.Add($"netsh advfirewall firewall delete rule name=all dir=in program=\"{exePath}\" >nul");
        steps.AddRange(ports.Select(p => $"netsh advfirewall firewall delete rule name=\"{RuleName(p.Protocol, p.Port)}\" >nul"));

        string adds = string.Join(" && ", ports.Select(p =>
            $"netsh advfirewall firewall add rule name=\"{RuleName(p.Protocol, p.Port)}\" dir=in action=allow " +
            $"protocol={p.Protocol} localport={p.Port} program=\"{exePath}\" remoteip=localsubnet " +
            $"profile={profiles} enable=yes description=\"Lets Ventrail share versions with computers on the same network.\""));

        // "&" runs the clean-up even if there was nothing to delete; "&&" makes the exit code fail
        // if any rule could not be added.
        return string.Join(" & ", steps) + " & " + adds;
    }

    public static string BuildRemoveScript(IReadOnlyList<(int Port, string Protocol)> ports) =>
        string.Join(" & ", ports.Select(p => $"netsh advfirewall firewall delete rule name=\"{RuleName(p.Protocol, p.Port)}\""));

    /// <summary>
    /// Runs the script through the Windows admin prompt. False if the user said No to the prompt.
    /// </summary>
    public static bool RunElevated(string script)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/s /c \"{script}\"")
        {
            UseShellExecute = true,          // required for "runas" (the UAC prompt)
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var process = Process.Start(info)
                                ?? throw new VentrailException("Windows could not start the firewall settings tool.");
            if (!process.WaitForExit(60_000))
                throw new VentrailException("Changing the firewall settings took too long. Please try again.");
            if (process.ExitCode != 0)
                throw new VentrailException($"Windows did not accept the firewall change (code {process.ExitCode}). " +
                                            "Your organisation may manage these settings.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: "No" on the admin prompt
        {
            return false;
        }
    }
}

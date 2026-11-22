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
/// Checks whether Windows Firewall lets other computers reach Ventrail, and asks for permission.
///
/// Reading: uses Windows Firewall's own COM API (HNetCfg.FwPolicy2), which works without admin
/// rights and in any Windows language (unlike parsing netsh output).
/// Changing: adds two inbound "allow" rules through the same API, from a copy of Ventrail.exe
/// started with the normal Windows admin prompt (UAC), so the user always sees and approves the
/// change, and the prompt names Ventrail. Rules are limited to Ventrail's exe, its two ports and
/// devices on the same local network (LocalSubnet).
/// </summary>
public static class FirewallAuditor
{
    public const int Tcp = 6, Udp = 17, AnyProtocol = 256;   // NET_FW_IP_PROTOCOL
    private const int Domain = 1, Private = 2, Public = 4;   // NET_FW_PROFILE_TYPE2

    public static string RuleName(string protocol, int port)
    {
        return $"Ventrail P2P ({protocol} {port})";
    }

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
            object Get(string name, int profile)
            {
                return type.InvokeMember(name, BindingFlags.GetProperty, null, policy, new object[] { profile })!;
            }

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
                    Port = p.Port,
                    Protocol = p.Protocol,
                    Access = PortAccess.Unknown,
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
            {
                (check.Access, check.Detail) = (PortAccess.Allowed, "Windows Firewall is off on this network.");
            }
            else if (blockAllInbound)
            {
                (check.Access, check.Detail) = (PortAccess.Blocked,
                    "Windows is set to block ALL incoming connections on this network (Windows Security > Firewall).");
            }
            else if (block != null)
            {
                (check.Access, check.Detail) = (PortAccess.Blocked, $"Blocked by the firewall rule \"{block.Name}\".");
                check.BlockedByOwnRule = !string.IsNullOrEmpty(block.Application) && AppMatches(block.Application, exePath);
            }
            else if (allow != null)
            {
                (check.Access, check.Detail) = (PortAccess.Allowed, $"Allowed by the firewall rule \"{allow.Name}\".");
            }
            else if (defaultInboundAllow)
            {
                (check.Access, check.Detail) = (PortAccess.Allowed, "This network lets all incoming connections through.");
            }
            else
            {
                (check.Access, check.Detail) = (PortAccess.NeedsPermission,
                    "No firewall rule lets other computers reach Ventrail on this port yet.");
            }

            status.Ports.Add(check);
        }
        return status;
    }

    // "4001", "4000-4010", "80,443,4001", "*" or empty (= any). Keywords like "RPC" never match.
    public static bool PortMatches(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts) || localPorts.Trim() == "*")
        {
            return true;
        }

        foreach (string part in localPorts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out int lo) && int.TryParse(part[(dash + 1)..], out int hi))
            {
                if (port >= lo && port <= hi)
                {
                    return true;
                }
            }
            else if (int.TryParse(part, out int single) && single == port)
            {
                return true;
            }
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
        if (!string.IsNullOrEmpty(r.Service))
        {
            return false;
        }

        bool noProgram = string.IsNullOrWhiteSpace(r.Application) || r.Application == "*";
        if (noProgram && !string.IsNullOrEmpty(r.UserOwner))
        {
            return false;
        }

        bool anyPort = string.IsNullOrWhiteSpace(r.LocalPorts) || r.LocalPorts.Trim() == "*";
        if (noProgram && anyPort && r.Allow)
        {
            return false;
        }

        return true;
    }

    // A rule with no program applies to every program; otherwise it must be Ventrail's exe.
    private static bool AppMatches(string? ruleApp, string exePath)
    {
        if (string.IsNullOrWhiteSpace(ruleApp) || ruleApp == "*")
        {
            return true;
        }

        try
        {
            string a = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ruleApp));
            string b = Path.GetFullPath(exePath);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)   // malformed path in someone else's rule
        {
            return false;
        }
    }

    // ---------- changing (admin prompt) ----------
    //
    // Ventrail asks Windows to run a second copy of ITSELF with admin rights ("helper mode",
    // see RunHelper), so the Windows prompt names Ventrail, with its icon. Earlier versions ran
    // netsh through cmd.exe, and the prompt then said "Windows Command Processor", which is
    // confusing and looks suspicious. The helper changes the rules through the same Windows
    // Firewall API the audit reads, adds nothing else, and exits.

    public const string HelperAllow = "--firewall-allow";
    public const string HelperRemove = "--firewall-remove";

    // Exit codes of the helper.
    public const int HelperOk = 0, HelperBadArguments = 2, HelperFailed = 3;

    private const int NetFwIn = 1, NetFwAllow = 1;   // NET_FW_RULE_DIRECTION / NET_FW_ACTION

    /// <summary>
    /// The allow rules that give permission: one per port, only for Ventrail's exe, only for devices
    /// on the local network, on private + domain networks (+ public if asked). Kept as data so the
    /// exact rules can be tested without touching the firewall.
    /// </summary>
    public static List<FirewallRuleSpec> BuildAllowRules(string exePath, IReadOnlyList<(int Port, string Protocol)> ports,
                                                         bool includePublic)
    {
        return ports.Select(p => new FirewallRuleSpec(
            RuleName(p.Protocol, p.Port), p.Protocol == "UDP" ? Udp : Tcp, p.Port,
            Domain | Private | (includePublic ? Public : 0), exePath)).ToList();
    }

    /// <summary>The command-line arguments for the elevated helper.</summary>
    public static string HelperArguments(bool allow, IReadOnlyList<(int Port, string Protocol)> ports,
                                         bool includePublic = false, bool replaceOwnBlockRules = false)
    {
        var args = new List<string> { allow ? HelperAllow : HelperRemove };
        foreach (var (port, protocol) in ports)
        {
            args.Add($"--{protocol.ToLowerInvariant()} {port}");
        }

        if (allow && includePublic)
        {
            args.Add("--public");
        }

        if (allow && replaceOwnBlockRules)
        {
            args.Add("--replace-own-blocks");
        }

        return string.Join(' ', args);
    }

    public static bool IsHelperCommand(string[] args)
    {
        return args.Length > 0 && (args[0] == HelperAllow || args[0] == HelperRemove);
    }

    /// <summary>
    /// Parses the helper's arguments. Null if anything is wrong: the helper runs with admin
    /// rights, so it only accepts exactly what HelperArguments produces.
    /// </summary>
    public static HelperRequest? ParseHelperArguments(string[] args)
    {
        if (!IsHelperCommand(args))
        {
            return null;
        }

        var request = new HelperRequest { Allow = args[0] == HelperAllow };
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tcp" or "--udp" when i + 1 < args.Length && int.TryParse(args[i + 1], out int port) && port is > 0 and <= 65535:
                    request.Ports.Add((port, args[i] == "--tcp" ? "TCP" : "UDP"));
                    i++;
                    break;
                case "--public" when request.Allow:
                    request.IncludePublic = true;
                    break;
                case "--replace-own-blocks" when request.Allow:
                    request.ReplaceOwnBlockRules = true;
                    break;
                default:
                    return null;
            }
        }
        return request.Ports.Count is > 0 and <= 4 ? request : null;
    }

    /// <summary>
    /// Helper mode: what the elevated copy of Ventrail does, and nothing more. The rules are always
    /// for the running Ventrail.exe itself (never a path passed in), so the helper can't be used to
    /// open the firewall for some other program. Returns the exit code.
    /// </summary>
    public static int RunHelper(string[] args)
    {
        var request = ParseHelperArguments(args);
        string? exe = Environment.ProcessPath;
        if (request is null || string.IsNullOrEmpty(exe))
        {
            return HelperBadArguments;
        }

        try
        {
            if (request.Allow)
            {
                ApplyAllowRules(BuildAllowRules(exe, request.Ports, request.IncludePublic),
                                request.ReplaceOwnBlockRules ? exe : null);
            }
            else
            {
                RemoveRules(request.Ports.Select(p => RuleName(p.Protocol, p.Port)));
            }

            return HelperOk;
        }
        catch (Exception)
        {
            return HelperFailed;
        }
    }

    /// <summary>
    /// Adds the rules through the Windows Firewall API (needs admin rights). Old Ventrail rules with
    /// the same names are replaced so they never pile up. With ownBlocksForExe, inbound rules for
    /// Ventrail's own exe (e.g. the block rule Windows creates when its pop-up is cancelled) are
    /// removed first. Other programs' rules are never touched.
    /// </summary>
    public static void ApplyAllowRules(IReadOnlyList<FirewallRuleSpec> rules, string? ownBlocksForExe)
    {
        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                                                  ?? throw new InvalidOperationException("Windows Firewall API not found"))!;
        dynamic list = policy.Rules;

        if (ownBlocksForExe != null)
        {
            // Rule names aren't unique, so only remove a name if every rule with it is ours.
            var byName = new Dictionary<string, bool>();
            foreach (dynamic r in list)
            {
                string name = (string?)r.Name ?? "";
                bool ours = (int)r.Direction == NetFwIn && AppMatches((string?)r.ApplicationName, ownBlocksForExe)
                            && !string.IsNullOrWhiteSpace((string?)r.ApplicationName);
                byName[name] = byName.TryGetValue(name, out bool all) ? all && ours : ours;
            }
            RemoveRules(byName.Where(n => n.Value).Select(n => n.Key), list);
        }

        RemoveRules(rules.Select(r => r.Name), list);
        foreach (var spec in rules)
        {
            dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
            rule.Name = spec.Name;
            rule.Description = "Lets Ventrail share versions with computers on the same network.";
            rule.ApplicationName = spec.Program;
            rule.Protocol = spec.Protocol;               // must be set before the port
            rule.LocalPorts = spec.Port.ToString();
            rule.RemoteAddresses = spec.RemoteAddresses;
            rule.Direction = NetFwIn;
            rule.Action = NetFwAllow;
            rule.Profiles = spec.Profiles;
            rule.Enabled = true;
            list.Add(rule);
        }
    }

    /// <summary>Removes every rule with these names (needs admin rights).</summary>
    public static void RemoveRules(IEnumerable<string> names, dynamic? list = null)
    {
        if (list is null)
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                                                      ?? throw new InvalidOperationException("Windows Firewall API not found"))!;
            list = policy.Rules;
        }
        foreach (string name in names.Distinct())
        {
            // Remove() deletes one rule with that name at a time; Item() throws once none are left.
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    _ = list!.Item(name);
                }
                catch (Exception)
                {
                    break;
                }
                list!.Remove(name);
            }
        }
    }

    /// <summary>
    /// Runs Ventrail's helper mode through the Windows admin prompt (the prompt shows Ventrail).
    /// False if the user said No to the prompt.
    /// </summary>
    public static bool RunElevatedHelper(string arguments, string? exePath = null)
    {
        string exe = exePath ?? Environment.ProcessPath
                     ?? throw new VentrailException("Ventrail could not find its own program file.");
        var info = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = true,          // required for "runas" (the admin prompt)
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var process = Process.Start(info)
                                ?? throw new VentrailException("Windows could not start the firewall settings helper.");
            if (!process.WaitForExit(60_000))
            {
                throw new VentrailException("Changing the firewall settings took too long. Please try again.");
            }

            if (process.ExitCode != HelperOk)
            {
                throw new VentrailException($"Windows did not accept the firewall change (code {process.ExitCode}). " +
                                            "Your organisation may manage these settings.");
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: "No" on the admin prompt
        {
            return false;
        }
    }
}

/// <summary>One Windows Firewall allow rule that Ventrail adds (inbound, one port, local network only).</summary>
public record FirewallRuleSpec(string Name, int Protocol, int Port, int Profiles, string Program,
                               string RemoteAddresses = "LocalSubnet");

/// <summary>What the elevated helper was asked to do.</summary>
public class HelperRequest
{
    public bool Allow { get; set; }
    public List<(int Port, string Protocol)> Ports { get; } = new();
    public bool IncludePublic { get; set; }
    public bool ReplaceOwnBlockRules { get; set; }
}

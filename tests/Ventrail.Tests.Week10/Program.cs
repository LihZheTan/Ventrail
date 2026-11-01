using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;
using Ventrail.Services.Mock;
using Ventrail.ViewModels;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); }

const string Exe = @"C:\Apps\Ventrail\Ventrail.exe";
var ports = new[] { (4001, "TCP"), (4002, "UDP") };
FirewallRuleInfo Rule(string name, bool allow = true, int proto = 6, string? lp = "4001", int prof = 0x7FFFFFFF, string? app = Exe, bool en = true, bool inbound = true)
    => new(name, en, inbound, allow, proto, lp, prof, app);
FirewallStatus Eval(params FirewallRuleInfo[] rules) => FirewallAuditor.Evaluate(Exe, 2, true, false, false, rules, ports);
PortAccess Tcp(FirewallStatus s) => s.Ports[0].Access;

// ---- port matching ----
Check("ports: exact, list, range, any", FirewallAuditor.PortMatches("4001", 4001) && FirewallAuditor.PortMatches("80, 4001", 4001)
      && FirewallAuditor.PortMatches("4000-4010", 4001) && FirewallAuditor.PortMatches("*", 4001) && FirewallAuditor.PortMatches(null, 4001));
Check("ports: no false matches", !FirewallAuditor.PortMatches("40010", 4001) && !FirewallAuditor.PortMatches("4002-4010", 4001)
      && !FirewallAuditor.PortMatches("RPC", 4001) && !FirewallAuditor.PortMatches("80,443", 4001));

// ---- rule evaluation (like Windows does) ----
Check("no rules -> needs permission", Tcp(Eval()) == PortAccess.NeedsPermission && !Eval().AllAllowed);
Check("our allow rule -> allowed", Tcp(Eval(Rule("Ventrail P2P (TCP 4001)"))) == PortAccess.Allowed);
Check("both ports allowed -> AllAllowed + HasVentrailRules",
      Eval(Rule("Ventrail P2P (TCP 4001)"), Rule("Ventrail P2P (UDP 4002)", proto: 17, lp: "4002")) is var both && both.AllAllowed && both.HasVentrailRules);
Check("block beats allow", Tcp(Eval(Rule("allow"), Rule("blk", allow: false))) == PortAccess.Blocked);
var ownBlock = Eval(Rule("ventrail.exe", allow: false, lp: null, proto: 256));
Check("Windows' own block rule for Ventrail is recognised", Tcp(ownBlock) == PortAccess.Blocked && ownBlock.Ports[0].BlockedByOwnRule);
Check("someone else's block rule is not ours", !Eval(Rule("corp", allow: false, app: null)).Ports[0].BlockedByOwnRule);
Check("rule for another program ignored", Tcp(Eval(Rule("x", app: @"C:\Other\app.exe"))) == PortAccess.NeedsPermission);
Check("rule with any program applies", Tcp(Eval(Rule("x", app: null))) == PortAccess.Allowed);
Check("Store-app rule (Game Bar style) ignored", Tcp(Eval(new FirewallRuleInfo("Game Bar", true, true, true, 256, null, 7, null, null, "S-1-5-21-1"))) == PortAccess.NeedsPermission);
Check("any-program any-port allow ignored (conservative)", Tcp(Eval(Rule("open", proto: 256, lp: null, app: null))) == PortAccess.NeedsPermission);
Check("service-only rule ignored", Tcp(Eval(new FirewallRuleInfo("RDP", true, true, true, 6, "4001", 7, null, "TermService"))) == PortAccess.NeedsPermission);
Check("rule for other network type ignored", Tcp(Eval(Rule("x", prof: 4))) == PortAccess.NeedsPermission);
Check("disabled / outbound rules ignored", Tcp(Eval(Rule("x", en: false), Rule("y", inbound: false))) == PortAccess.NeedsPermission);
Check("UDP rule doesn't open TCP port", Tcp(Eval(Rule("x", proto: 17))) == PortAccess.NeedsPermission);
Check("any-protocol rule opens both", Eval(Rule("x", proto: 256, lp: "4001-4002")).AllAllowed);
Check("firewall off -> allowed", FirewallAuditor.Evaluate(Exe, 2, false, false, false, new List<FirewallRuleInfo>(), ports).AllAllowed);
Check("block-all-incoming -> blocked even with allow rule",
      Tcp(FirewallAuditor.Evaluate(Exe, 2, true, true, false, new[] { Rule("x") }, ports)) == PortAccess.Blocked);
Check("network default allow -> allowed", FirewallAuditor.Evaluate(Exe, 2, true, false, true, new List<FirewallRuleInfo>(), ports).AllAllowed);
var pub = FirewallAuditor.Evaluate(Exe, 4, true, false, false, new List<FirewallRuleInfo>(), ports);
Check("public network detected", pub.IsPublicNetwork && pub.NetworkProfile == "Public");
Check("env-var paths in rules are expanded", Tcp(FirewallAuditor.Evaluate(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\x.exe"), 2, true, false, false,
      new[] { Rule("x", app: @"%SystemRoot%\x.exe") }, ports)) == PortAccess.Allowed);

// ---- netsh scripts ----
string allowScript = FirewallAuditor.BuildAllowScript(Exe, ports, includePublic: false, replaceOwnBlockRules: false);
Console.WriteLine("   " + allowScript);
Check("allow script: both rules, local network only, private/domain",
      allowScript.Contains("name=\"Ventrail P2P (TCP 4001)\" dir=in action=allow protocol=TCP localport=4001")
      && allowScript.Contains("protocol=UDP localport=4002") && allowScript.Contains("remoteip=localsubnet")
      && allowScript.Contains("profile=private,domain ") && allowScript.Contains($"program=\"{Exe}\""));
Check("allow script replaces old Ventrail rules first", allowScript.IndexOf("delete rule name=\"Ventrail P2P (TCP 4001)\"") < allowScript.IndexOf("add rule"));
Check("allow script never deletes by program unless asked", !allowScript.Contains("name=all"));
string pubScript = FirewallAuditor.BuildAllowScript(Exe, ports, true, true);
Check("public + replace own blocks", pubScript.Contains("profile=private,domain,public") && pubScript.StartsWith($"netsh advfirewall firewall delete rule name=all dir=in program=\"{Exe}\""));
Check("remove script deletes only Ventrail's rules", FirewallAuditor.BuildRemoveScript(ports) ==
      "netsh advfirewall firewall delete rule name=\"Ventrail P2P (TCP 4001)\" & netsh advfirewall firewall delete rule name=\"Ventrail P2P (UDP 4002)\"");

// ---- real audit on this PC (read only) ----
var sw = System.Diagnostics.Stopwatch.StartNew();
var real = FirewallAuditor.Audit(Environment.ProcessPath!, ports);
Console.WriteLine($"   this PC: {real.NetworkProfile} network, firewall on={real.FirewallOn}, {real.Summary} ({sw.ElapsedMilliseconds} ms)");
foreach (var p in real.Ports) Console.WriteLine($"     {p.Name}: {p.Label} - {p.Detail}");
Check("real audit reads Windows Firewall without admin", real.Error == null && real.Ports.Count == 2, real.Error ?? "");
Check("real audit under 10 s", sw.ElapsedMilliseconds < 10000);
Check("this PC: no Ventrail rules yet -> needs permission", real.Ports.All(p => p.Access == PortAccess.NeedsPermission));

// ---- listening (loopback + spare ports, so Windows Firewall never asks) ----
string dataDir = Path.Combine(Path.GetTempPath(), "ventrail-w10-" + Guid.NewGuid().ToString("N")[..6]);
var node = new NetworkNodeCoordinator(IPAddress.Loopback, 45101, 45102, dataDir);
Check("peer id format", node.LocalPeerId.StartsWith("vt-") && node.LocalPeerId.Length == 15, node.LocalPeerId);
Check("peer id stays the same between runs", new NetworkNodeCoordinator(IPAddress.Loopback, 45101, 45102, dataDir).LocalPeerId == node.LocalPeerId);
Check("not listening at start", !node.IsListening);
Check("StartListening", node.StartListening() && node.IsListening);
Check("StartListening twice is harmless", node.StartListening());

using (var c = new TcpClient())
{
    await c.ConnectAsync(IPAddress.Loopback, 45101);
    using var reader = new StreamReader(c.GetStream());
    string? hello = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
    Check("TCP 4001 answers with hello + peer id", hello == $"VENTRAIL/1 {node.LocalPeerId}", hello ?? "");
}
using (var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
{
    await u.SendAsync(Encoding.UTF8.GetBytes("hello?"), new IPEndPoint(IPAddress.Loopback, 45102));   // must be ignored
    await u.SendAsync(Encoding.UTF8.GetBytes("VENTRAIL?"), new IPEndPoint(IPAddress.Loopback, 45102));
    var reply = await u.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
    string text = Encoding.UTF8.GetString(reply.Buffer);
    Check("UDP 4002 answers the discovery question (and ignores other packets)", text.StartsWith($"VENTRAIL! {node.LocalPeerId} 45101 "), text);
}
var twin = new NetworkNodeCoordinator(IPAddress.Loopback, 45101, 45103, dataDir);
try { twin.StartListening(); Check("port in use -> friendly error", false); }
catch (VentrailException ex) { Check("port in use -> friendly error", ex.Message.StartsWith("Port 45101 is already used"), ex.Message); }
Check("failed start leaves nothing half-open", !twin.IsListening);
node.StopListening();
Check("StopListening frees the ports", !node.IsListening && twin.StartListening());
twin.StopListening();
bool refused;
try { using var c2 = new TcpClient(); await c2.ConnectAsync(IPAddress.Loopback, 45101); refused = false; }
catch (SocketException) { refused = true; }
Check("after stop, nobody listens", refused);
Directory.Delete(dataDir, true);

// ---- ViewModel flow (Fig 3.5) ----
MainDashboardViewModel NewVm(INetworkNodeCoordinator net) => new(new MockRepositoryManager(),
    new IntelligentStorageEngine(@"C:\nope.zip"), net, new SpecializedFileHandler(), new InMemoryRecentRepositoryStore(), true);
async Task WaitFor(Func<bool> cond) { for (int i = 0; i < 50 && !cond(); i++) await Task.Delay(50); }

var mock = new MockNetworkNodeCoordinator();
var vm = NewVm(mock);
await vm.InitializeDashboardAsync(@"C:\Demo\Thesis");
vm.CurrentPage = AppPage.Network;
await WaitFor(() => vm.Firewall != null);
Check("opening the Network page audits the firewall", vm.TcpCheck?.Access == PortAccess.NeedsPermission && vm.NetworkStateText == "Offline");

var share = vm.ShareRepositoryCommand.ExecuteAsync(null);
await WaitFor(() => vm.IsDialogOpen);
Check("missing permission -> asks first", vm.IsDialogOpen && vm.DialogTitle == "Allow peer-to-peer sharing?");
vm.CancelDialogCommand.Execute(null);
await share;
Check("denied -> abort, still offline, no rules", !vm.IsSharingEnabled && !mock.IsListening && vm.TcpCheck!.Access == PortAccess.NeedsPermission);
Check("denied is logged", vm.ActivityLog.Any(a => a.Text.Contains("firewall permission not given")));

share = vm.ShareRepositoryCommand.ExecuteAsync(null);
await WaitFor(() => vm.IsDialogOpen);
vm.ConfirmDialogCommand.Execute(null);
await share;
Check("allowed -> rules added, listening, online", vm.IsSharingEnabled && mock.IsListening && vm.Firewall!.AllAllowed && vm.NetworkStateText == "Online");
Check("share works after permission (Week 11 now finds peers)", vm.StatusText.Contains("shared"), vm.StatusText);

vm.IsSharingEnabled = false;   // the switch flips through its binding, then runs the command
await vm.ToggleSharingCommand.ExecuteAsync(null);
Check("switch off -> stops listening", !mock.IsListening && vm.NetworkStateText == "Offline");
vm.IsSharingEnabled = true;
await vm.ToggleSharingCommand.ExecuteAsync(null);
Check("switch on again -> no second prompt (already allowed)", mock.IsListening && !vm.IsDialogOpen);
Check("Remove permission link shows", vm.CanRemoveFirewallPermission);
var remove = vm.RemoveFirewallPermissionCommand.ExecuteAsync(null);
await WaitFor(() => vm.IsDialogOpen);
vm.ConfirmDialogCommand.Execute(null);
await remove;
Check("remove permission -> offline + needs permission", !mock.IsListening && !vm.IsSharingEnabled && vm.TcpCheck!.Access == PortAccess.NeedsPermission);

// User clicks "Allow" in Ventrail but "No" on Windows' admin prompt
var declining = new DecliningNode();
var vm2 = NewVm(declining);
await vm2.InitializeDashboardAsync(@"C:\Demo\Thesis");
vm2.IsSharingEnabled = true;
var toggle = vm2.ToggleSharingCommand.ExecuteAsync(null);
await WaitFor(() => vm2.IsDialogOpen);
Check("public network warning in the prompt", vm2.DialogMessage.Contains("Public"));
vm2.ConfirmDialogCommand.Execute(null);
await toggle;
Check("UAC declined -> abort, switch back off, not listening", !vm2.IsSharingEnabled && !declining.IsListening
      && vm2.ToastMessage.Contains("prompt was closed"));
Check("asked for public-network rules because network is Public", declining.AskedPublic);

Console.WriteLine($"\n{pass} passed, {fail} failed.");

class DecliningNode : MockNetworkNodeCoordinator, INetworkNodeCoordinator
{
    public bool AskedPublic;
    FirewallStatus INetworkNodeCoordinator.CheckFirewall()
    {
        var s = CheckFirewall();
        s.NetworkProfile = "Public";
        s.IsPublicNetwork = true;
        return s;
    }
    bool INetworkNodeCoordinator.RequestFirewallPermission(bool includePublic) { AskedPublic = includePublic; return false; }
}

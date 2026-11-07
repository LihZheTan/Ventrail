using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LibGit2Sharp;
using Ventrail.Converters;
using Ventrail.Models;
using Ventrail.Services.Implementations;
using Ventrail.Services.Mock;
using Ventrail.Themes;
using Ventrail.ViewModels;

// Redesign (GitHub Desktop-style shell, light/dark themes): the ViewModel parts behind it, and a
// static check that both colour themes are complete.

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); }
async Task WaitFor(Func<bool> cond) { for (int i = 0; i < 100 && !cond(); i++) await Task.Delay(50); }

// ---------- themes: both palettes define the same colours, and every colour used exists ----------
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
HashSet<string> Keys(string file) => XDocument.Load(Path.Combine(TestPaths.Project, "Themes", file))
    .Root!.Elements().Select(e => (string?)e.Attribute(x + "Key")).Where(k => k != null).Select(k => k!).ToHashSet();
var dark = Keys("Colors.Dark.xaml");
var light = Keys("Colors.Light.xaml");
Check("light and dark define the same colour keys", dark.SetEquals(light),
      $"only dark: {string.Join(",", dark.Except(light))} only light: {string.Join(",", light.Except(dark))}");

var xamlFiles = Directory.GetFiles(TestPaths.Project, "*.xaml", SearchOption.AllDirectories)
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                !f.Contains("_to_delete"));
var used = xamlFiles.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\{DynamicResource (\w+)\}").Select(m => m.Groups[1].Value)).ToHashSet();
Check("every {DynamicResource} colour exists in both themes", used.All(dark.Contains),
      $"missing: {string.Join(",", used.Where(k => !dark.Contains(k)))} ({used.Count} keys used)");
var brushKeys = dark.Where(k => k.EndsWith("Brush")).ToHashSet();
var staticColour = xamlFiles.SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\{StaticResource (\w+)\}")
        .Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)))
    .Where(u => brushKeys.Contains(u.Key)).ToList();
Check("no colour is used as StaticResource (it wouldn't change with the theme)", staticColour.Count == 0,
      string.Join(", ", staticColour.Select(u => $"{u.File}:{u.Key}").Distinct()));

ThemeManager.Apply(AppTheme.Dark, save: false);   // no WPF Application in tests: must not throw
Check("ThemeManager without a WPF app does nothing (tests, tools)", ThemeManager.Chosen == AppTheme.Dark);

// ---------- small model / converter helpers ----------
Check("FolderPart: folder shown greyed before the file name",
      new FileAsset { FilePath = "figures/chart.png", FileName = "chart.png" }.FolderPart == "figures/" &&
      new FileAsset { FilePath = "notes.txt", FileName = "notes.txt" }.FolderPart == "");
var folderName = new FolderNameConverter();
Check("FolderNameConverter: C:\\Users\\me\\Thesis\\ -> Thesis",
      (string)folderName.Convert(@"C:\Users\me\Thesis\", typeof(string), null!, CultureInfo.InvariantCulture) == "Thesis");

// ---------- ViewModel with the demo repository ----------
MainDashboardViewModel NewVm(Ventrail.Services.Contracts.IRepositoryManager repo) => new(repo,
    new IntelligentStorageEngine(@"C:\nope.zip"), new MockNetworkNodeCoordinator(), new SpecializedFileHandler(),
    new InMemoryRecentRepositoryStore(), true);

var vm = NewVm(new MockRepositoryManager());
await vm.InitializeDashboardAsync(@"C:\Demo\Thesis");
Check("commit button names the branch", vm.CommitButtonText == "Commit to main", vm.CommitButtonText);
Check("toolbar shows when the folder was last scanned", vm.LastScanText.StartsWith("Last scanned at "), vm.LastScanText);

vm.SelectedFile = vm.Changes.First(f => f.FileName == "todo.md");
await WaitFor(() => !vm.IsInlineDiffLoading && vm.InlineDiff != null);
Check("selecting a changed file shows its diff beside the list", vm.HasInlineDiffLines && vm.InlineDiff!.FilePath == "notes/todo.md",
      vm.InlineDiff?.Summary ?? "");
vm.SelectedFile = vm.Changes.First(f => f.FileName == "flowchart_v2.png");
vm.SelectedFile = vm.Changes.First(f => f.FileName == "todo.md");   // quick changes: the last one wins
await WaitFor(() => !vm.IsInlineDiffLoading);
Check("quick selection changes: the diff matches the last selected file", vm.InlineDiff?.FilePath == "notes/todo.md");
vm.SelectedFile = null;
Check("nothing selected -> no diff", vm.InlineDiff is null && !vm.HasInlineDiffLines);

vm.SetThemeCommand.Execute(AppTheme.Light);
Check("View > Theme > Light sets the theme", vm.CurrentTheme == AppTheme.Light);
vm.SetThemeCommand.Execute(AppTheme.System);
Check("View > Theme > Match Windows", vm.CurrentTheme == AppTheme.System);

vm.NavigateToNetworkCommand.Execute(null);
Check("toolbar 'Share on network' opens the Peer-to-peer page", vm.CurrentPage == AppPage.Network);
vm.NavigateToWelcomeCommand.Execute(null);
Check("View > Home opens the Welcome page", vm.CurrentPage == AppPage.Welcome);

var about = vm.ShowAboutCommand.ExecuteAsync(null);
await WaitFor(() => vm.IsDialogOpen);
Check("Help > About shows an info dialog (no cancel button)", vm.DialogTitle == "About Ventrail" && vm.DialogCancelText == ""
      && vm.DialogMessage.Contains("Version 1.0"), vm.DialogMessage.Split('\n')[0]);
vm.ConfirmDialogCommand.Execute(null);
await about;

// ---------- summary + description with a real Git repository ----------
string root = Path.Combine(Path.GetTempPath(), "vtr-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root, "a.txt"), "one");
var real = NewVm(new RepositoryManager());
await real.InitializeDashboardAsync(root);
await real.StageAllCommand.ExecuteAsync(null);
real.CommitMessage = "Add chapter outline";
real.CommitDescription = "Covers sections 1-3.\nMore to come.";
Check("commit button follows the real branch", real.CommitButtonText.StartsWith("Commit to "), real.CommitButtonText);
await real.CommitCommand.ExecuteAsync(null);
using (var repo = new Repository(root))
    Check("summary + description become one Git message (blank line between)",
          repo.Head.Tip.Message.TrimEnd('\n') == "Add chapter outline\n\nCovers sections 1-3.\nMore to come.",
          repo.Head.Tip.Message.Replace("\n", "\\n"));
Check("both boxes clear after committing", real.CommitMessage == "" && real.CommitDescription == "");

File.WriteAllText(Path.Combine(root, "a.txt"), "two");
await real.RefreshCommand.ExecuteAsync(null);
await real.StageAllCommand.ExecuteAsync(null);
real.CommitMessage = "Only a summary";
await real.CommitCommand.ExecuteAsync(null);
using (var repo = new Repository(root))
    Check("summary only -> message is just the summary", repo.Head.Tip.MessageShort == "Only a summary" && !repo.Head.Tip.Message.Contains("\n\n"));
Check("history list shows only the summary line", real.History[0].Message == "Only a summary" && !real.History[0].HasDescription
      && real.History[1].Message == "Add chapter outline");
Check("the description is loaded for the version details", real.History[1].Description == "Covers sections 1-3.\nMore to come.",
      real.History[1].Description.Replace("\n", "\\n"));

try { foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(root, true); } catch (Exception) { }
Console.WriteLine($"\n{pass} passed, {fail} failed.");

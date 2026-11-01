using System.IO;
using Ventrail.Models;
using Ventrail.Services.AI;
using Ventrail.Services.Implementations;
using Ventrail.Services.Mock;
using Ventrail.ViewModels;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); }

FileAsset F(string path, string status, long size = 1000, bool? heavy = null, bool compressed = false) => new()
{
    FilePath = path, FileName = Path.GetFileName(path), TrackStatus = status, SizeInBytes = size, IsStaged = true,
    Classification = heavy is null ? null : new FileClassification
    { IsHeavy = heavy.Value, Probability = heavy.Value ? 0.97f : 0.02f, FormatName = "file", IsAlreadyCompressed = compressed, SizeInBytes = size }
};
string Msg(params FileAsset[] files) { string m = SuggestionWriter.WriteMessage(files); Console.WriteLine($"   \"{m}\" ({m.Length})"); return m; }

// ---- commit messages ----
Check("one modified file", Msg(F("Chapter3.docx", "Modified")) == "Update Chapter3.docx");
Check("one added file", Msg(F("figures/flow.png", "Added")) == "Add flow.png");
Check("one deleted file", Msg(F("old.txt", "Deleted")) == "Remove old.txt");
Check("same-kind files counted + folder", Msg(F("figures/a.png", "Added"), F("figures/b.jpg", "Added"), F("figures/c.png", "Added"))
      == "Add 3 pictures in figures");
Check("two mixed files named", Msg(F("a.docx", "Modified"), F("b.xlsx", "Modified")) == "Update a.docx and b.xlsx");
Check("three mixed: biggest named", Msg(F("a.docx", "Modified", 900), F("big.xlsx", "Modified", 5000), F("c.md", "Modified", 10))
      == "Update big.xlsx and 2 other files");
var demo = new[]
{
    F("Chapter3_Methodology.docx", "Modified", 482_000), F("figures/flowchart_v2.png", "Added", 1_250_000),
    F("data/survey_results.xlsx", "Modified", 96_500), F("notes/old_ideas.txt", "Deleted", 0),
    F("notes/todo.md", "Modified", 412), F("slides/scan_sample.svs", "Added", 2_400_000_000)
};
string demoMsg = Msg(demo);
Check("demo set: clauses in order, under 72 chars",
      demoMsg.StartsWith("Update ") && demoMsg.Contains(", add ") && demoMsg.Contains(" and remove ") && demoMsg.Length <= 72);
var many = Enumerable.Range(0, 40).Select(i => F($"folder{i % 7}/a_really_long_file_name_number_{i}.{(i % 3 == 0 ? "png" : "docx")}",
                                                   i % 4 == 0 ? "Added" : i % 4 == 1 ? "Deleted" : i % 4 == 2 ? "Renamed" : "Modified")).ToArray();
Check("40 files: still under 72 chars", Msg(many).Length <= 72);
Check("untracked staged file counts as update", Msg(F("x.txt", "Untracked")) == "Update x.txt");
Check("no files -> empty", SuggestionWriter.WriteMessage(Array.Empty<FileAsset>()) == "");
Check("long single name falls back to counts", Msg(F(new string('n', 80) + ".txt", "Modified")) == "Update 1 text file");

// ---- storage advice ----
var s = SuggestionWriter.Suggest(new[]
{
    F("photo.jpg", "Added", 4_000_000, heavy: true, compressed: true),
    F("notes.txt", "Modified", 2000, heavy: false),
    F("scan.svs", "Added", 2_400_000_000, heavy: true),
    F("gone.mp4", "Deleted", 0, heavy: true),
    F("raw.bin", "Added", 80_000_000, heavy: null)   // no verdict: Week 6 size rule
});
foreach (var a in s.Advice) Console.WriteLine($"   advice [{a.Level}] {a.Text}");
Console.WriteLine($"   summary: {s.Summary}");
Check("advice only for heavy (and large unclassified) files", s.Advice.Count == 3 && s.Advice.All(a => a.FileName != "notes.txt"));
Check("deleted file gets no advice", s.Advice.All(a => a.FileName != "gone.mp4"));
Check("warnings first, biggest first", s.Advice[0].FileName == "scan.svs" && s.Advice[1].FileName == "raw.bin");
Check("very large files suggested to leave out", s.LeaveOutPaths.SequenceEqual(new[] { "scan.svs", "raw.bin" }));
Check("compressed file explained", s.Advice[2].Text.Contains("already compressed"));
Check("summary mentions saving", s.Summary.StartsWith("3 heavy files") && s.Summary.Contains("Leaving 2 files out"));
Check("no heavy -> calm summary", SuggestionWriter.Suggest(new[] { F("a.txt", "Modified", 10, heavy: false) }).Summary.StartsWith("No heavy"));

// ---- blocks / tips ----
Check("block count rounds up", SuggestionWriter.BlockCount(1) == 1 && SuggestionWriter.BlockCount(1024 * 1024) == 1
      && SuggestionWriter.BlockCount(1024 * 1024 + 1) == 2 && SuggestionWriter.BlockCount(0) == 1);
string tip = SuggestionWriter.StorageTip(new FileClassification { IsHeavy = true, IsAlreadyCompressed = true }, 2_400_000_000);
Console.WriteLine("   tip: " + tip);
Check("heavy tip mentions blocks + milestones + very large", tip.Contains("2,289 blocks") && tip.Contains("milestones") && tip.Contains("very large"));
Check("light tip is short and calm", SuggestionWriter.StorageTip(new FileClassification { IsHeavy = false }, 500).Contains("as often as you like"));
Check("no verdict, small file -> no tip", SuggestionWriter.StorageTip(null, 500) == "");

// ---- engine ----
var engine = new IntelligentStorageEngine(TestPaths.Model);
string rec = engine.GenerateRecommendation("video.mp4", 300_000_000);
Console.WriteLine("   GenerateRecommendation(mp4, 300 MB): " + rec);
Check("GenerateRecommendation works (no more NotImplemented)", rec.Contains("blocks") && rec.Contains("milestones"));
Check("GenerateRecommendation accepts bare extension", engine.GenerateRecommendation("txt", 2_000).Contains("as often"));
var noModel = new IntelligentStorageEngine(@"C:\nope\missing.zip");
Check("no model: recommendation falls back, no crash", noModel.GenerateRecommendation(".zip", 100_000_000).Contains("full copy"));

// ---- end to end: ViewModel + demo repo ----
var vm = new MainDashboardViewModel(new MockRepositoryManager(), engine, new NetworkNodeCoordinator(),
    new SpecializedFileHandler(), new InMemoryRecentRepositoryStore(), true);
await vm.InitializeDashboardAsync(@"C:\Demo\Thesis");
async Task Idle() { for (int i = 0; i < 50 && (vm.IsBusy || vm.Changes.Any(c => c.TrackStatus != "Deleted" && c.Classification == null)); i++) await Task.Delay(100); }
await Idle();
Check("nothing ticked -> no suggestion card", !vm.IsSuggestionVisible && vm.Suggestion is null);

vm.StageAllCommand.Execute(null);
await Idle(); await Task.Delay(200);
Console.WriteLine($"   card: \"{vm.Suggestion?.Message}\" | {vm.Suggestion?.Summary}");
Check("all ticked -> card visible with message", vm.IsSuggestionVisible && !string.IsNullOrEmpty(vm.Suggestion!.Message));
Check("card flags the 2.4 GB slide", vm.Suggestion!.LeaveOutPaths.Contains("slides/scan_sample.svs"));

vm.SelectedFile = vm.Changes.Single(c => c.FileName == "scan_sample.svs");
Console.WriteLine("   inspector tip: " + vm.RecommendationTip);
Check("inspector tip for heavy file", vm.HasRecommendationTip && vm.RecommendationTip.Contains("blocks"));
vm.SelectedFile = vm.Changes.Single(c => c.TrackStatus == "Deleted");
Check("deleted file -> no tip", !vm.HasRecommendationTip);

vm.EditSuggestionManuallyCommand.Execute(null);
Check("Edit Manually: fills empty box, hides card", vm.CommitMessage == vm.Suggestion!.Message && !vm.IsSuggestionVisible);
vm.CommitMessage = "My own words";
vm.ToggleStageCommand.Execute(null);   // null = no-op; key unchanged
Check("card stays hidden while ticked files are the same", !vm.IsSuggestionVisible);

var todo = vm.Changes.Single(c => c.FileName == "todo.md");
todo.IsStaged = false;
await vm.ToggleStageCommand.ExecuteAsync(todo);
await Idle();
Check("unticking a file brings the card back", vm.IsSuggestionVisible);
Check("Edit Manually keeps a typed message", ((Func<bool>)(() => { vm.EditSuggestionManuallyCommand.Execute(null); return vm.CommitMessage == "My own words"; }))());

// Accept: un-dismiss by changing the ticked set again
var todo2 = vm.Changes.Single(c => c.FileName == "todo.md");
todo2.IsStaged = true;
await vm.ToggleStageCommand.ExecuteAsync(todo2);
await Idle();
await vm.AcceptSuggestionCommand.ExecuteAsync(null);
await Idle();
var svs = vm.Changes.Single(c => c.FileName == "scan_sample.svs");
Console.WriteLine($"   after accept: \"{vm.CommitMessage}\", svs ticked={svs.IsStaged}, staged={vm.StagedCount}");
Check("Accept unticks the 2.4 GB slide", !svs.IsStaged);
Check("Accept fills message without the left-out file", !vm.CommitMessage.Contains("scan_sample") && vm.CommitMessage.Length > 0
      && vm.CommitMessage == vm.Suggestion!.Message);
Check("Accept hides the card", !vm.IsSuggestionVisible);
await vm.CommitCommand.ExecuteAsync(null);
await Idle();
Console.WriteLine($"   latest version: \"{vm.History[0].Message}\"");
Check("commit uses the AI message", vm.History[0].Message == vm.History[0].Message.Trim() && !vm.History[0].Message.Contains("scan_sample"));

// Only the huge file ticked: Accept must not untick everything
var vm2 = new MainDashboardViewModel(new MockRepositoryManager(), engine, new NetworkNodeCoordinator(),
    new SpecializedFileHandler(), new InMemoryRecentRepositoryStore(), true);
await vm2.InitializeDashboardAsync(@"C:\Demo\Thesis");
var only = vm2.Changes.Single(c => c.FileName == "scan_sample.svs");
only.IsStaged = true;
await vm2.ToggleStageCommand.ExecuteAsync(only);
for (int i = 0; i < 50 && vm2.Suggestion?.HasAdvice != true; i++) await Task.Delay(100);
await vm2.AcceptSuggestionCommand.ExecuteAsync(null);
Check("Accept never unticks the last file", vm2.Changes.Single(c => c.FileName == "scan_sample.svs").IsStaged
      && vm2.CommitMessage == "Add scan_sample.svs");

Console.WriteLine($"\n{pass} passed, {fail} failed.");

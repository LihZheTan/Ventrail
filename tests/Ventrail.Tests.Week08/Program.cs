using System.Diagnostics;
using System.IO;
using System.Text;
using Ventrail.Models;
using Ventrail.Services.Implementations;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); }

string model = TestPaths.Model;
var engine = new IntelligentStorageEngine(model);
var sw = Stopwatch.StartNew();
Check("model loads", engine.IsModelLoaded, engine.LoadError ?? "");
Console.WriteLine($"   load time: {sw.ElapsedMilliseconds} ms");

string dir = Path.Combine(Path.GetTempPath(), "ventrail-w8-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(dir);
var rng = new Random(7);
string Make(string name, byte[] header, long size, bool randomBody)
{
    string p = Path.Combine(dir, name);
    using var fs = File.Create(p);
    fs.Write(header);
    var buf = new byte[1024 * 1024];
    long left = size - header.Length;
    while (left > 0)
    {
        if (randomBody) rng.NextBytes(buf); else Array.Clear(buf);
        int n = (int)Math.Min(left, buf.Length);
        fs.Write(buf, 0, n);
        left -= n;
    }
    return p;
}
string MakeText(string name, long size)
{
    var sb = new StringBuilder();
    while (sb.Length < size) sb.Append($"2026-10-27 12:{rng.Next(60):00}:{rng.Next(60):00} INFO request {rng.Next(100000)} ok in {rng.Next(900)} ms\r\n");
    string p = Path.Combine(dir, name);
    File.WriteAllText(p, sb.ToString(0, (int)size));
    return p;
}

byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0 };
byte[] mp4 = { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' };
byte[] zip = { 0x50, 0x4B, 0x03, 0x04 };

FileClassification C(string path) => engine.ClassifyFile(path, 0)!;
void Show(string label, FileClassification c) => Console.WriteLine($"   {label,-28} {c.Label} p={c.Probability:F3} ({c.FormatName})");

var photoBig = C(Make("holiday.jpg", jpeg, 4_000_000, true));   Show("4 MB JPEG", photoBig);
var photoSmall = C(Make("icon.jpg", jpeg, 150_000, true));      Show("150 KB JPEG", photoSmall);
var video = C(Make("lecture.mp4", mp4, 30_000_000, true));      Show("30 MB MP4", video);
var docx = C(Make("thesis.docx", zip, 400_000, true));          Show("400 KB DOCX", docx);
var log = C(MakeText("server.log", 3_000_000));                 Show("3 MB text log", log);
var hugeLog = C(MakeText("huge.log", 40_000_000));              Show("40 MB text log", hugeLog);
var note = C(MakeText("notes.txt", 2_000));                     Show("2 KB text", note);

Check("4 MB JPEG -> heavy", photoBig.IsHeavy);
Check("150 KB JPEG -> light", !photoSmall.IsHeavy);
Check("30 MB video -> heavy", video.IsHeavy && video.IsAlreadyCompressed);
Check("400 KB Word file -> light", !docx.IsHeavy && docx.FormatName == "Word document");
Check("3 MB text log -> light (compresses well)", !log.IsHeavy);
Check("40 MB text log -> heavy", hugeLog.IsHeavy);
Check("2 KB note -> light", !note.IsHeavy);
Check("real files are not estimates", !photoBig.IsEstimate);

// Files that are not on disk (demo data) -> estimate from extension + size
var demoSvs = engine.ClassifyFile(@"C:\Demo\Thesis\slides\scan_sample.svs", 2_400_000_000)!;
var demoDocx = engine.ClassifyFile(@"C:\Demo\Thesis\Chapter3_Methodology.docx", 482_000)!;
Show("demo 2.4 GB .svs (estimate)", demoSvs);
Show("demo 482 KB .docx (estimate)", demoDocx);
Check("demo .svs -> heavy estimate", demoSvs.IsHeavy && demoSvs.IsEstimate);
Check("demo .docx -> light estimate", !demoDocx.IsHeavy && demoDocx.IsEstimate);

foreach (var f0 in Enumerable.Range(0, 1)) { } // Speed: second scan of the same 500 files (cache) after a first cold scan
var files = Enumerable.Range(0, 500).Select(i => MakeText($"f{i}.txt", 5_000)).ToList();
sw.Restart();
foreach (var f in files) engine.ClassifyFile(f, 0);
Console.WriteLine($"   first scan, 500 new files: {sw.ElapsedMilliseconds} ms (antivirus checks each new file once)");
sw.Restart();
foreach (var f in files) engine.ClassifyFile(f, 0);
Console.WriteLine($"   rescan, 500 unchanged files: {sw.ElapsedMilliseconds} ms");
Check("rescan of 500 unchanged files under 1 s (cache)", sw.ElapsedMilliseconds < 1000);
File.AppendAllText(files[0], "changed");
Check("changed file is re-read", engine.ClassifyFile(files[0], 0)!.SizeInBytes == 5_007);

// Parallel calls (scan on background thread while user selects) must not crash
var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => files.Take(50).Select(f => engine.ClassifyFile(f, 0)!.IsHeavy).ToList())).ToArray();
Task.WaitAll(tasks);
Check("thread-safe under parallel calls", tasks.All(t => t.Result.Count == 50));

// Missing model: no crash, advisor switched off
var missing = new IntelligentStorageEngine(Path.Combine(dir, "nope.zip"));
Check("missing model -> not loaded, no crash", !missing.IsModelLoaded && missing.ClassifyFile(files[0], 0) == null);
File.WriteAllText(Path.Combine(dir, "broken.zip"), "not a model");
var broken = new IntelligentStorageEngine(Path.Combine(dir, "broken.zip"));
Check("corrupt model -> not loaded, no crash", !broken.IsModelLoaded, broken.LoadError ?? "");

// ---- End to end: ViewModel + demo repository + real engine ----
var vm = new Ventrail.ViewModels.MainDashboardViewModel(new Ventrail.Services.Mock.MockRepositoryManager(), engine,
    new NetworkNodeCoordinator(), new SpecializedFileHandler(), new Ventrail.Services.Mock.InMemoryRecentRepositoryStore(), true);
await vm.InitializeDashboardAsync(@"C:\Demo\Thesis");
for (int i = 0; i < 50 && vm.Changes.Any(c => c.TrackStatus != "Deleted" && c.Classification == null); i++) await Task.Delay(100);
foreach (var c in vm.Changes) Console.WriteLine($"   demo: {c.FilePath,-32} {c.StorageBadge ?? "-",-6} {c.Classification?.Label}");
Check("demo: all non-deleted files classified", vm.Changes.Where(c => c.TrackStatus != "Deleted").All(c => c.Classification != null));
Check("demo: .svs shows Heavy badge", vm.Changes.Single(c => c.FileName == "scan_sample.svs").StorageBadge == "Heavy");
Check("demo: docx has no badge", vm.Changes.Single(c => c.FileName == "Chapter3_Methodology.docx").StorageBadge == null);
vm.SelectedFile = vm.Changes.Single(c => c.FileName == "scan_sample.svs");
Console.WriteLine("   inspector: " + vm.RecommendationText);
Check("inspector shows verdict", vm.RecommendationText.StartsWith("Heavy file"));
vm.SelectedFile = vm.Changes.Single(c => c.TrackStatus == "Deleted");
Check("deleted file message", vm.RecommendationText.Contains("deleted"));
vm.StageAllCommand.Execute(null);
for (int i = 0; i < 30 && vm.IsBusy; i++) await Task.Delay(100);
Check("verdicts kept after reload (no flicker)", vm.Changes.Single(c => c.FileName == "scan_sample.svs").Classification != null);

foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
Directory.Delete(dir);
Console.WriteLine($"\n{pass} passed, {fail} failed.");

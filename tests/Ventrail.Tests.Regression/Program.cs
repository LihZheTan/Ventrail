using System.IO;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;

// Bugs found in the bug check (November 2026), each with a check that fails on the old code.

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok)
    {
        pass++;
    }
    else
    {
        fail++;
    }

    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}");
}
string Expect<TEx>(Action a) where TEx : Exception
{
    try
    {
        a();
        return "<no exception>";
    }
    catch (TEx ex)
    {
        return ex.Message;
    }
    catch (Exception ex)
    {
        return $"<wrong {ex.GetType().Name}: {ex.Message}>";
    }
}

string root = Path.Combine(Path.GetTempPath(), "ventrail-reg-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
string P(string rel)
{
    return Path.Combine(root, rel);
}

string Read(string rel)
{
    return File.ReadAllText(P(rel)).Replace("\r\n", "\n");
}

var rm = new RepositoryManager();
rm.InitRepository(root);
FileAsset? Find(string path)
{
    return rm.ScanWorkingDirectory().SingleOrDefault(f => f.FilePath == path);
}

bool Staged(string path)
{
    return Find(path)?.IsStaged == true;
}

// ---------- 1. file names with [ ] must not touch similarly named files ----------
// Git reads "report[1].txt" as a pattern that also matches "report1.txt".
Console.WriteLine("--- names with [ ] ---");
File.WriteAllText(P("report[1].txt"), "bracket v1\n");
File.WriteAllText(P("report1.txt"), "plain v1\n");
File.WriteAllText(P("notes (final) [v2] #3 & co.txt"), "odd name\n");
Directory.CreateDirectory(P("data [raw]"));
File.WriteAllText(P("data [raw]/set[2].csv"), "a,b\n");

rm.Stage("report[1].txt");
Check("tick report[1].txt: only that file is ticked", Staged("report[1].txt") && !Staged("report1.txt"));
rm.Stage("report1.txt");
rm.Unstage("report[1].txt");
Check("untick report[1].txt: report1.txt stays ticked", !Staged("report[1].txt") && Staged("report1.txt"));
rm.Stage("data [raw]/set[2].csv");
rm.Stage("notes (final) [v2] #3 & co.txt");
Check("odd names and folders with [ ] can be ticked", Staged("data [raw]/set[2].csv") && Staged("notes (final) [v2] #3 & co.txt"));

rm.StageAll();
Check("Tick all ticks everything", rm.ScanWorkingDirectory().All(f => f.IsStaged));
rm.Commit("baseline", "T");
string first = rm.GetCommitHistory()[0].Hash;
Check("everything saved", rm.ScanWorkingDirectory().Count == 0 && rm.GetFilesInCommit(first).Count == 5,
      string.Join(",", rm.GetFilesInCommit(first).Select(f => f.FilePath)));

File.WriteAllText(P("report[1].txt"), "bracket v2\n");
File.WriteAllText(P("report1.txt"), "plain v2 (unsaved work)\n");
rm.Stage("report[1].txt");
Check("tick modified report[1].txt: report1.txt not ticked", Staged("report[1].txt") && !Staged("report1.txt"));
rm.Unstage("report[1].txt");
Check("untick it again: back to unticked, still modified", Find("report[1].txt") is { IsStaged: false, TrackStatus: "Modified" });

rm.RestoreFileFromCommit("report[1].txt", first);
Check("restore report[1].txt: it goes back", Read("report[1].txt") == "bracket v1\n");
Check("  ...and the unsaved work in report1.txt is NOT touched", Read("report1.txt") == "plain v2 (unsaved work)\n", Read("report1.txt").Trim());

File.WriteAllText(P("report[1].txt"), "bracket v3\n");
rm.DiscardChanges("report[1].txt");
Check("discard report[1].txt: it goes back", Read("report[1].txt") == "bracket v1\n" && Find("report[1].txt") == null);
Check("  ...and report1.txt still has its unsaved work", Read("report1.txt") == "plain v2 (unsaved work)\n" && Find("report1.txt")?.TrackStatus == "Modified");

File.Delete(P("data [raw]/set[2].csv"));
rm.Stage("data [raw]/set[2].csv");
Check("a deleted file with [ ] can be ticked", Find("data [raw]/set[2].csv") is { IsStaged: true, TrackStatus: "Deleted" });
rm.DiscardChanges("data [raw]/set[2].csv");
Check("  ...and discarding the deletion brings it back", Read("data [raw]/set[2].csv") == "a,b\n");
rm.DiscardChanges("report1.txt");

// ---------- 2. a name sent by another computer can't choose where the copy goes ----------
Console.WriteLine("--- names from other computers ---");
string Safe(string? n)
{
    return OfferedRepository.SafeFolderName(n);
}

Check("normal names are kept", Safe("Thesis") == "Thesis" && Safe("Group Report 2026") == "Group Report 2026" && Safe("论文") == "论文");
Check("..\\..\\Startup can't climb out", Safe(@"..\..\Startup") == "Startup" && Safe("../../etc") == "etc", Safe(@"..\..\Startup"));
Check("C:\\Windows\\evil can't pick a drive", !Safe(@"C:\Windows\evil").Contains('\\') && !Safe(@"C:\Windows\evil").Contains(':'), Safe(@"C:\Windows\evil"));
Check("\\\\server\\share can't pick a network path", !Safe(@"\\server\share").Contains('\\'), Safe(@"\\server\share"));
Check("empty, dots or device names get a default", Safe("") == "Shared folder" && Safe("..") == "Shared folder" && Safe(null) == "Shared folder" &&
      Safe("CON") == "Shared folder" && Safe("nul.txt") == "Shared folder" && Safe("COM1") == "Shared folder");
Check("very long names are cut", Safe(new string('x', 500)).Length == 60);
string parent = Path.Combine(root, "downloads");
foreach (string evil in new[] { @"..\..\Startup", @"C:\Windows\evil", @"\\server\share\x", "a/../../b", "ok" })
{
    string target = Path.GetFullPath(Path.Combine(parent, Safe(evil)));
    Check($"\"{evil}\" stays inside the chosen folder", Path.GetDirectoryName(target) == parent, target);
}

// ---------- 3. a folder with its own Git history inside the tracked folder ----------
Console.WriteLine("--- nested Git folder ---");
Directory.CreateDirectory(P("downloaded-project"));
LibGit2Sharp.Repository.Init(P("downloaded-project"));
File.WriteAllText(P("downloaded-project/readme.txt"), "someone else's project\n");
File.WriteAllText(P("mine.txt"), "my file\n");
string all = Expect<Exception>(() => rm.StageAll());
Check("Tick all works with a nested Git folder present", all == "<no exception>", all);
Check("  ...my own file is ticked", Staged("mine.txt"));
Check("  ...the nested folder isn't listed (Git can't save it here)", !rm.ScanWorkingDirectory().Any(f => f.FilePath.StartsWith("downloaded-project")));
Check("  ...and the version saves", rm.Commit("with nested folder", "T") && rm.ScanWorkingDirectory().Count == 0);

// ---------- 4. ticking several files quickly ----------
Console.WriteLine("--- quick ticks ---");
for (int i = 0; i < 16; i++)
{
    File.WriteAllText(P($"quick{i}.txt"), "q\n");
}

int errors = 0;
string lastError = "";
Parallel.For(0, 16, i => { try { rm.Stage($"quick{i}.txt"); } catch (Exception ex) { Interlocked.Increment(ref errors); lastError = ex.Message; } });
Check("16 ticks at the same moment: none fails", errors == 0, $"{errors} failed: {lastError}");
Check("  ...and all 16 are ticked", rm.ScanWorkingDirectory().Count(f => f.IsStaged) == 16);
rm.Commit("quick", "T");

// ---------- 5. comparing a file that was ticked, then deleted before saving ----------
Console.WriteLine("--- compare edge cases ---");
File.WriteAllText(P("temp.txt"), "x\n");
rm.Stage("temp.txt");
File.Delete(P("temp.txt"));
string ghost = Expect<Exception>(() => { var d = rm.GetFileDiff("temp.txt"); if (d.Message?.Contains("added and then removed") != true) { throw new Exception(d.Summary); } });
Check("ticked then deleted: a clear message, no crash", ghost == "<no exception>", ghost);
rm.DiscardChanges("temp.txt");
Check("  ...and it can be discarded", Find("temp.txt") == null);

File.WriteAllText(P("empty.txt"), "");
Check("new empty file: says it is empty", rm.GetFileDiff("empty.txt").Message == "This new file is empty.", rm.GetFileDiff("empty.txt").Summary);
File.Delete(P("empty.txt"));

// ---------- 6. read-only files ----------
Console.WriteLine("--- read-only files ---");
File.WriteAllText(P("mine.txt"), "changed\n");
File.SetAttributes(P("mine.txt"), FileAttributes.ReadOnly);
string ro = Expect<Exception>(() => rm.DiscardChanges("mine.txt"));
Check("a read-only file can be discarded", ro == "<no exception>" && Read("mine.txt") == "my file\n", ro);
File.WriteAllText(P("mine.txt"), "changed again\n");
rm.StageAll();
rm.Commit("second", "T");
File.SetAttributes(P("mine.txt"), FileAttributes.ReadOnly);
string roRestore = Expect<Exception>(() => rm.RestoreFileFromCommit("mine.txt", rm.GetCommitHistory().Last(c => c.Message == "with nested folder").Hash));
Check("a read-only file can be restored", roRestore == "<no exception>" && Read("mine.txt") == "my file\n", roRestore);

// ---------- restore still does what it did ----------
Console.WriteLine("--- restore basics ---");
File.Delete(P("report1.txt"));
rm.StageAll();
rm.Commit("removed report1", "T");
rm.RestoreFileFromCommit("report1.txt", first);
Check("a file deleted in a later version can be restored", Read("report1.txt") == "plain v1\n" && Find("report1.txt")?.TrackStatus == "Added");
Directory.Delete(P("data [raw]"), true);
rm.RestoreFileFromCommit("data [raw]/set[2].csv", first);
Check("restoring recreates a missing folder", Read("data [raw]/set[2].csv") == "a,b\n");
string missing = Expect<VentrailException>(() => rm.RestoreFileFromCommit("never-existed.txt", first));
Check("restoring a file that version doesn't have: friendly message", missing.Contains("not part of that version"), missing);

try
{
    foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
    {
        File.SetAttributes(f, FileAttributes.Normal);
    }

    Directory.Delete(root, true);
}
catch (IOException) { }
catch (UnauthorizedAccessException) { }

Console.WriteLine($"\n{pass} passed, {fail} failed.");

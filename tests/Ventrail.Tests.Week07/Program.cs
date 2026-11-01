using System.IO;
using LibGit2Sharp;
using Ventrail.Models;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "") { if (ok) pass++; else fail++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}"); }
string Expect<TEx>(Action a) where TEx : Exception { try { a(); return "<no exception>"; } catch (TEx ex) { return ex.Message; } catch (Exception ex) { return $"<wrong {ex.GetType().Name}: {ex.Message}>"; } }

string root = Path.Combine(Path.GetTempPath(), "ventrail-w7-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
string P(string rel) => Path.Combine(root, rel);
var rm = new RepositoryManager();

// ---- Empty repo: discard a staged new file before the first version exists ----
rm.InitRepository(root);
File.WriteAllText(P("early.txt"), "x");
rm.Stage("early.txt");
Check("discard staged file, no commits yet", rm.DiscardChanges("early.txt") == SafeDelete.RecycleBin && !File.Exists(P("early.txt")));
Check("  ...and not in staging", !rm.ScanWorkingDirectory().Any(f => f.FilePath == "early.txt"));

// ---- Baseline version ----
File.WriteAllText(P("notes.txt"), "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n");
File.WriteAllText(P("keep.txt"), "a\nb\nc\n");
File.WriteAllText(P("gone.txt"), "x1\nx2\n");
File.WriteAllBytes(P("report.docx"), new byte[] { 0x50, 0x4B, 0, 0, 1, 2, 3 });
File.WriteAllText(P("old-name.txt"), "rename me\n");
rm.StageAll(); rm.Commit("baseline", "T");

// ---- Diff: modified text file ----
File.WriteAllText(P("notes.txt"), "one\ntwo\nTHREE\nfour\nfive\nsix\nseven\neight\nnine\n");
var d = rm.GetFileDiff("notes.txt");
Console.WriteLine("   diff notes.txt:\n" + string.Join("\n", d.Lines.Select(l => $"     {l.OldLineNumber,3} {l.NewLineNumber,3} {l.Sign} {l.Text}")));
Check("modified: 2 added / 1 removed", d.LinesAdded == 2 && d.LinesRemoved == 1, d.Summary);
Check("modified: removed line number", d.Lines.Single(l => l.Kind == DiffLineKind.Removed) is { OldLineNumber: 3, Text: "three" });
Check("modified: added line numbers", d.Lines.Where(l => l.Kind == DiffLineKind.Added).Select(l => l.NewLineNumber).SequenceEqual(new int?[] { 3, 9 }));
Check("modified: has hunk header", d.Lines.Any(l => l.Kind == DiffLineKind.Hunk));

// staged change still compared against last saved version
rm.Stage("notes.txt");
Check("staged change still shown", rm.GetFileDiff("notes.txt").LinesAdded == 2);

// ---- Diff: new, deleted, binary, large ----
File.WriteAllText(P("new.md"), "# Title\nbody\n");
var dn = rm.GetFileDiff("new.md");
Check("new file: all lines added", dn.LinesAdded == 2 && dn.LinesRemoved == 0 && dn.Lines[0].NewLineNumber == 1);
File.Delete(P("gone.txt"));
var dg = rm.GetFileDiff("gone.txt");
Check("deleted file: all lines removed", dg.LinesRemoved == 2 && dg.LinesAdded == 0);
File.WriteAllBytes(P("report.docx"), new byte[] { 0x50, 0x4B, 0, 0, 9, 9 });
var db = rm.GetFileDiff("report.docx");
Check("binary: friendly message, no lines", !db.CanShowLines && db.Message!.Contains("Word"));
File.WriteAllText(P("big.log"), new string('x', 3 * 1024 * 1024));
Check("large: friendly message", rm.GetFileDiff("big.log").Message!.Contains("too large"));
File.Delete(P("big.log"));

// ---- Discard ----
Check("discard modified -> Recycle Bin", rm.DiscardChanges("notes.txt") == SafeDelete.RecycleBin);
// core.autocrlf=true (Git for Windows default) writes CRLF on checkout, so compare ignoring line endings.
string Lf(string s) => s.Replace("\r\n", "\n");
Check("  ...content back to saved version", Lf(File.ReadAllText(P("notes.txt"))) == "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n");
Check("  ...no longer listed as changed", !rm.ScanWorkingDirectory().Any(f => f.FilePath == "notes.txt"));

Check("discard new file -> removed", rm.DiscardChanges("new.md") != null && !File.Exists(P("new.md")));

Check("discard deletion -> no copy (nothing on disk)", rm.DiscardChanges("gone.txt") == null);
Check("  ...file restored", File.Exists(P("gone.txt")) && Lf(File.ReadAllText(P("gone.txt"))) == "x1\nx2\n");

rm.DiscardChanges("report.docx");
Check("discard binary -> original bytes", File.ReadAllBytes(P("report.docx")).Length == 7);

// rename staged (like "git mv") -> both names back to normal
using (var repo = new Repository(root)) Commands.Move(repo, "old-name.txt", "new-name.txt");
var renamed = rm.ScanWorkingDirectory();
Console.WriteLine("   after rename: " + string.Join(", ", renamed.Select(f => $"{f.FilePath}={f.TrackStatus}")));
Check("rename visible", renamed.Any(f => f.FilePath == "new-name.txt"));
rm.DiscardChanges("new-name.txt");
var afterRename = rm.ScanWorkingDirectory();
Console.WriteLine("   after discard: " + string.Join(", ", afterRename.Select(f => $"{f.FilePath}={f.TrackStatus}")));
Check("discard rename -> old name back, new gone", File.Exists(P("old-name.txt")) && !File.Exists(P("new-name.txt")));
Check("  ...nothing left changed", afterRename.Count == 0);

// locked file -> friendly error and nothing changed
File.WriteAllText(P("keep.txt"), "edited\n");
using (new FileStream(P("keep.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    string m = Expect<VentrailException>(() => rm.DiscardChanges("keep.txt"));
    Console.WriteLine("   locked: " + m);
    Check("discard locked -> friendly", !m.StartsWith("<"));
}
Check("  ...edits kept", File.ReadAllText(P("keep.txt")) == "edited\n");

string none = Expect<VentrailException>(() => rm.DiscardChanges("old-name.txt"));
Check("discard unchanged file -> friendly", none.Contains("no unsaved changes"), none);

Console.WriteLine($"\n{pass} passed, {fail} failed.");

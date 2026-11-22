using System.IO;
using System.Text.Json;
using Ventrail.Models;
using Ventrail.Services.Implementations;

// Jupyter notebooks (.ipynb): can their content be tracked? A notebook is one JSON file that mixes
// what the user wrote (cells) with results (outputs, embedded charts). These checks show that
// Ventrail saves and restores notebooks like any file, and compares them cell by cell.

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

// ---- helpers that write notebooks the way Jupyter does ----
string png = Convert.ToBase64String(new byte[600]);   // stands in for an embedded chart

// Jupyter stores source as a list of lines, each keeping its line break except the last.
string[] SourceLines(string source)
{
    var lines = source.Split('\n');
    return lines.Select((l, i) => i < lines.Length - 1 ? l + "\n" : l).ToArray();
}
object Code(string source, int? run = null, string? result = null, string? id = null, string? image = null)
{
    var outputs = new List<object>();
    if (result != null)
    {
        outputs.Add(new
        {
            output_type = "stream",
            name = "stdout",
            text = new[] { result + "\n" }
        });
    }

    if (image != null)
    {
        outputs.Add(new
        {
            output_type = "display_data",
            data = new Dictionary<string, object> { ["image/png"] = image },
            metadata = new
            {
            }
        });
    }

    var cell = new Dictionary<string, object?>
    {
        ["cell_type"] = "code",
        ["execution_count"] = run,
        ["metadata"] = new { },
        ["outputs"] = outputs,
        ["source"] = SourceLines(source)
    };
    if (id != null)
    {
        cell["id"] = id;
    }

    return cell;
}
object Text(string source, string? id = null)
{
    var cell = new Dictionary<string, object?> { ["cell_type"] = "markdown", ["metadata"] = new { }, ["source"] = SourceLines(source) };
    if (id != null)
    {
        cell["id"] = id;
    }

    return cell;
}
string Notebook(params object[] cells)
{
    return JsonSerializer.Serialize(new
    {
        cells,
        metadata = new
        {
            kernelspec = new
            {
                display_name = "Python 3",
                language = "python",
                name = "python3"
            },
            language_info = new
            {
                name = "python",
                version = "3.12.0"
            }
        },
        nbformat = 4,
        nbformat_minor = 5
    }, new JsonSerializerOptions { WriteIndented = true });
}

string Canonical(string json)
{
    using var doc = JsonDocument.Parse(json);
    return JsonSerializer.Serialize(doc.RootElement);
}

// ---------- reading notebooks ----------
Console.WriteLine("--- reading ---");
var book = NotebookReader.TryParse(Notebook(
    Text("# Sales analysis\nLoad the data first."),
    Code("import pandas as pd\ndf = pd.read_csv('sales.csv')", 1),
    Code("df.plot()", 2, "<Axes>", image: png)))!;
Check("reads the cells in order", book.Cells.Select(c => c.Kind).SequenceEqual(new[] { "markdown", "code", "code" }));
Check("reads the source lines", book.Cells[1].Lines.SequenceEqual(new[] { "import pandas as pd", "df = pd.read_csv('sales.csv')" }));
Check("reads the language", book.Language == "python", book.Language);
Check("counts outputs without keeping them", book.Cells[2].OutputCount == 2 && book.Cells[2].OutputsHash.Length == 64 && book.Cells[1].OutputCount == 0);
Check("is a notebook: by .ipynb extension, any case",
      NotebookReader.IsNotebook("a/b/Report.IPYNB") && !NotebookReader.IsNotebook("notes.json") && !NotebookReader.IsNotebook("ipynb"));

var oneString = NotebookReader.TryParse("{\"cells\":[{\"cell_type\":\"code\",\"source\":\"a = 1\\nb = 2\\n\",\"outputs\":[]}],\"metadata\":{},\"nbformat\":4}")!;
Check("source stored as one string is read too", oneString.Cells[0].Lines.SequenceEqual(new[] { "a = 1", "b = 2" }));
var v3 = NotebookReader.TryParse("{\"nbformat\":3,\"metadata\":{},\"worksheets\":[{\"cells\":[" +
    "{\"cell_type\":\"code\",\"language\":\"python\",\"input\":[\"x = 1\"],\"outputs\":[]}," +
    "{\"cell_type\":\"markdown\",\"source\":[\"old format\"]}]}]}")!;
Check("old notebooks (format 3, worksheets) are read", v3.Cells.Count == 2 && v3.Cells[0].Lines[0] == "x = 1" && v3.Cells[1].Kind == "markdown");
Check("broken JSON is not a notebook", NotebookReader.TryParse("{ \"cells\": [ {") == null);
Check("JSON without cells is not a notebook",
      NotebookReader.TryParse("{\"name\":\"package\",\"version\":1}") == null && NotebookReader.TryParse("[1,2]") == null);
Check("empty notebook is fine", NotebookReader.TryParse("{\"cells\":[],\"metadata\":{},\"nbformat\":4}")!.Cells.Count == 0);

// ---------- tracking a notebook in a real repository ----------
Console.WriteLine("--- tracking in a repository ---");
string root = Path.Combine(Path.GetTempPath(), "ventrail-nb-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(root);
string P(string rel)
{
    return Path.Combine(root, rel);
}

var rm = new RepositoryManager();
rm.InitRepository(root);

string v1 = Notebook(
    Text("# Sales analysis\nLoad the data first."),
    Code("import pandas as pd\ndf = pd.read_csv('sales.csv')\ndf.head()", 1, "   month  total"),
    Code("df.plot()", 2, "<Axes>", image: png),
    Text("## Conclusion\nSales grew."));
File.WriteAllText(P("analysis.ipynb"), v1);

var found = rm.ScanWorkingDirectory().SingleOrDefault(f => f.FilePath == "analysis.ipynb");
Check("a new notebook is found by the scan", found != null, found?.TrackStatus ?? "not found");

var dNew = rm.GetFileDiff("analysis.ipynb");
int newCells = dNew.Lines.Count(l => l.Kind == DiffLineKind.Hunk);
Check("new notebook: every cell listed as new", newCells == 4 && dNew.LinesRemoved == 0 && dNew.LinesAdded == 8, $"{newCells} cells, +{dNew.LinesAdded}");
Check("new notebook: note says what it is", dNew.Note == "Notebook (Python): new, 4 cells", dNew.Note ?? "");
Check("headings name the cell and its kind",
      dNew.Lines[0].Text == "Cell 1 · Text · new cell" && dNew.Lines.Any(l => l.Text == "Cell 2 · Code · new cell"), dNew.Lines[0].Text);
Check("only the source is shown: no JSON, no outputs, no pictures",
      !dNew.Lines.Any(l => l.Text.Contains("cell_type") || l.Text.Contains("image/png") || l.Text.Contains("AAAA") || l.Text.Contains("month  total")));

rm.StageAll();
Check("the notebook is saved as a version", rm.Commit("Add sales analysis", "T") && rm.GetCommitHistory().Count == 1);
string first = rm.GetCommitHistory()[0].Hash;
Check("saved version lists the notebook", rm.GetFilesInCommit(first).Any(f => f.FilePath == "analysis.ipynb"));
Check("nothing left to save", rm.ScanWorkingDirectory().Count == 0);

// ---------- what changed, cell by cell ----------
Console.WriteLine("--- cell-by-cell comparison ---");
File.WriteAllText(P("analysis.ipynb"), Notebook(
    Text("# Sales analysis\nLoad the data first."),
    Code("import pandas as pd\ndf = pd.read_csv('sales_2026.csv')\ndf.head()", 1, "   month  total"),
    Code("df.plot()", 2, "<Axes>", image: png),
    Text("## Conclusion\nSales grew.")));
Check("an edited notebook is found by the scan", rm.ScanWorkingDirectory().Single().TrackStatus == "Modified");
var d1 = rm.GetFileDiff("analysis.ipynb");
Check("one edited line: one cell shown",
      d1.Lines.Count(l => l.Kind == DiffLineKind.Hunk) == 1 && d1.Lines[0].Text == "Cell 2 · Code · changed", d1.Lines[0].Text);
Check("  ...1 line added, 1 removed", d1.LinesAdded == 1 && d1.LinesRemoved == 1, d1.Summary);
Check("  ...the old and new line",
      d1.Lines.Single(l => l.Kind == DiffLineKind.Removed) is { Text: "df = pd.read_csv('sales.csv')", OldLineNumber: 2 } &&
      d1.Lines.Single(l => l.Kind == DiffLineKind.Added) is { Text: "df = pd.read_csv('sales_2026.csv')", NewLineNumber: 2 });
Check("  ...the cell's other lines as context", d1.Lines.Count(l => l.Kind == DiffLineKind.Context) == 2);
Check("  ...summary", d1.Summary == "Notebook (Python): 1 cell changed · 1 line(s) added, 1 line(s) removed", d1.Summary);

// Run the notebook again: same code, new results and run numbers.
File.WriteAllText(P("analysis.ipynb"), Notebook(
    Text("# Sales analysis\nLoad the data first."),
    Code("import pandas as pd\ndf = pd.read_csv('sales.csv')\ndf.head()", 7, "   month  total  NEW"),
    Code("df.plot()", 8, "<Axes>", image: Convert.ToBase64String(new byte[900])),
    Text("## Conclusion\nSales grew.")));
var dRun = rm.GetFileDiff("analysis.ipynb");
Check("only re-run: still counted as a change to save", rm.ScanWorkingDirectory().Count == 1);
Check("only re-run: no lines, a clear message",
      !dRun.CanShowLines && dRun.Message!.Contains("Only the results (outputs) of 2 cells changed"), dRun.Message ?? "");

// Add a cell in the middle, remove the conclusion, edit the title, and re-run a cell.
File.WriteAllText(P("analysis.ipynb"), Notebook(
    Text("# Sales analysis 2026\nLoad the data first."),
    Code("import pandas as pd\ndf = pd.read_csv('sales.csv')\ndf.head()", 1, "   month  total"),
    Code("df = df.dropna()\nlen(df)", 3, "118"),
    Code("df.plot()", 4, "<Axes>", image: Convert.ToBase64String(new byte[900]))));
var d2 = rm.GetFileDiff("analysis.ipynb");
var heads = d2.Lines.Where(l => l.Kind == DiffLineKind.Hunk).Select(l => l.Text).ToList();
Check("changed + new + removed cells, in notebook order", heads.SequenceEqual(new[]
{
    "Cell 1 · Text · changed", "Cell 3 · Code · new cell", "Cell 4 of the saved version · Text · removed"
}), string.Join(" | ", heads));
Check("  ...note counts them, and the re-run cell",
      d2.Note == "Notebook (Python): 1 cell changed, 1 new, 1 removed, results changed in 1 cell (not shown)", d2.Note ?? "");
Check("  ...line counts", d2.LinesAdded == 3 && d2.LinesRemoved == 3, d2.Summary);

// Code cell turned into a text cell.
File.WriteAllText(P("analysis.ipynb"), Notebook(
    Text("# Sales analysis\nLoad the data first."),
    Code("import pandas as pd\ndf = pd.read_csv('sales.csv')\ndf.head()", 1, "   month  total"),
    Text("df.plot()"),
    Text("## Conclusion\nSales grew.")));
var dKind = rm.GetFileDiff("analysis.ipynb");
Check("a code cell turned into text is shown", dKind.Lines[0].Text == "Cell 3 · Text (was Code) · changed", dKind.Lines[0].Text);

// Cell ids (Jupyter 4.5+): a new cell in front of an edited one must not mix them up.
string idOld = Notebook(Code("a = 1", id: "aaa"), Code("total = a + 1\nprint(total)", id: "bbb"));
string idNew = Notebook(Code("a = 1", id: "aaa"), Code("import math", id: "ccc"), Code("total = a + 2\nprint(total)", id: "bbb"));
var dIds = NotebookDiff.Build("x.ipynb", idOld, idNew)!;
var idHeads = dIds.Lines.Where(l => l.Kind == DiffLineKind.Hunk).Select(l => l.Text).ToList();
Check("cell ids keep an edited cell matched to itself",
      idHeads.SequenceEqual(new[] { "Cell 2 · Code · new cell", "Cell 3 · Code · changed" }) &&
      dIds.Lines.Any(l => l.Kind == DiffLineKind.Context && l.Text == "print(total)"), string.Join(" | ", idHeads));
var dSame = NotebookDiff.Build("x.ipynb", idOld, idOld)!;
Check("identical notebooks: nothing to show", !dSame.CanShowLines && dSame.Message!.Contains("cells are the same"), dSame.Message ?? "");

// ---------- big notebooks, broken notebooks ----------
Console.WriteLine("--- limits ---");
string bigImage = Convert.ToBase64String(new byte[3 * 1024 * 1024]);   // a 3 MB chart inside the file
File.WriteAllText(P("analysis.ipynb"), Notebook(
    Text("# Sales analysis\nLoad the data first."),
    Code("import pandas as pd\ndf = pd.read_csv('sales.csv')\ndf.head()", 1, "   month  total"),
    Code("df.plot(kind='bar')", 2, "<Axes>", image: bigImage),
    Text("## Conclusion\nSales grew.")));
var dBig = rm.GetFileDiff("analysis.ipynb");
Check("a 4 MB notebook (too big for a text comparison) is still compared by cell",
      new FileInfo(P("analysis.ipynb")).Length > 2 * 1024 * 1024 && dBig.CanShowLines && dBig.Lines[0].Text == "Cell 3 · Code · changed", dBig.Summary);

File.WriteAllText(P("analysis.ipynb"), "{\n  \"cells\": [\n    { \"cell_type\": \"code\",\n");
var dBroken = rm.GetFileDiff("analysis.ipynb");
Check("a half-written notebook falls back to the plain text comparison", dBroken.Note == null && dBroken.CanShowLines, dBroken.Summary);
File.WriteAllText(P("data.json"), "{\"cells\": []}\n");
var dJson = rm.GetFileDiff("data.json");
Check("other JSON files are compared as text", dJson.Note == null && dJson.LinesAdded == 1, dJson.Summary);
File.Delete(P("data.json"));

// ---------- restore and delete ----------
Console.WriteLine("--- restore and delete ---");
rm.RestoreFileFromCommit("analysis.ipynb", first);
Check("restoring brings back the saved notebook, outputs included", Canonical(File.ReadAllText(P("analysis.ipynb"))) == Canonical(v1));
Check("  ...and nothing is left to save", rm.ScanWorkingDirectory().Count == 0);

File.WriteAllText(P("analysis.ipynb"), Notebook(Text("# Sales analysis\nLoad the data first."), Code("print('second version')", 1, "second version")));
rm.StageAll();
rm.Commit("Shorter notebook", "T");
Check("two versions in the history", rm.GetCommitHistory().Count == 2);
rm.RestoreFileFromCommit("analysis.ipynb", first);
var dBack = rm.GetFileDiff("analysis.ipynb");
Check("an old version can be brought back and compared with the latest",
      dBack.Note == "Notebook (Python): 1 cell changed, 2 new", dBack.Note ?? "");

File.Delete(P("analysis.ipynb"));
var dGone = rm.GetFileDiff("analysis.ipynb");
Check("deleted notebook: cells shown as removed",
      dGone.LinesAdded == 0 && dGone.LinesRemoved == 3 && dGone.Note == "Notebook (Python): deleted, 2 cells", dGone.Summary);

// Temporary repository: Git marks its object files read-only, so clear that before deleting.
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ventrail.Models;

namespace Ventrail.Services.Implementations;

/// <summary>One cell of a Jupyter notebook: its kind, its source lines and a fingerprint of its outputs.</summary>
public class NotebookCell
{
    public string Kind { get; set; } = "code";          // code, markdown or raw
    public string? Id { get; set; }                      // nbformat 4.5+ gives every cell an id
    public List<string> Lines { get; set; } = new();
    public int OutputCount { get; set; }
    public string OutputsHash { get; set; } = string.Empty;

    public string Source => string.Join("\n", Lines);

    // The name shown to users. "Markdown" cells are simply the text between the code.
    public string KindName => Kind switch { "markdown" => "Text", "raw" => "Raw", _ => "Code" };
}

public class NotebookDocument
{
    public List<NotebookCell> Cells { get; set; } = new();
    public string Language { get; set; } = string.Empty;   // "python", "r", ...
}

/// <summary>
/// Reads Jupyter notebooks (.ipynb). A notebook is one JSON file holding the cells' source AND
/// their outputs (results, charts as embedded pictures), so a plain line-by-line comparison is
/// mostly noise. This reads only what the user wrote: the cells.
/// </summary>
public static class NotebookReader
{
    public static bool IsNotebook(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".ipynb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns null if the text isn't a notebook (broken JSON, or no cells list).</summary>
    public static NotebookDocument? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var cells = new List<JsonElement>();
            if (root.TryGetProperty("cells", out var v4) && v4.ValueKind == JsonValueKind.Array)
            {
                cells.AddRange(v4.EnumerateArray());                                   // nbformat 4
            }
            else if (root.TryGetProperty("worksheets", out var sheets) && sheets.ValueKind == JsonValueKind.Array)
            {
                foreach (var sheet in sheets.EnumerateArray())                         // nbformat 3
                {
                    if (sheet.ValueKind == JsonValueKind.Object &&
                        sheet.TryGetProperty("cells", out var v3) && v3.ValueKind == JsonValueKind.Array)
                    {
                        cells.AddRange(v3.EnumerateArray());
                    }
                }
            }
            else
            {
                return null;
            }

            var notebook = new NotebookDocument { Language = ReadLanguage(root) };
            foreach (var cell in cells)
            {
                if (cell.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var item = new NotebookCell
                {
                    Kind = Text(cell, "cell_type") is { Length: > 0 } kind ? kind.ToLowerInvariant() : "code",
                    Id = Text(cell, "id")
                };
                // nbformat 3 keeps code in "input"; everything else uses "source".
                string source = cell.TryGetProperty("source", out var s) ? Joined(s)
                              : cell.TryGetProperty("input", out var i) ? Joined(i) : string.Empty;
                item.Lines = SplitLines(source);

                if (cell.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                {
                    item.OutputCount = outputs.GetArrayLength();
                    // Hash the outputs re-written without spacing, so that a different layout of the
                    // same JSON (Git may change the line endings) doesn't count as "results changed".
                    if (item.OutputCount > 0)
                    {
                        item.OutputsHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(outputs)));
                    }
                }
                notebook.Cells.Add(item);
            }
            return notebook;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadLanguage(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (meta.TryGetProperty("kernelspec", out var kernel) && Text(kernel, "language") is { Length: > 0 } a)
        {
            return a;
        }

        if (meta.TryGetProperty("language_info", out var info) && Text(info, "name") is { Length: > 0 } b)
        {
            return b;
        }

        return string.Empty;
    }

    private static string? Text(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
    }

    // "source" is either one string or a list of lines that each keep their own line break.
    private static string Joined(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())),
            _ => string.Empty
        };
    }

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return new List<string>();
        }

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}

/// <summary>
/// Compares two versions of a notebook cell by cell. Only cells whose source changed are shown,
/// each under a "Cell 3 · Code · changed" heading; outputs are counted but never shown.
/// </summary>
public static class NotebookDiff
{
    /// <summary>
    /// <paramref name="oldJson"/> is the last saved version (null = new file), <paramref name="newJson"/>
    /// the file on disk (null = deleted). Returns null if either isn't a readable notebook, so the
    /// caller can fall back to the ordinary text comparison.
    /// </summary>
    public static FileDiff? Build(string filePath, string? oldJson, string? newJson, int maxLines = 3000)
    {
        NotebookDocument? oldBook = oldJson == null ? new NotebookDocument() : NotebookReader.TryParse(oldJson);
        NotebookDocument? newBook = newJson == null ? new NotebookDocument() : NotebookReader.TryParse(newJson);
        if (oldBook == null || newBook == null)
        {
            return null;
        }

        var diff = new FileDiff { FilePath = filePath };
        int changed = 0, added = 0, removed = 0, outputsChanged = 0;

        foreach (var (o, n) in AlignCells(oldBook.Cells, newBook.Cells))
        {
            NotebookCell? before = o >= 0 ? oldBook.Cells[o] : null;
            NotebookCell? after = n >= 0 ? newBook.Cells[n] : null;

            if (before != null && after != null && before.Kind == after.Kind && before.Source == after.Source)
            {
                if (before.OutputsHash != after.OutputsHash)
                {
                    outputsChanged++;
                }

                continue;                                  // same source: nothing to show
            }
            if (diff.Lines.Count >= maxLines)
            {
                diff.IsTruncated = true;
                continue;
            }

            if (before != null && after != null)
            {
                changed++;
                string kind = before.Kind == after.Kind ? after.KindName : $"{after.KindName} (was {before.KindName})";
                diff.Lines.Add(Heading($"Cell {n + 1} · {kind} · changed"));
                AddLineDiff(diff.Lines, before.Lines, after.Lines);
            }
            else if (after != null)
            {
                added++;
                diff.Lines.Add(Heading($"Cell {n + 1} · {after.KindName} · new cell"));
                for (int i = 0; i < after.Lines.Count; i++)
                {
                    diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = after.Lines[i], NewLineNumber = i + 1 });
                }
            }
            else
            {
                removed++;
                diff.Lines.Add(Heading($"Cell {o + 1} of the saved version · {before!.KindName} · removed"));
                for (int i = 0; i < before.Lines.Count; i++)
                {
                    diff.Lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = before.Lines[i], OldLineNumber = i + 1 });
                }
            }
        }

        diff.LinesAdded = diff.Lines.Count(l => l.Kind == DiffLineKind.Added);
        diff.LinesRemoved = diff.Lines.Count(l => l.Kind == DiffLineKind.Removed);

        string language = (newJson != null ? newBook : oldBook).Language;
        string title = language.Length > 0 ? $"Notebook ({char.ToUpperInvariant(language[0])}{language[1..]})" : "Notebook";
        var parts = new List<string>();
        if (oldJson == null)
        {
            parts.Add($"new, {Count(newBook.Cells.Count, "cell")}");
        }
        else if (newJson == null)
        {
            parts.Add($"deleted, {Count(oldBook.Cells.Count, "cell")}");
        }
        else
        {
            if (changed > 0)
            {
                parts.Add($"{Count(changed, "cell")} changed");
            }

            if (added > 0)
            {
                parts.Add($"{added} new");
            }

            if (removed > 0)
            {
                parts.Add($"{removed} removed");
            }

            if (outputsChanged > 0)
            {
                parts.Add($"results changed in {Count(outputsChanged, "cell")} (not shown)");
            }
        }
        diff.Note = parts.Count > 0 ? $"{title}: {string.Join(", ", parts)}" : title;

        if (diff.Lines.Count == 0)
        {
            diff.Message = outputsChanged > 0
                ? $"Only the results (outputs) of {Count(outputsChanged, "cell")} changed, for example after running the " +
                  "notebook again. The code and text are the same."
                : "The notebook's cells are the same. Only its settings changed (for example the kernel or the run counters).";
        }

        return diff;
    }

    private static string Count(int n, string noun)
    {
        return n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }

    private static DiffLine Heading(string text)
    {
        return new()
        {
            Kind = DiffLineKind.Hunk,
            Text = text
        };
    }

    // Pairs up the cells of both versions: (old index, new index), -1 where a cell has no partner.
    //  1. cells with exactly the same kind and source stay paired (longest common subsequence);
    //  2. between those, cells with the same id are the same cell, edited;
    //  3. what is left is paired in order (edited), and the rest is new or removed.
    internal static List<(int Old, int New)> AlignCells(IReadOnlyList<NotebookCell> before, IReadOnlyList<NotebookCell> after)
    {
        var result = new List<(int, int)>();
        var same = Lcs(before.Select(c => c.Kind + "\n" + c.Source).ToList(), after.Select(c => c.Kind + "\n" + c.Source).ToList());
        int o = 0, n = 0;
        foreach (var (so, sn) in same.Append((before.Count, after.Count)))
        {
            AlignGap(before, after, o, so, n, sn, result);
            if (so < before.Count)
            {
                result.Add((so, sn));
            }

            o = so + 1;
            n = sn + 1;
        }
        return result;
    }

    private static void AlignGap(IReadOnlyList<NotebookCell> before, IReadOnlyList<NotebookCell> after,
                                 int oStart, int oEnd, int nStart, int nEnd, List<(int, int)> result)
    {
        var oldIds = Enumerable.Range(oStart, oEnd - oStart).Select(i => before[i].Id ?? $"old-{i}").ToList();
        var newIds = Enumerable.Range(nStart, nEnd - nStart).Select(i => after[i].Id ?? $"new-{i}").ToList();
        int o = oStart, n = nStart;
        foreach (var (io, inew) in Lcs(oldIds, newIds).Append((oEnd - oStart, nEnd - nStart)))
        {
            int oStop = oStart + io, nStop = nStart + inew;
            // No ids to go by: pair in order, the extras are removed (old) or new.
            while (o < oStop && n < nStop)
            {
                result.Add((o++, n++));
            }

            while (o < oStop)
            {
                result.Add((o++, -1));
            }

            while (n < nStop)
            {
                result.Add((-1, n++));
            }

            if (oStop < oEnd)
            {
                result.Add((oStop, nStop));
            }

            o = oStop + 1;
            n = nStop + 1;
        }
    }

    private static void AddLineDiff(List<DiffLine> lines, List<string> before, List<string> after)
    {
        int o = 0, n = 0;
        foreach (var (so, sn) in Lcs(before, after).Append((before.Count, after.Count)))
        {
            while (o < so)
            {
                lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = before[o], OldLineNumber = ++o });
            }

            while (n < sn)
            {
                lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = after[n], NewLineNumber = ++n });
            }

            if (so < before.Count)
            {
                lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = before[so], OldLineNumber = ++o, NewLineNumber = ++n });
            }
        }
    }

    // Longest common subsequence: the pairs (index in a, index in b) that stay the same, in order.
    private static List<(int A, int B)> Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var pairs = new List<(int, int)>();
        if (a.Count == 0 || b.Count == 0)
        {
            return pairs;
        }

        if ((long)a.Count * b.Count > 4_000_000)
        {
            return pairs;   // huge cell: show it as fully replaced
        }

        var length = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
        {
            for (int j = b.Count - 1; j >= 0; j--)
            {
                length[i, j] = a[i] == b[j] ? length[i + 1, j + 1] + 1 : Math.Max(length[i + 1, j], length[i, j + 1]);
            }
        }

        for (int i = 0, j = 0; i < a.Count && j < b.Count;)
        {
            if (a[i] == b[j])
            {
                pairs.Add((i++, j++));
            }
            else if (length[i + 1, j] >= length[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }
        return pairs;
    }
}

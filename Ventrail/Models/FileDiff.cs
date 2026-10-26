using System.Collections.Generic;

namespace Ventrail.Models;

public enum DiffLineKind { Context, Added, Removed, Hunk }

/// <summary>One line of a text comparison (Week 7 diff viewer).</summary>
public class DiffLine
{
    public DiffLineKind Kind { get; set; }
    public string Text { get; set; } = string.Empty;
    public int? OldLineNumber { get; set; }   // line number in the last saved version
    public int? NewLineNumber { get; set; }   // line number in the file on disk

    // "+" / "-" / " " gutter sign shown in front of the text.
    public string Sign => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        _ => " "
    };
}

/// <summary>
/// What changed in one file since the last saved version. For files that can't be compared
/// line by line (Word, images, very large files) Lines is empty and Message explains why.
/// </summary>
public class FileDiff
{
    public string FilePath { get; set; } = string.Empty;
    public List<DiffLine> Lines { get; set; } = new();
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public string? Message { get; set; }
    public bool IsTruncated { get; set; }

    public bool CanShowLines => Lines.Count > 0;

    public string Summary => CanShowLines
        ? $"{LinesAdded} line(s) added, {LinesRemoved} line(s) removed" + (IsTruncated ? " (first part shown)" : "")
        : Message ?? "No differences.";
}

using System.Collections.Generic;
using System.Linq;

namespace Ventrail.Models;

public enum AdviceLevel { Info, Warning }

/// <summary>Week 9: one storage tip about a staged file, shown on the AI suggestion card.</summary>
public class StorageAdvice
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeInBytes { get; set; }
    public string Text { get; set; } = string.Empty;
    public AdviceLevel Level { get; set; }

    // True when "Accept AI Suggestions" should untick this file (very large heavy files).
    public bool SuggestLeaveOut { get; set; }

    public bool IsWarning => Level == AdviceLevel.Warning;
}

/// <summary>
/// Week 9: what the advisor suggests for the next version: a commit message written from the
/// ticked files, plus storage advice for the heavy ones.
/// </summary>
public class CommitSuggestion
{
    public string Message { get; set; } = string.Empty;
    public List<StorageAdvice> Advice { get; set; } = new();
    public int FileCount { get; set; }

    public bool HasAdvice => Advice.Count > 0;
    public List<string> LeaveOutPaths => Advice.Where(a => a.SuggestLeaveOut).Select(a => a.FilePath).ToList();

    public string Summary
    {
        get
        {
            if (Advice.Count == 0) return "No heavy files: this version adds little to the history.";
            var leaveOut = Advice.Where(a => a.SuggestLeaveOut).ToList();
            string text = $"{Advice.Count} heavy file{(Advice.Count == 1 ? "" : "s")} in this version.";
            if (leaveOut.Count > 0)
                text += $" Leaving {(leaveOut.Count == 1 ? "1 file" : $"{leaveOut.Count} files")} out would keep about " +
                        $"{FileAsset.FormatSize(leaveOut.Sum(a => a.SizeInBytes))} out of the history.";
            return text;
        }
    }
}

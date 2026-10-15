using System;
using System.Collections.Generic;

namespace Ventrail.Models;

public class CommitNode
{
    public string Hash { get; set; } = string.Empty;          // full SHA-1, e.g. "3f2a9c..."
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Author { get; set; } = string.Empty;

    // Parent pointers: this is what makes history a DAG.
    // 0 parents = first commit, 1 = normal, 2+ = merge.
    public List<string> ParentHashes { get; set; } = new();

    // Short form shown in the UI (Git's usual 7 characters).
    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;

    // Friendly label for non-technical users.
    public string DisplayTime => Timestamp.ToString("dd MMM yyyy, HH:mm");

    // "3 hours ago" style label for the history timeline (Week 4).
    public string RelativeTime
    {
        get
        {
            TimeSpan age = DateTime.Now - Timestamp;
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min ago";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours} h ago";
            if (age.TotalDays < 2) return "yesterday";
            if (age.TotalDays < 30) return $"{(int)age.TotalDays} days ago";
            return Timestamp.ToString("dd MMM yyyy");
        }
    }

    // A merge joins two lines of history (2+ parents); drawn differently on the timeline.
    public bool IsMerge => ParentHashes.Count > 1;

    // Position in the timeline, set by the ViewModel when history loads:
    // the newest version has no line above it, the oldest has no line below it.
    public bool IsLatest { get; set; }
    public bool IsOldest { get; set; }
}

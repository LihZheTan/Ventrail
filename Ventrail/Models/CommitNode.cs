using System;
using System.Collections.Generic;

namespace Ventrail.Models;

public class CommitNode
{
    public string Hash { get; set; } = string.Empty;          
    public string Message { get; set; } = string.Empty;       // first line (the summary)
    public string Description { get; set; } = string.Empty;   // the rest, if the author wrote one
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public DateTime Timestamp { get; set; }
    public string Author { get; set; } = string.Empty;

    public List<string> ParentHashes { get; set; } = new();

    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;

    public string DisplayTime => Timestamp.ToString("dd MMM yyyy, HH:mm");

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

    public bool IsMerge => ParentHashes.Count > 1;

    public bool IsLatest { get; set; }
    public bool IsOldest { get; set; }
}

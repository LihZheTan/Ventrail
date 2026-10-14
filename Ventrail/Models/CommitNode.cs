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
}

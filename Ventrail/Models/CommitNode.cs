using System;

namespace Ventrail.Models;

public class CommitNode
{
    public string Hash { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Author { get; set; } = string.Empty;
}
using System;

namespace Ventrail.Models;

/// <summary>One line in the Session Summary activity log (Fig 3.7).</summary>
public class ActivityEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Text { get; init; } = string.Empty;

    public string DisplayTime => Time.ToString("HH:mm");
}

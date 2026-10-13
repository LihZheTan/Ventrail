namespace Ventrail.Models;

/// <summary>
/// One changed file, as reported by a working-directory scan.
/// </summary>
public class FileAsset
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;      // path relative to the repo root (what Git uses)
    public long SizeInBytes { get; set; }
    public string TrackStatus { get; set; } = "Untracked";    // Added, Modified, Deleted, Renamed

    // True when the change is in the staging index and will go into the next commit.
    public bool IsStaged { get; set; }

    public string DisplaySize => SizeInBytes switch
    {
        < 1024 => $"{SizeInBytes} B",
        < 1024 * 1024 => $"{SizeInBytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{SizeInBytes / (1024.0 * 1024):F1} MB",
        _ => $"{SizeInBytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}

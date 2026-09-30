namespace Ventrail.Models;

public class FileAsset
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeInBytes { get; set; }
    public string TrackStatus { get; set; } = "Untracked"; // e.g., Added, Modified, Deleted
}
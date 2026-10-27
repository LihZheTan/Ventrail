using CommunityToolkit.Mvvm.ComponentModel;

namespace Ventrail.Models;

/// <summary>
/// One changed file, as reported by a working-directory scan.
/// Observable since Week 8: the storage advisor's verdict arrives a moment after the scan.
/// </summary>
public partial class FileAsset : ObservableObject
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;      // path relative to the repo root (what Git uses)
    public long SizeInBytes { get; set; }
    public string TrackStatus { get; set; } = "Untracked";    // Added, Modified, Deleted, Renamed

    // True when the change is in the staging index and will go into the next commit.
    public bool IsStaged { get; set; }

    // Week 6: files at or above this size get a "Large" badge and a warning before saving,
    // because every version keeps a full copy (the storage-bloat problem in the proposal).
    // Weeks 8-9 replace this fixed rule with the ML.NET heavy/light classifier.
    public const long LargeFileBytes = 50L * 1024 * 1024;

    public bool IsLarge => SizeInBytes >= LargeFileBytes;

    // Friendly type for the File Inspector, e.g. "DOCX file".
    public string FileType
    {
        get
        {
            string ext = System.IO.Path.GetExtension(FileName).TrimStart('.');
            return string.IsNullOrEmpty(ext) ? "File" : $"{ext.ToUpperInvariant()} file";
        }
    }

    // Icon glyph (Segoe Fluent / MDL2) for the kind of file, shown in the staging list.
    public string TypeGlyph => System.IO.Path.GetExtension(FileName).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".svs" or ".psd" => "\uEB9F", // picture
        ".mp4" or ".mov" or ".avi" or ".mkv" => "\uE714",                                                    // video
        ".mp3" or ".wav" or ".flac" => "\uE8D6",                                                              // audio
        ".cs" or ".py" or ".js" or ".ts" or ".java" or ".cpp" or ".json" or ".xml" or ".xaml" => "\uE943",  // code
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "\uE7B8",                                           // archive
        ".docx" or ".doc" or ".pdf" or ".txt" or ".md" or ".xlsx" or ".csv" or ".pptx" => "\uE8A5",          // document
        _ => "\uE7C3"                                                                                        // other
    };

    public string DisplaySize => FormatSize(SizeInBytes);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    // Week 8: the ML.NET storage advisor's verdict (null = not classified yet, a deleted file,
    // or the model could not be loaded). Filled in after the scan, so the list updates live.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHeavy), nameof(StorageBadge), nameof(HasStorageBadge), nameof(StorageBadgeTip))]
    private FileClassification? _classification;

    public bool IsHeavy => Classification?.IsHeavy == true;

    // Badge in the staging list: the model's "Heavy" verdict, or the Week 6 size rule if the
    // model isn't available.
    public string? StorageBadge => IsHeavy ? "Heavy" : Classification is null && IsLarge ? "Large" : null;
    public bool HasStorageBadge => StorageBadge is not null;
    public string StorageBadgeTip => Classification?.Summary
        ?? "Large file: every saved version keeps a full copy of it";
}

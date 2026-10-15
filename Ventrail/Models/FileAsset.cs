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

    public string DisplaySize => SizeInBytes switch
    {
        < 1024 => $"{SizeInBytes} B",
        < 1024 * 1024 => $"{SizeInBytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{SizeInBytes / (1024.0 * 1024):F1} MB",
        _ => $"{SizeInBytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}

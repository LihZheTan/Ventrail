using System;
using System.IO;
using Microsoft.ML.Data;

namespace Ventrail.Services.AI;

/// <summary>
/// Week 8: the cheap facts about a file that the heavy/light classifier looks at.
/// Only the size and the first 4 KB ("header") are read, so classifying is fast even for
/// multi-gigabyte files.
///
/// This file is shared word-for-word with the offline trainer (Ventrail.ModelTrainer links it),
/// so the features used for training and in the app can never drift apart.
/// </summary>
public class FileFeatures
{
    [LoadColumn(0)] public float LogSize { get; set; }          // log10(bytes + 1): 1 KB = 3, 1 MB = 6, 1 GB = 9
    [LoadColumn(1)] public float HeaderEntropy { get; set; }    // 0-8 bits per byte; ~8 = already compressed/encrypted
    [LoadColumn(2)] public float ZeroByteRatio { get; set; }    // share of 0x00 bytes (binary structures)
    [LoadColumn(3)] public float PrintableRatio { get; set; }   // share of text-like bytes (ASCII + UTF-8)
    [LoadColumn(4)] public string Signature { get; set; } = "unknown";   // format from the magic bytes
    [LoadColumn(5)] public string Category { get; set; } = "other";     // family from the extension
    [LoadColumn(6), ColumnName("Label")] public bool IsHeavy { get; set; } // training only
}

/// <summary>What the trained model returns.</summary>
public class HeavyPrediction
{
    [ColumnName("PredictedLabel")] public bool IsHeavy { get; set; }
    public float Probability { get; set; }
}

public static class FileFeatureExtractor
{
    public const int HeaderBytes = 4096;

    // Definition of "heavy" used to label the training data: saving one new version of the file
    // adds at least this much to the history after Git's zlib compression.
    public const long HeavyThresholdBytes = 1024 * 1024;

    /// <summary>Reads the size and first 4 KB of a real file.</summary>
    public static FileFeatures FromFile(string fullPath)
    {
        long size;
        byte[] header = new byte[HeaderBytes];
        int read;
        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            size = fs.Length;
            read = fs.ReadAtLeast(header, HeaderBytes, throwOnEndOfStream: false);
        }
        return FromHeader(Path.GetExtension(fullPath), size, header.AsSpan(0, read));
    }

    public static FileFeatures FromHeader(string extension, long size, ReadOnlySpan<byte> header)
    {
        var counts = new int[256];
        int zeros = 0, printable = 0;
        foreach (byte b in header)
        {
            counts[b]++;
            if (b == 0) zeros++;
            // tab, newline, carriage return, printable ASCII, and UTF-8 multi-byte parts
            if (b is 9 or 10 or 13 || (b >= 32 && b < 127) || b >= 0x80) printable++;
        }

        double entropy = 0;
        if (header.Length > 0)
            foreach (int c in counts)
            {
                if (c == 0) continue;
                double p = (double)c / header.Length;
                entropy -= p * Math.Log2(p);
            }

        return new FileFeatures
        {
            LogSize = (float)Math.Log10(size + 1),
            HeaderEntropy = (float)entropy,
            ZeroByteRatio = header.Length == 0 ? 0 : (float)zeros / header.Length,
            PrintableRatio = header.Length == 0 ? 1 : (float)printable / header.Length,
            Signature = DetectSignature(header),
            Category = CategoryOf(extension)
        };
    }

    /// <summary>
    /// For files that are not on disk (demo data, a deleted file): typical header values for the
    /// extension, so the advisor can still give an estimate. Marked as an estimate in the UI.
    /// </summary>
    public static FileFeatures Estimate(string extension, long size)
    {
        string category = CategoryOf(extension);
        (string sig, float entropy, float zeros, float printable) = extension.ToLowerInvariant() switch
        {
            ".docx" or ".xlsx" or ".pptx" or ".zip" => ("zip", 7.6f, 0.02f, 0.55f),
            ".png" => ("png", 7.8f, 0.01f, 0.53f),
            ".jpg" or ".jpeg" => ("jpeg", 7.6f, 0.02f, 0.55f),
            ".mp4" or ".mov" => ("mp4", 7.5f, 0.05f, 0.50f),
            ".tif" or ".tiff" or ".svs" => ("tiff", 6.5f, 0.15f, 0.45f),
            ".psd" => ("psd", 5.5f, 0.30f, 0.40f),
            ".pdf" => ("pdf", 7.0f, 0.01f, 0.80f),
            _ when category is "code" or "text" or "data" => ("text", 4.8f, 0f, 1f),
            _ => ("unknown", 6.0f, 0.10f, 0.50f)
        };
        return new FileFeatures
        {
            LogSize = (float)Math.Log10(size + 1),
            HeaderEntropy = entropy,
            ZeroByteRatio = zeros,
            PrintableRatio = printable,
            Signature = sig,
            Category = category
        };
    }

    // Magic bytes at the start of common formats. The extension can lie; the header can't.
    public static string DetectSignature(ReadOnlySpan<byte> h)
    {

        if (Starts(h, 0x50, 0x4B, 0x03, 0x04) || Starts(h, 0x50, 0x4B, 0x05, 0x06)) return "zip";   // also docx/xlsx/pptx/nupkg
        if (Starts(h, 0x89, 0x50, 0x4E, 0x47)) return "png";
        if (Starts(h, 0xFF, 0xD8, 0xFF)) return "jpeg";
        if (Starts(h, 0x47, 0x49, 0x46, 0x38)) return "gif";
        if (Starts(h, 0x25, 0x50, 0x44, 0x46)) return "pdf";
        if (Starts(h, 0x49, 0x49, 0x2A, 0x00) || Starts(h, 0x4D, 0x4D, 0x00, 0x2A) ||
            Starts(h, 0x49, 0x49, 0x2B, 0x00) || Starts(h, 0x4D, 0x4D, 0x00, 0x2B)) return "tiff";   // incl. BigTIFF (.svs)
        if (Starts(h, 0x38, 0x42, 0x50, 0x53)) return "psd";
        if (Starts(h, 0xD0, 0xCF, 0x11, 0xE0)) return "ole";        // old .doc/.xls/.msi
        if (Starts(h, 0x4D, 0x5A)) return "exe";                    // .exe/.dll
        if (Starts(h, 0x1F, 0x8B)) return "gzip";
        if (Starts(h, 0x37, 0x7A, 0xBC, 0xAF)) return "7z";
        if (Starts(h, 0x52, 0x61, 0x72, 0x21)) return "rar";
        if (Starts(h, 0x52, 0x49, 0x46, 0x46)) return "riff";       // wav/avi/webp
        if (Starts(h, 0x49, 0x44, 0x33) || Starts(h, 0xFF, 0xFB)) return "mp3";
        if (Starts(h, 0x66, 0x4C, 0x61, 0x43)) return "flac";
        if (Starts(h, 0x00, 0x01, 0x00, 0x00) || Starts(h, 0x4F, 0x54, 0x54, 0x4F)) return "font";
        if (Starts(h, 0x1A, 0x45, 0xDF, 0xA3)) return "mkv";
        if (h.Length >= 8 && h[4..8].SequenceEqual("ftyp"u8)) return "mp4";   // mp4/mov/m4a/heic
        if (h.Length > 0 && h.IndexOf((byte)0) < 0) return "text";
        return "unknown";
    }

    private static bool Starts(ReadOnlySpan<byte> header, params byte[] magic) =>
        header.Length >= magic.Length && header[..magic.Length].SequenceEqual(magic);

    public static string CategoryOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".odt" or ".rtf" or ".pdf" or ".pptx" or ".ppt" or ".odp" or ".epub" => "document",
        ".xls" or ".xlsx" or ".ods" or ".csv" or ".tsv" or ".json" or ".xml" or ".yaml" or ".yml" or ".sqlite" or ".db" => "data",
        ".txt" or ".md" or ".log" or ".ini" or ".cfg" or ".html" or ".htm" or ".css" or ".tex" or ".bib" => "text",
        ".cs" or ".py" or ".js" or ".ts" or ".java" or ".cpp" or ".c" or ".h" or ".xaml" or ".ps1" or ".sh" or ".sql" => "code",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".svs" or ".psd" or ".webp"
            or ".ico" or ".svg" or ".heic" or ".raw" or ".cr2" or ".nef" => "image",
        ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".webm" => "video",
        ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".wma" => "audio",
        ".zip" or ".7z" or ".rar" or ".gz" or ".tar" or ".nupkg" or ".cab" or ".iso" => "archive",
        ".exe" or ".dll" or ".msi" or ".sys" or ".pdb" or ".lib" or ".so" => "executable",
        ".ttf" or ".otf" or ".woff" or ".woff2" => "font",
        _ => "other"
    };
}

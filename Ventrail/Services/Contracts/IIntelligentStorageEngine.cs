using System.Collections.Generic;
using Ventrail.Models;

namespace Ventrail.Services.Contracts;

/// <summary>
/// Offline ML.NET advisor that checks files before they are saved in a version.
/// </summary>
public interface IIntelligentStorageEngine
{
    string MlModelPath { get; }
    List<string> SupportedFormats { get; }

    // False if the model file is missing or can't be loaded (the app then falls back to the
    // 50 MB size rule).
    bool IsModelLoaded { get; }

    // Friendly format name from the file's header bytes, e.g. "JPEG picture".
    string AnalyzeFileFormat(string filePath);

    // Storage advice for a kind of file ("report.docx", ".docx" or "docx") and size,
    // estimated from the extension. Used when the file itself can't be read.
    string GenerateRecommendation(string fileType, long fileSize);

    // Storage and sharing tip for the selected file, from its verdict. Never loads
    // the model, so it is safe to call from the UI thread.
    string GetStorageTip(FileAsset file);

    // Suggested commit message and storage advice for the ticked files.
    CommitSuggestion SuggestCommit(IReadOnlyList<FileAsset> stagedFiles);

    // Heavy/light prediction for one file. fullPath may not exist (demo data, deleted
    // file); the size and extension are then used for an estimate. Null if no model is loaded.
    FileClassification? ClassifyFile(string fullPath, long sizeInBytes);
}

using System.Collections.Generic;
using Ventrail.Models;

namespace Ventrail.Services.Contracts;

/// <summary>
/// Offline ML.NET pre-commit advisor (IntelligentStorageEngine in Fig 3.8).
/// </summary>
public interface IIntelligentStorageEngine
{
    string MlModelPath { get; }
    List<string> SupportedFormats { get; }

    // Week 8: false if the model file is missing or can't be loaded (the app then falls back
    // to the Week 6 size rule).
    bool IsModelLoaded { get; }

    // Friendly format name from the file's header bytes, e.g. "JPEG picture".
    string AnalyzeFileFormat(string filePath);

    // Week 9: storage advice for a kind of file ("report.docx", ".docx" or "docx") and size,
    // estimated from the extension. Used when the file itself can't be read.
    string GenerateRecommendation(string fileType, long fileSize);

    // Week 9: storage + sharing tip for the File Inspector, from the file's verdict. Never loads
    // the model, so it is safe to call from the UI thread.
    string GetStorageTip(FileAsset file);

    // Week 9: suggested commit message and storage advice for the ticked files.
    CommitSuggestion SuggestCommit(IReadOnlyList<FileAsset> stagedFiles);

    // Week 8: heavy/light prediction for one file. fullPath may not exist (demo data, deleted
    // file); the size and extension are then used for an estimate. Null if no model is loaded.
    FileClassification? ClassifyFile(string fullPath, long sizeInBytes);
}

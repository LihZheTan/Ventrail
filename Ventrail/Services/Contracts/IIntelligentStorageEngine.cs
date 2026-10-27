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

    // Week 9: storage advice and commit message suggestions.
    string GenerateRecommendation(string fileType, long fileSize);

    // Week 8: heavy/light prediction for one file. fullPath may not exist (demo data, deleted
    // file); the size and extension are then used for an estimate. Null if no model is loaded.
    FileClassification? ClassifyFile(string fullPath, long sizeInBytes);
}

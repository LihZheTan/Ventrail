using System;
using System.Collections.Generic;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Offline ML.NET pre-commit advisor. Stub created in Week 1 from the class diagram (Fig 3.8).
/// Real implementation: CP2 Weeks 8-9.
/// </summary>
public class IntelligentStorageEngine : IIntelligentStorageEngine
{
    public string MlModelPath { get; } = "MLModels/ventrail_classifier.zip";

    // Large or specialised formats the advisor should recognise (extend as needed).
    public List<string> SupportedFormats { get; } = new() { ".svs", ".tif", ".tiff", ".psd", ".zip", ".mp4" };

    public string AnalyzeFileFormat(string filePath) =>
        throw new NotImplementedException("Planned for Week 8: parse file headers and classify heavy vs light files.");

    public string GenerateRecommendation(string fileType, long fileSize) =>
        throw new NotImplementedException("Planned for Week 9: commit message and storage recommendations.");
}

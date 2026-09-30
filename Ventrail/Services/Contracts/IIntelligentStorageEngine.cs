using System.Collections.Generic;

namespace Ventrail.Services.Contracts;

public interface IIntelligentStorageEngine
{
    string MlModelPath { get; }
    List<string> SupportedFormats { get; }

    string AnalyzeFileFormat(string filePath);
    string GenerateRecommendation(string fileType, long fileSize);
}
using System.Collections.Generic;

namespace Ventrail.Services.Contracts;

public interface ISpecializedFileHandler
{
    bool LoadLibraryForType(string fileExtension);
    Dictionary<string, string> ExtractMetadata(string filePath);
    object? GetPreviewThumbnail(string filePath);
}
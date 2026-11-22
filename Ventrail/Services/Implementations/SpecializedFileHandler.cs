using System;
using System.Collections.Generic;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Loads external C-based libraries (e.g. OpenSlide) for specialised formats.
/// Not built yet: every method throws NotImplementedException.
/// </summary>
public class SpecializedFileHandler : ISpecializedFileHandler
{
    // Libraries loaded so far, by file type.
    private readonly Dictionary<string, object> _activeLibraries = new();

    public bool LoadLibraryForType(string fileExtension)
    {
        throw new NotImplementedException("Optional feature: load native library for this file type.");
    }

    public Dictionary<string, string> ExtractMetadata(string filePath)
    {
        throw new NotImplementedException("Optional feature: extract metadata from specialised files.");
    }

    public object? GetPreviewThumbnail(string filePath)
    {
        throw new NotImplementedException("Optional feature: generate preview thumbnail.");
    }
}

using System;
using System.Collections.Generic;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Loads external C-based libraries (e.g. OpenSlide) for specialised formats.
/// Stub created in Week 1 from the class diagram (Fig 3.8). Lower priority: implement only if time allows.
/// </summary>
public class SpecializedFileHandler : ISpecializedFileHandler
{
    // From the class diagram: -Dictionary<String, Object> activeLibraries
    private readonly Dictionary<string, object> _activeLibraries = new();

    public bool LoadLibraryForType(string fileExtension) =>
        throw new NotImplementedException("Optional feature: load native library for this file type.");

    public Dictionary<string, string> ExtractMetadata(string filePath) =>
        throw new NotImplementedException("Optional feature: extract metadata from specialised files.");

    public object? GetPreviewThumbnail(string filePath) =>
        throw new NotImplementedException("Optional feature: generate preview thumbnail.");
}

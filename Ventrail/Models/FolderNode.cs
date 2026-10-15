using System.Collections.Generic;

namespace Ventrail.Models;

/// <summary>One entry in the workspace tree shown in the left pane.</summary>
public class FolderNode
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public List<FolderNode> Children { get; set; } = new();

    // Segoe Fluent / MDL2 icon glyphs: folder or document.
    public string Icon => IsFolder ? "" : "";
}

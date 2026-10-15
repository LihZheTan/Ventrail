using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ventrail.Models;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Builds the workspace tree for the left pane. Depth and entry count are capped so
/// opening a huge folder never freezes or floods the UI.
/// </summary>
public static class FolderTreeBuilder
{
    public static List<FolderNode> FromDirectory(string root, int maxDepth = 3, int maxEntries = 400)
    {
        int count = 0;
        return Walk(new DirectoryInfo(root), root, 1, maxDepth, maxEntries, ref count);
    }

    // Used by demo mode, where the folder doesn't exist on disk: build the tree from file paths.
    public static List<FolderNode> FromPaths(IEnumerable<string> relativePaths)
    {
        var roots = new List<FolderNode>();
        foreach (string path in relativePaths.Distinct())
        {
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var level = roots;
            for (int i = 0; i < parts.Length; i++)
            {
                bool isFolder = i < parts.Length - 1;
                var node = level.FirstOrDefault(n => n.Name == parts[i] && n.IsFolder == isFolder);
                if (node == null)
                {
                    node = new FolderNode
                    {
                        Name = parts[i],
                        IsFolder = isFolder,
                        RelativePath = string.Join('/', parts.Take(i + 1))
                    };
                    level.Add(node);
                }
                level = node.Children;
            }
        }
        Sort(roots);
        return roots;
    }

    private static List<FolderNode> Walk(DirectoryInfo dir, string root, int depth, int maxDepth,
                                         int maxEntries, ref int count)
    {
        var result = new List<FolderNode>();
        try
        {
            foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name))
            {
                if (sub.Name == ".git" || count >= maxEntries) continue;
                count++;
                var node = new FolderNode
                {
                    Name = sub.Name,
                    IsFolder = true,
                    RelativePath = Path.GetRelativePath(root, sub.FullName)
                };
                if (depth < maxDepth)
                    node.Children = Walk(sub, root, depth + 1, maxDepth, maxEntries, ref count);
                result.Add(node);
            }
            foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name))
            {
                if (count >= maxEntries) break;
                count++;
                result.Add(new FolderNode
                {
                    Name = file.Name,
                    IsFolder = false,
                    RelativePath = Path.GetRelativePath(root, file.FullName)
                });
            }
        }
        catch (UnauthorizedAccessException) { /* skip folders we may not read */ }
        catch (IOException) { }
        return result;
    }

    private static void Sort(List<FolderNode> nodes)
    {
        nodes.Sort((a, b) => a.IsFolder != b.IsFolder
            ? (a.IsFolder ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var n in nodes) Sort(n.Children);
    }
}

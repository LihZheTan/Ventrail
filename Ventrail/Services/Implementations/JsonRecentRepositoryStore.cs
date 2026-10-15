using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Implementations;

/// <summary>
/// Saves the recent-folders list to %AppData%\Ventrail\recent-repositories.json.
/// Stays local to the computer, matching Ventrail's privacy-first goal. Never throws:
/// a missing or damaged file simply means "no recent folders".
/// </summary>
public class JsonRecentRepositoryStore : IRecentRepositoryStore
{
    private const int MaxEntries = 6;
    private readonly string _file;

    public JsonRecentRepositoryStore(string? filePath = null)
    {
        _file = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ventrail", "recent-repositories.json");
    }

    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_file)) return Array.Empty<string>();
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new();
            return list.Where(Directory.Exists).ToList();   // hide folders that were moved or deleted
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public void Add(string folderPath)
    {
        try
        {
            string normalized = folderPath.TrimEnd('\\', '/');
            var list = Load().Where(p => !string.Equals(p.TrimEnd('\\', '/'), normalized,
                                                        StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(list.Take(MaxEntries).ToList()));
        }
        catch (Exception)
        {
            // Not being able to remember a folder must never stop the user working.
        }
    }
}

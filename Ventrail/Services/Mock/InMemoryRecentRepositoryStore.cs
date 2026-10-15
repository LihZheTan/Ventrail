using System;
using System.Collections.Generic;
using System.Linq;
using Ventrail.Services.Contracts;

namespace Ventrail.Services.Mock;

/// <summary>Demo-mode recent list: pre-filled, never written to disk.</summary>
public class InMemoryRecentRepositoryStore : IRecentRepositoryStore
{
    private readonly List<string> _items = new() { @"C:\Demo\Thesis", @"C:\Demo\Research Data" };

    public IReadOnlyList<string> Load() => _items.ToList();

    public void Add(string folderPath)
    {
        _items.RemoveAll(p => string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, folderPath);
    }
}

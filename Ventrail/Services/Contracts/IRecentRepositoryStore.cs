using System.Collections.Generic;

namespace Ventrail.Services.Contracts;

/// <summary>Remembers recently opened folders for the Home page.</summary>
public interface IRecentRepositoryStore
{
    IReadOnlyList<string> Load();
    void Add(string folderPath);
}

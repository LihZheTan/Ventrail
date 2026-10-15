using System.Collections.Generic;

namespace Ventrail.Services.Contracts;

/// <summary>Remembers recently opened folders for the Welcome page (Week 4).</summary>
public interface IRecentRepositoryStore
{
    IReadOnlyList<string> Load();
    void Add(string folderPath);
}

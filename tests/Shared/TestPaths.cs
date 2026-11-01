using System;
using System.IO;

/// <summary>Finds the repository from wherever the test runs, so no test depends on one PC's folders.</summary>
static class TestPaths
{
    public static string RepoRoot { get; } = Find();

    // The Ventrail project folder and its trained ML.NET model.
    public static string Project => Path.Combine(RepoRoot, "Ventrail");
    public static string Model => Path.Combine(Project, "MLModels", "ventrail_classifier.zip");

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ventrail.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Run the tests from inside the Ventrail repository (Ventrail.slnx not found).");
    }
}

using System;
using System.IO;

static class TestPaths
{
    public static string RepoRoot { get; } = Find();

    public static string Project => Path.Combine(RepoRoot, "Ventrail");
    public static string Model => Path.Combine(Project, "MLModels", "ventrail_classifier.zip");

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ventrail.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Run the tests from inside the Ventrail repository (Ventrail.slnx not found).");
    }
}

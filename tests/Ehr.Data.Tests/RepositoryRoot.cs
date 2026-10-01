namespace Ehr.Data.Tests;

static class RepositoryRoot
{
    // The folder holding Ehr.slnx, found by walking up from the test binaries.
    public static string Path { get; } = Find();

    public static string Combine(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Ehr.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Couldn't find the repository root (the folder holding Ehr.slnx).");
    }
}

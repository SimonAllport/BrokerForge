namespace BrokerForge.Descriptors.Tests;

internal static class RepoPaths
{
    /// <summary>The repository root, found by walking up from the test binaries to CLAUDE.md.</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (CLAUDE.md).");
        }
    }

    public static string Descriptors => Path.Combine(Root, "descriptors");
}

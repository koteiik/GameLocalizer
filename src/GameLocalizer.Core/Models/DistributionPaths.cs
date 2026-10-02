namespace GameLocalizer.Core.Models;

/// <summary>Installation paths, independent of the current working directory and user-data storage.</summary>
public static class DistributionPaths
{
    public static string InstallRoot(string baseDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var parent = Directory.GetParent(directory)?.FullName;
        return Path.GetFileName(directory).Equals("app", StringComparison.OrdinalIgnoreCase) && parent != null &&
            File.Exists(Path.Combine(directory, "GameLocalizer.dll")) && File.Exists(Path.Combine(parent, "GameLocalizer.exe")) ? parent : directory;
    }
    public static string InternalDirectory(string baseDirectory)
    {
        var root = InstallRoot(baseDirectory);
        return File.Exists(Path.Combine(root, "app", "GameLocalizer.dll")) ? Path.Combine(root, "app") : root;
    }
    public static string ModelHost(string baseDirectory) => Path.Combine(InternalDirectory(baseDirectory), "GameLocalizer.ModelHost.exe");
    public static string Updater(string baseDirectory) => Path.Combine(InternalDirectory(baseDirectory), "Updater");
    public static string Metadata(string installation, string name)
    {
        var clean = Path.Combine(installation, "app", name);
        return File.Exists(clean) ? clean : Path.Combine(installation, name);
    }
}

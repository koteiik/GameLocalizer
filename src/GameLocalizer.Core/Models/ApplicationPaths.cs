using System.Reflection;
namespace GameLocalizer.Core.Models;

public static class ApplicationPaths
{
    // Build-time identity permits isolated installer integration fixtures without changing a user's real data.
    private static string Metadata(string key) => typeof(ApplicationPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
    public static string AppId => Metadata("InstallerAppId");
    public static string DataDirectoryName => Metadata("UserDataDirectoryName");
    public static string UserData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryName);
    public static string AppMutex => @"Local\GameLocalizer-" + AppId;
}

using GameLocalizer.Core.Models;
using Microsoft.Win32;

namespace GameLocalizer.Infrastructure.Update;

public record InstalledRegistration(string Directory, string Version);
public record InstallationInfo(string Directory, string Version, bool Installed, bool Writable, string UserDataDirectory)
{
    public string Mode => Installed ? "Installed (Inno Setup)" : "Portable";
}
public sealed class InstallationInfoService(string? directory = null, Func<InstalledRegistration?>? registration = null)
{
    public static string RegistryPath => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + ApplicationPaths.AppId + "}_is1";
    public InstallationInfo GetInfo()
    {
        var path = UpdatePaths.Canonical(directory ?? AppContext.BaseDirectory);
        var record = (registration ?? ReadRegistration)();
        var installed = record != null && UpdatePaths.Canonical(record.Directory).Equals(path, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(path, "unins000.exe")) && File.Exists(Path.Combine(path, "installation.ini"));
        var writable = false;
        try
        {
            UpdatePaths.NoLinks(path);
            var probe = Path.Combine(path, ".gl-write-probe-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            writable = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new(path, installed ? record!.Version : ApplicationVersion.Current.ToString(), installed, writable, ApplicationPaths.UserData);
    }
    private static InstalledRegistration? ReadRegistration()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.OpenSubKey(RegistryPath);
        return key?.GetValue("InstallLocation") is string path && key.GetValue("DisplayVersion") is string version ? new(path, version) : null;
    }
}

public static class UninstallDataPolicy
{
    public static bool ShouldDelete(bool explicitConsent, bool silent) => explicitConsent && !silent;
}

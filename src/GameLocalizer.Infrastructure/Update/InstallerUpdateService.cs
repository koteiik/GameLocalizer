using System.Diagnostics;
using GameLocalizer.Core.Models;

namespace GameLocalizer.Infrastructure.Update;

public interface IInstallerUpdateService
{
    Task<ProcessStartInfo> PrepareAsync(AppRelease release, string setup, InstallationInfo installation, CancellationToken ct);
    void Launch(ProcessStartInfo start);
}

public sealed class InstallerUpdateService : IInstallerUpdateService
{
    public async Task<ProcessStartInfo> PrepareAsync(AppRelease release, string setup, InstallationInfo installation, CancellationToken ct)
    {
        if (!installation.Installed || !installation.Writable || release.Asset != ReleaseClient.InstallerAssetName || !release.CanInstall ||
            !ReleaseClient.IsNewer(release, installation.Version)) throw new InvalidDataException("Недопустимое обновление установленного приложения.");
        var expected = Path.Combine(UpdateHandoff.Root, release.Tag, ReleaseClient.InstallerAssetName);
        if (!UpdatePaths.Canonical(setup).Equals(UpdatePaths.Canonical(expected), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Недоверенный путь установщика.");
        UpdatePaths.NoLinks(setup); UpdatePaths.NoLinks(installation.Directory);
        await UpdatePackage.VerifyHashAsync(setup, release.Sha256!, ct);
        // No shell command or URL can become an argument. Inno waits for the app mutex before changing files.
        var start = new ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)! };
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/UPDATE", "/DIR=" + installation.Directory,
            "/LOG=" + Path.Combine(UpdateHandoff.Root, release.Tag, "setup.log") }) start.ArgumentList.Add(argument);
        return start;
    }
    public void Launch(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить установщик. Приложение остаётся открытым.");
    }
}

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
        if (!installation.Installed || !installation.Writable || !ReleaseClient.IsInstallerAsset(release.Asset) || !release.CanInstall ||
            !ReleaseClient.IsNewer(release, installation.Version)) throw new InvalidDataException("Недопустимое обновление установленного приложения.");
        var expected = Path.Combine(UpdateHandoff.Root, release.Tag, release.Asset);
        if (!UpdatePaths.Canonical(setup).Equals(UpdatePaths.Canonical(expected), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Недоверенный путь установщика.");
        UpdatePaths.NoLinks(setup); UpdatePaths.NoLinks(installation.Directory);
        await UpdatePackage.VerifyHashAsync(setup, release.Sha256!, ct);
        var id = Guid.NewGuid().ToString("N");
        using var current = Process.GetCurrentProcess();
        var request = new InstalledUpdateRequest(id, setup, installation.Directory, installation.Version, release, current.Id, current.StartTime.ToUniversalTime().Ticks);
        var supervisor = new InstalledUpdate(UpdateHandoff.Root); supervisor.Validate(request);
        var runner = Path.Combine(UpdateHandoff.Root, "InstalledRunner", id);
        await Task.Run(() => UpdatePaths.CopyTree(DistributionPaths.Updater(installation.Directory), runner, ct), ct);
        supervisor.Save(new(request, "Prepared"));
        var start = new ProcessStartInfo(Path.Combine(runner, "GameLocalizer.Updater.exe")) { UseShellExecute = false, WorkingDirectory = runner };
        start.ArgumentList.Add("--installed-update"); start.ArgumentList.Add(supervisor.JournalPath(id));
        return start;
    }
    public void Launch(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить установщик. Приложение остаётся открытым.");
    }
}

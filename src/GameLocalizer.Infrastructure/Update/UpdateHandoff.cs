using System.Diagnostics;
using System.Text.Json;

namespace GameLocalizer.Infrastructure.Update;

public sealed class UpdateHandoff
{
    public static string Root => Path.Combine(Core.Models.ApplicationPaths.UserData, "Updates");
    public static async Task<string> PrepareAsync(AppRelease release, string zip, CancellationToken ct)
    {
        var install = UpdatePaths.Canonical(AppContext.BaseDirectory);
        UpdateInstaller.ProbeInstallDirectory(install);
        await UpdatePackage.VerifyHashAsync(zip, release.Sha256!, ct);
        await Task.Run(() => UpdateInstaller.CheckFreeSpace(install, Root, zip, ct), ct);
        var id = Guid.NewGuid().ToString("N");
        // Preflight before closing the main application. Installer repeats hash/extraction afterwards.
        await Task.Run(() => UpdatePackage.Extract(zip, Path.Combine(Root, "Preflight", id), release.Version.ToString(), ct, release.Sha256), ct);
        var runner = Path.Combine(Root, "Runner", id);
        await Task.Run(() => UpdatePaths.CopyTree(Path.Combine(install, "Updater"), runner, ct), ct);
        using var current = Process.GetCurrentProcess();
        var request = new UpdateRequest(id, zip, install, current.Id, current.StartTime.ToUniversalTime().Ticks, Core.Models.ApplicationVersion.Label, release);
        var installer = new UpdateInstaller(Root, new UpdateProcesses(Root)); installer.Validate(request); installer.Save(request, InstallPhase.Prepared);
        return installer.JournalPath(request);
    }
    public static void Launch(string journal)
    {
        var installer = new UpdateInstaller(Root, new UpdateProcesses(Root)); var request = installer.ReadRequest(journal);
        var executable = Path.Combine(Root, "Runner", request.Id, "GameLocalizer.Updater.exe"); UpdatePaths.NoLinks(executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, CreateNoWindow = true };
        start.ArgumentList.Add("--install"); start.ArgumentList.Add(journal);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить updater.");
    }
    public static UpdateNotification? Startup(string[] arguments)
    {
        var position = Array.IndexOf(arguments, "--update-ready");
        if (position >= 0 && position + 1 < arguments.Length && Guid.TryParseExact(arguments[position + 1], "N", out _))
        {
            var id = arguments[position + 1]; var installer = new UpdateInstaller(Root, new UpdateProcesses(Root));
            var request = installer.ReadRequest(Path.Combine(Root, "Transactions", id + ".json"));
            if (request.Release.Tag != Core.Models.ApplicationVersion.Label || !request.InstallDirectory.Equals(UpdatePaths.Canonical(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Запущена неверная версия обновления.");
            var ready = Path.Combine(Root, "Transactions", id + ".ready"); UpdatePaths.NoLinks(ready); File.WriteAllText(ready, id);
        }
        return ConsumeNotification(Root, AppContext.BaseDirectory, Core.Models.ApplicationVersion.Label);
    }
    public static UpdateNotification? ConsumeNotification(string root, string installation, string version)
    {
        var notice = UpdateInstaller.NotificationPath(root, installation); UpdatePaths.NoLinks(notice);
        if (!File.Exists(notice)) return null;
        if (new FileInfo(notice).Length > 4 * 1024 * 1024) throw new InvalidDataException("Уведомление повреждено.");
        var notification = JsonSerializer.Deserialize<UpdateNotification>(File.ReadAllText(notice));
        if (notification?.Version != version || !notification.InstallDirectory.Equals(UpdatePaths.Canonical(installation), StringComparison.OrdinalIgnoreCase)) return null;
        File.Delete(notice); return notification;
    }
}

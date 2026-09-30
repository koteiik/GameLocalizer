using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GameLocalizer.Core.Models;
using Microsoft.Win32;

namespace GameLocalizer.Infrastructure.Update;

public sealed record InstalledUpdateRequest(string Id, string Setup, string Install, string OldVersion, AppRelease Release, int ParentId, long ParentTicks);
public sealed record RegistrySnapshotValue(string Name, RegistryValueKind Kind, JsonElement Value);
public sealed record InstalledUpdateJournal(InstalledUpdateRequest Request, string Phase, IReadOnlyList<PackageFile>? Files = null, IReadOnlyList<RegistrySnapshotValue>? Registry = null, string? Error = null);

/// <summary>Inno performs installation; this durable supervisor keeps the previous installation until startup is confirmed.</summary>
public sealed class InstalledUpdate(string root)
{
    public string JournalPath(string id) => Path.Combine(root, "InstalledTransactions", Id(id) + ".json");
    private string Backup(string id) => Path.Combine(root, "InstalledBackup", Id(id));
    private string Ready(string id) => Path.Combine(root, "InstalledTransactions", Id(id) + ".ready");
    private static string Id(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new InvalidDataException("Invalid transaction ID.");
    public void Save(InstalledUpdateJournal journal) => UpdatePaths.WriteJson(JournalPath(journal.Request.Id), journal);
    public InstalledUpdateJournal Read(string path)
    {
        UpdatePaths.NoLinks(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Journal too large.");
        var journal = JsonSerializer.Deserialize<InstalledUpdateJournal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid journal.");
        if (!UpdatePaths.Canonical(path).Equals(UpdatePaths.Canonical(JournalPath(journal.Request.Id)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid journal path.");
        Validate(journal.Request, requireInstalled: false); return journal;
    }
    public void Validate(InstalledUpdateRequest request, bool requireInstalled = true)
    {
        Id(request.Id); UpdatePaths.NoLinks(root); UpdatePaths.NoLinks(request.Install); UpdatePaths.NoLinks(request.Setup);
        if (UpdatePaths.Inside(root, request.Install) || UpdatePaths.Inside(request.Install, root) || UpdatePaths.Canonical(root).Equals(UpdatePaths.Canonical(request.Install), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installation and recovery storage must be separate.");
        if (request.Release.Asset != ReleaseClient.InstallerAssetName || !request.Release.CanInstall || !ReleaseClient.IsNewer(request.Release, request.OldVersion) ||
            !UpdatePaths.Canonical(request.Setup).Equals(UpdatePaths.Canonical(Path.Combine(root, request.Release.Tag, ReleaseClient.InstallerAssetName)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid installer request.");
        if (requireInstalled && !new InstallationInfoService(request.Install).GetInfo().Installed) throw new InvalidDataException("Registered installation required.");
    }
    public static IReadOnlyList<PackageFile> Inventory(string directory)
    {
        var files = new List<PackageFile>();
        void Walk(string current)
        {
            UpdatePaths.NoLinks(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                UpdatePaths.NoLinks(entry);
                var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
                UpdatePaths.RelativeFile(directory, relative);
                if (Directory.Exists(entry)) Walk(entry);
                else { using var input = File.OpenRead(entry); files.Add(new(relative, input.Length, Convert.ToHexString(SHA256.HashData(input)))); }
            }
        }
        Walk(directory); return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }
    public static void VerifyInventory(string directory, IReadOnlyList<PackageFile> files)
    {
        foreach (var file in files)
        {
            var path = UpdatePaths.RelativeFile(directory, file.Path);
            using var input = File.OpenRead(path);
            if (input.Length != file.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload hash mismatch: " + file.Path);
        }
    }
    private static IReadOnlyList<RegistrySnapshotValue> CaptureRegistry()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.OpenSubKey(InstallationInfoService.RegistryPath) ?? throw new IOException("Registration missing.");
        var values = new List<RegistrySnapshotValue>();
        foreach (var name in key.GetValueNames()) values.Add(new(name, key.GetValueKind(name), JsonSerializer.SerializeToElement(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames))));
        return values;
    }
    private static void RestoreRegistry(IReadOnlyList<RegistrySnapshotValue> values)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.CreateSubKey(InstallationInfoService.RegistryPath);
        foreach (var name in key.GetValueNames()) if (!values.Any(v => v.Name == name)) key.DeleteValue(name);
        foreach (var value in values)
        {
            object data = value.Kind switch
            {
                RegistryValueKind.DWord => value.Value.GetInt32(), RegistryValueKind.QWord => value.Value.GetInt64(),
                RegistryValueKind.Binary => value.Value.GetBytesFromBase64(),
                RegistryValueKind.MultiString => value.Value.Deserialize<string[]>()!,
                RegistryValueKind.String or RegistryValueKind.ExpandString => value.Value.GetString()!,
                _ => throw new InvalidDataException("Unsupported registry value type.")
            };
            key.SetValue(value.Name, data, value.Kind);
        }
    }
    private static async Task WaitForParent(InstalledUpdateRequest request, CancellationToken ct)
    {
        Process parent;
        try { parent = Process.GetProcessById(request.ParentId); } catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentTicks) return;
            await parent.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
    }
    public async Task ExecuteAsync(string journalPath, CancellationToken ct)
    {
        var journal = Read(journalPath); var request = journal.Request;
        if (journal.Phase != "Prepared") throw new InvalidDataException("Use recovery for an interrupted transaction.");
        Validate(request);
        await WaitForParent(request, ct);
        if (Mutex.TryOpenExisting(ApplicationPaths.AppMutex, out var running)) { running.Dispose(); throw new IOException("Close GameLocalizer before updating."); }
        await UpdatePackage.VerifyHashAsync(request.Setup, request.Release.Sha256!, ct);
        var files = Inventory(request.Install);
        if (Directory.Exists(Backup(request.Id))) throw new IOException("Backup already exists.");
        UpdatePaths.CopyTree(request.Install, Backup(request.Id), ct); VerifyInventory(Backup(request.Id), files);
        journal = journal with { Phase = "BackedUp", Files = files, Registry = CaptureRegistry() }; Save(journal);
        try
        {
            Save(journal = journal with { Phase = "Installing" });
            var start = new ProcessStartInfo(request.Setup) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(request.Setup)! };
            foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/UPDATE", "/NOLAUNCH", "/DIR=" + request.Install, "/LOG=" + Path.Combine(root, request.Release.Tag, "setup.log") }) start.ArgumentList.Add(arg);
            using (var setup = Process.Start(start) ?? throw new IOException("Installer failed to start."))
            { await setup.WaitForExitAsync(ct); if (setup.ExitCode != 0) throw new IOException("Installer exit code: " + setup.ExitCode); }
            var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(request.Install, "installer-payload.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Payload manifest missing.");
            if (manifest.Version != request.Release.Version.ToString()) throw new InvalidDataException("Installed version mismatch.");
            VerifyInventory(request.Install, manifest.Files);
            Save(journal = journal with { Phase = "Starting" });
            var appStart = new ProcessStartInfo(Path.Combine(request.Install, "GameLocalizer.exe")) { UseShellExecute = false, WorkingDirectory = request.Install };
            appStart.ArgumentList.Add("--installer-ready"); appStart.ArgumentList.Add(request.Id);
            using var app = Process.Start(appStart) ?? throw new IOException("Updated application failed to start.");
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!File.Exists(Ready(request.Id)))
                { if (app.HasExited || DateTime.UtcNow > deadline) throw new IOException("Updated application did not confirm startup."); await Task.Delay(250, ct); }
                await Task.Delay(2000, ct); if (app.HasExited) throw new IOException("Updated application exited during startup.");
            }
            catch { if (!app.HasExited) { app.Kill(); await app.WaitForExitAsync(CancellationToken.None); } throw; }
            Save(journal with { Phase = "Complete" });
        }
        catch (Exception error)
        {
            Save(journal with { Error = error.GetType().Name + ": " + error.Message });
            try { Recover(journalPath); StartPrevious(request.Install); }
            catch (Exception recoveryError)
            {
                Save(journal with { Phase = "RollbackFailed", Error = error.Message + "; Recovery: " + recoveryError.Message });
                throw;
            }
        }
    }
    public void Recover(string journalPath)
    {
        var journal = Read(journalPath);
        if (journal.Phase is "Prepared" or "Complete" or "RolledBack" || journal.Files == null || journal.Registry == null) throw new InvalidDataException("No pending recovery.");
        var location = journal.Registry.SingleOrDefault(v => v.Name == "InstallLocation")?.Value.GetString();
        if (location == null || !UpdatePaths.Canonical(location).Equals(UpdatePaths.Canonical(journal.Request.Install), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Recovery registration does not match the installation.");
        if (Mutex.TryOpenExisting(ApplicationPaths.AppMutex, out var running)) { running.Dispose(); throw new IOException("Close GameLocalizer before recovery."); }
        VerifyInventory(Backup(journal.Request.Id), journal.Files);
        // Only installer-owned new files may be removed; unknown files and user data are never deleted.
        var ownedList = Path.Combine(journal.Request.Install, "install-files.txt");
        var owned = File.Exists(ownedList) ? File.ReadAllLines(ownedList) : [];
        var previous = journal.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in owned)
        {
            var relative = name.Replace('\\', '/'); var path = UpdatePaths.RelativeFile(journal.Request.Install, relative);
            if (!previous.Contains(relative) && File.Exists(path)) File.Delete(path);
        }
        UpdatePaths.CopyTree(Backup(journal.Request.Id), journal.Request.Install, CancellationToken.None);
        VerifyInventory(journal.Request.Install, journal.Files); RestoreRegistry(journal.Registry);
        Save(journal with { Phase = "RolledBack" });
    }
    public static void StartPrevious(string installation)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(installation, "GameLocalizer.exe")) { UseShellExecute = false, WorkingDirectory = installation });
    }
    public static void ConfirmStartup(string[] args)
    {
        var position = Array.IndexOf(args, "--installer-ready"); if (position < 0 || position + 1 >= args.Length) return;
        var supervisor = new InstalledUpdate(UpdateHandoff.Root); var id = Id(args[position + 1]);
        var journal = supervisor.Read(supervisor.JournalPath(id));
        if (journal.Phase != "Starting" || journal.Request.Release.Version.ToString() != ApplicationVersion.Current.ToString() ||
            !UpdatePaths.Canonical(AppContext.BaseDirectory).Equals(UpdatePaths.Canonical(journal.Request.Install), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected startup confirmation.");
        UpdatePaths.NoLinks(supervisor.Ready(id)); File.WriteAllText(supervisor.Ready(id), id);
    }
}

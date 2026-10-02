using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace GameLocalizer.Infrastructure.Update;

public sealed record UpdateRequest(string Id, string ZipPath, string InstallDirectory, int ParentPid, long ParentStartTicks, string OldVersion, AppRelease Release);
public enum InstallPhase { Prepared, BackedUp, MovingOld, OldMoved, NewMoved, Starting, Complete, RolledBack }
public sealed record UpdateJournal(UpdateRequest Request, InstallPhase Phase);
public sealed record UpdateNotification(string Version, string Notes, string InstallDirectory);
public interface IUpdateProcesses
{
    Task WaitForParentAsync(UpdateRequest request, CancellationToken ct);
    Task<bool> StartAndConfirmAsync(string installDirectory, string transactionId, CancellationToken ct);
    void StartPrevious(string installDirectory);
}
public sealed class UpdateProcesses(string updatesRoot) : IUpdateProcesses
{
    public async Task WaitForParentAsync(UpdateRequest request, CancellationToken ct)
    {
        try
        {
            using var parent = Process.GetProcessById(request.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks || !string.Equals(parent.MainModule?.FileName, Path.Combine(request.InstallDirectory, "GameLocalizer.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException("PID не соответствует обновляемому приложению.");
            await parent.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
        catch (ArgumentException) { } // Parent has already exited.
        foreach (var process in Process.GetProcessesByName("GameLocalizer"))
        {
            using (process)
            {
                try { if (string.Equals(process.MainModule?.FileName, Path.Combine(request.InstallDirectory, "GameLocalizer.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException("Закройте другие экземпляры GameLocalizer перед обновлением."); }
                catch (System.ComponentModel.Win32Exception) { throw new IOException("Не удалось проверить работающие экземпляры приложения."); }
            }
        }
    }
    public async Task<bool> StartAndConfirmAsync(string installDirectory, string transactionId, CancellationToken ct)
    {
        var ready = Path.Combine(updatesRoot, "Transactions", transactionId + ".ready");
        UpdatePaths.NoLinks(ready);
        if (File.Exists(ready)) File.Delete(ready);
        var start = new ProcessStartInfo(Path.Combine(installDirectory, "GameLocalizer.exe")) { UseShellExecute = false, WorkingDirectory = installDirectory };
        start.ArgumentList.Add("--update-ready"); start.ArgumentList.Add(transactionId);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить новую версию.");
        var watch = Stopwatch.StartNew(); var confirmed = false;
        try
        {
            while (watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                ct.ThrowIfCancellationRequested(); if (process.HasExited) return false;
                if (File.Exists(ready) && File.ReadAllText(ready) == transactionId)
                {
                    await Task.Delay(2000, ct); confirmed = !process.HasExited; return confirmed;
                }
                await Task.Delay(250, ct);
            }
            return false;
        }
        finally { if (!confirmed && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    public void StartPrevious(string installDirectory) => Process.Start(new ProcessStartInfo(Path.Combine(installDirectory, "GameLocalizer.exe")) { UseShellExecute = false, WorkingDirectory = installDirectory });
}

/// <summary>Durable directory-swap transaction. The updater itself runs from Updates/Runner, outside the installation.</summary>
public sealed class UpdateInstaller(string updatesRoot, IUpdateProcesses processes)
{
    public string UpdatesRoot { get; } = UpdatePaths.Canonical(updatesRoot);
    public string JournalPath(UpdateRequest request) => Path.Combine(UpdatesRoot, "Transactions", request.Id + ".json");
    public string StagePath(UpdateRequest request) => request.InstallDirectory + ".gl-stage-" + request.Id;
    public string PreviousPath(UpdateRequest request) => request.InstallDirectory + ".gl-previous-" + request.Id;
    public string BackupPath(UpdateRequest request) => Path.Combine(UpdatesRoot, "Backup", request.OldVersion, request.Id);
    public static string NotificationPath(string root, string installation) => Path.Combine(root, "Notifications", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(UpdatePaths.Canonical(installation).ToUpperInvariant()))) + ".json");
    public void Validate(UpdateRequest request)
    {
        if (!Guid.TryParseExact(request.Id, "N", out _) || request.InstallDirectory != UpdatePaths.Canonical(request.InstallDirectory)) throw new InvalidDataException("Некорректный запрос updater.");
        var data = Directory.GetParent(UpdatesRoot)!.FullName;
        if (request.InstallDirectory == UpdatePaths.Canonical(Path.GetPathRoot(request.InstallDirectory)!) || UpdatePaths.Inside(data, request.InstallDirectory) || UpdatePaths.Inside(request.InstallDirectory, data) || request.InstallDirectory.Equals(UpdatePaths.Canonical(data), StringComparison.OrdinalIgnoreCase)) throw new IOException("Installation directory пересекается с пользовательскими данными.");
        if (!ReleaseClient.IsPortableAsset(request.Release.Asset) || !request.Release.CanInstall || !ReleaseClient.IsNewer(request.Release, request.OldVersion) || request.Release.Tag != "v" + request.Release.Version) throw new InvalidDataException("Недоверенная версия обновления.");
        var expectedZip = Path.Combine(UpdatesRoot, request.Release.Tag, request.Release.Asset);
        if (!UpdatePaths.Canonical(request.ZipPath).Equals(UpdatePaths.Canonical(expectedZip), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP находится вне папки обновлений.");
        if (request.OldVersion != "v" + SemanticVersion.Parse(request.OldVersion)) throw new InvalidDataException("Некорректная исходная версия.");
        foreach (var path in new[] { UpdatesRoot, request.InstallDirectory, request.ZipPath, StagePath(request), PreviousPath(request), BackupPath(request), JournalPath(request) }) UpdatePaths.NoLinks(path);
    }
    public UpdateRequest ReadRequest(string path)
    {
        UpdatePaths.NoLinks(path);
        if (!UpdatePaths.Inside(path, Path.Combine(UpdatesRoot, "Transactions")) || new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Недопустимый журнал обновления.");
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Журнал повреждён.");
        Validate(journal.Request);
        if (!UpdatePaths.Canonical(path).Equals(UpdatePaths.Canonical(JournalPath(journal.Request)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Имя журнала не совпадает.");
        return journal.Request;
    }
    public void Save(UpdateRequest request, InstallPhase phase) => UpdatePaths.WriteJson(JournalPath(request), new UpdateJournal(request, phase));
    public static bool IsOperationBlocked(bool applicationBusy, bool modelDownloadBusy, bool updateBusy) => applicationBusy || modelDownloadBusy || updateBusy;
    public static void CheckFreeSpace(string installation, string updatesRoot, string zip, CancellationToken ct)
    {
        long oldBytes = 0, expanded = 0;
        void Measure(string directory)
        {
            UpdatePaths.NoLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested(); UpdatePaths.NoLinks(path);
                if (Directory.Exists(path)) Measure(path); else oldBytes = checked(oldBytes + new FileInfo(path).Length);
            }
        }
        Measure(installation);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip)) foreach (var entry in archive.Entries) expanded = checked(expanded + entry.Length);
        if (expanded > UpdatePackage.MaximumExpandedBytes) throw new InvalidDataException("ZIP слишком велик.");
        var required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, long bytes) { var drive = Path.GetPathRoot(Path.GetFullPath(path))!; required[drive] = checked(required.GetValueOrDefault(drive) + bytes); }
        Add(installation, oldBytes + expanded); // Staged copy; original is renamed, not copied again.
        Add(updatesRoot, oldBytes * 2 + expanded * 2); // Backup, isolated runner, preflight and extraction.
        foreach (var drive in required) if (new DriveInfo(drive.Key).AvailableFreeSpace < drive.Value + 64L * 1024 * 1024) throw new IOException("Недостаточно свободного места для обновления и резервной копии.");
    }
    public static void ProbeInstallDirectory(string installDirectory)
    {
        UpdatePaths.NoLinks(installDirectory);
        if (!File.Exists(Path.Combine(installDirectory, "GameLocalizer.exe"))) throw new IOException("Папка приложения не найдена.");
        var parent = Directory.GetParent(installDirectory) ?? throw new IOException("Нельзя обновить корень диска.");
        var probe = Path.Combine(parent.FullName, ".gl-write-test-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(probe); File.WriteAllText(Path.Combine(probe, "test"), "test"); File.Delete(Path.Combine(probe, "test")); }
        finally { if (Directory.Exists(probe)) Directory.Delete(probe); }
        if (File.GetAttributes(installDirectory).HasFlag(FileAttributes.ReadOnly)) throw new UnauthorizedAccessException("Папка приложения доступна только для чтения.");
    }
    public async Task InstallAsync(UpdateRequest request, CancellationToken ct)
    {
        Validate(request); ProbeInstallDirectory(request.InstallDirectory);
        CheckFreeSpace(request.InstallDirectory, UpdatesRoot, request.ZipPath, ct);
        Directory.CreateDirectory(Path.Combine(UpdatesRoot, "Locks"));
        var lockPath = Path.Combine(UpdatesRoot, "Locks", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.InstallDirectory.ToUpperInvariant()))) + ".lock");
        UpdatePaths.NoLinks(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await processes.WaitForParentAsync(request, ct);
        var previous = PreviousPath(request); var stage = StagePath(request); var payload = Path.Combine(UpdatesRoot, "Payload", request.Id);
        if (Directory.Exists(previous) || Directory.Exists(stage)) throw new IOException("Найдена незавершённая транзакция. Выполните восстановление.");
        Save(request, InstallPhase.Prepared);
        try
        {
            await UpdatePackage.VerifyHashAsync(request.ZipPath, request.Release.Sha256!, ct);
            var manifest = UpdatePackage.Extract(request.ZipPath, payload, request.Release.Version.ToString(), ct, request.Release.Sha256);
            UpdatePaths.CopyTree(request.InstallDirectory, BackupPath(request), ct);
            Save(request, InstallPhase.BackedUp);
            UpdatePaths.CopyTree(request.InstallDirectory, stage, ct); // Unknown portable files retain their bytes.
            RemoveUnchangedObsoleteFiles(stage, manifest, ct);
            UpdatePaths.CopyTree(payload, stage, ct);
            foreach (var file in UpdatePackage.RequiredFor(manifest)) if (!File.Exists(UpdatePaths.RelativeFile(stage, file))) throw new InvalidDataException("Обязательный runtime-файл отсутствует.");
            ct.ThrowIfCancellationRequested();
            // From this point cancellation cannot interrupt recovery.
            Save(request, InstallPhase.MovingOld); Directory.Move(request.InstallDirectory, previous);
            Save(request, InstallPhase.OldMoved); Directory.Move(stage, request.InstallDirectory);
            Save(request, InstallPhase.NewMoved);
            UpdatePaths.WriteJson(NotificationPath(UpdatesRoot, request.InstallDirectory), new UpdateNotification(request.Release.Tag, request.Release.Notes, request.InstallDirectory));
            Save(request, InstallPhase.Starting);
            if (!await processes.StartAndConfirmAsync(request.InstallDirectory, request.Id, CancellationToken.None)) throw new IOException("Новая версия не подтвердила запуск.");
            Save(request, InstallPhase.Complete);
            // Keep both the external backup and previous complete directory for recoverability.
        }
        catch
        {
            Recover(request);
            try { processes.StartPrevious(request.InstallDirectory); } catch { /* Original bytes remain recoverable even if launch is denied. */ }
            throw;
        }
    }
    private static void RemoveUnchangedObsoleteFiles(string stage, PackageManifest next, CancellationToken ct)
    {
        var oldPath = GameLocalizer.Core.Models.DistributionPaths.Metadata(stage, UpdatePackage.LegacyManifestName);
        if (!File.Exists(oldPath)) return;
        UpdatePaths.NoLinks(oldPath);
        if (new FileInfo(oldPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Манифест установленной версии слишком велик.");
        var old = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(oldPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (old?.Files == null) throw new InvalidDataException("Манифест установленной версии повреждён.");
        var keep = next.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in old.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = UpdatePaths.RelativeFile(stage, file.Path);
            if (keep.Contains(file.Path) || !File.Exists(path) || new FileInfo(path).Length != file.Size) continue;
            using (var input = File.OpenRead(path))
                if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(path);
            var parent = Path.GetDirectoryName(path)!;
            while (!parent.Equals(stage, StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                UpdatePaths.NoLinks(parent); Directory.Delete(parent);
                parent = Path.GetDirectoryName(parent)!;
            }
        }
        if (next.Files.Any(f => f.Path == "app/GameLocalizer.dll") && Path.GetDirectoryName(oldPath) == stage) File.Delete(oldPath);
    }
    public void Recover(UpdateRequest request)
    {
        Validate(request); var previous = PreviousPath(request);
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(JournalPath(request))) ?? throw new InvalidDataException("Журнал повреждён.");
        if (journal.Phase is InstallPhase.Complete or InstallPhase.RolledBack) return;
        if (Directory.Exists(previous))
        {
            // Do not delete a failed installation: move it aside for diagnosis/retry.
            if (Directory.Exists(request.InstallDirectory)) Directory.Move(request.InstallDirectory, request.InstallDirectory + ".gl-failed-" + request.Id);
            Directory.Move(previous, request.InstallDirectory);
        }
        else if (!Directory.Exists(request.InstallDirectory))
        {
            if (!Directory.Exists(BackupPath(request))) throw new IOException("Резервная копия не найдена; автоматическое восстановление невозможно.");
            UpdatePaths.CopyTree(BackupPath(request), request.InstallDirectory, CancellationToken.None);
        }
        var notice = NotificationPath(UpdatesRoot, request.InstallDirectory); UpdatePaths.NoLinks(notice);
        if (File.Exists(notice)) File.Delete(notice);
        Save(request, InstallPhase.RolledBack);
    }
    public static string FriendlyError(Exception error) => error switch
    {
        UnauthorizedAccessException => "Недостаточно прав. Распакуйте ZIP вручную в доступную папку; elevation автоматически не запрашивается.",
        OperationCanceledException => "Обновление отменено.",
        HttpRequestException => "Не удалось скачать обновление. Проверьте соединение и повторите попытку.",
        IOException when (error.HResult & 0xffff) is 112 or 39 => "Недостаточно свободного места для обновления и резервной копии.",
        IOException when (error.HResult & 0xffff) is 32 or 33 => "Файл занят. Закройте другие экземпляры GameLocalizer и повторите попытку.",
        _ => error.Message
    };
}

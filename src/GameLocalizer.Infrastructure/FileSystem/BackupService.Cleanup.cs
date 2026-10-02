using System.Text.Json;
namespace GameLocalizer.Infrastructure.FileSystem;

public sealed record CleanupConflict(string Path, string Message, bool CanForceDelete);
public sealed record CleanupPlan(IReadOnlyList<string> FilesToRestore, IReadOnlyList<string> FilesToDelete,
    IReadOnlyList<string> DirectoriesToDelete, IReadOnlyList<CleanupConflict> Conflicts, IReadOnlyList<string> SkippedFiles);
public sealed record CleanupResult(int Restored, int Deleted, int DirectoriesDeleted, int Skipped, bool BackupRemoved);

public sealed partial class BackupService
{
    private static IEnumerable<string> BackupObjects(Record record)
    {
        yield return record.ObjectName;
        foreach (var previous in record.History)
            foreach (var name in BackupObjects(previous)) yield return name;
    }
    private async Task SaveManifest(string dir, Manifest manifest, CancellationToken ct)
    {
        manifest.Revision++; manifest.LastInteraction = DateTimeOffset.UtcNow;
        await SaveRegistry(manifest, ct);
        await AtomicWrite(Path.Combine(dir, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    public bool IsCleanupInterrupted(string root)
    {
        var dir = Path.Combine(Path.GetFullPath(root), "GameLocalizer_Backup");
        if (ReadRegistry(root)?.CleanupState != null) return true;
        if (!Directory.Exists(dir)) return false;
        BackupDirectory(root);
        if (File.Exists(Path.Combine(dir, "cleanup-retirement.json"))) return true;
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path)) return false;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) ?? throw new IOException("Invalid manifest");
        if (!string.Equals(manifest.Root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Backup belongs to another game directory");
        return manifest.CleanupState != null;
    }

    public async Task<CleanupPlan> PlanCleanupAsync(string root, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetFullPath(root), "GameLocalizer_Backup");
        if (!Directory.Exists(dir)) return new([], [], [], [], []);
        BackupDirectory(root);
        await using var gate = new FileStream(Path.Combine(dir, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        return await Plan(root, dir, await Load(root, dir, ct), ct);
    }

    private static async Task<CleanupPlan> Plan(string root, string dir, Manifest manifest, CancellationToken ct)
    {
        var restore = new List<string>(); var delete = new List<string>(); var conflicts = new List<CleanupConflict>();
        foreach (var record in manifest.Files)
        {
            var path = Resolve(root, record.RelativePath);
            await ReadOriginal(dir, record, ct);
            if (File.Exists(path) && !record.KnownHashes.Contains(TextFiles.Hash(await File.ReadAllBytesAsync(path, ct))))
                conflicts.Add(new(record.RelativePath, "Файл игры изменён извне; восстановление остановлено.", false));
            restore.Add(record.RelativePath);
        }
        if (!manifest.CreatedByGameLocalizer && (manifest.CreatedFiles.Count > 0 || manifest.CreatedDirectories.Count > 0)) throw new IOException("Unproven ownership");
        foreach (var record in manifest.CreatedFiles)
        {
            var path = Resolve(root, record.RelativePath);
            if (!File.Exists(path)) continue;
            if (!record.CreatedByGameLocalizer)
            {
                conflicts.Add(new(record.RelativePath, "Создание файла не подтверждено журналом; файл сохранён.", false));
                continue;
            }
            delete.Add(record.RelativePath);
            if (TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != record.AppliedHash)
                conflicts.Add(new(record.RelativePath, "Файл был изменён после создания GameLocalizer.", true));
        }
        var dirs = manifest.CreatedDirectories.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length).ToList();
        var removable = new HashSet<string>(delete.Except(conflicts.Select(c => c.Path)).Select(p => Resolve(root, p)), StringComparer.OrdinalIgnoreCase);
        var empty = new List<string>();
        foreach (var relative in dirs)
        {
            var path = ResolveOwned(root, relative, false);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).All(p => removable.Contains(p)))
            { empty.Add(relative); removable.Add(path); }
        }
        return new(restore, delete, empty, conflicts, conflicts.Where(c => c.CanForceDelete).Select(c => c.Path).ToArray());
    }

    public async Task<CleanupResult> CleanupAsync(string root, IReadOnlyDictionary<string, string>? forceDeleteHashes, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetFullPath(root), "GameLocalizer_Backup");
        if (!Directory.Exists(dir)) return new(0, 0, 0, 0, true);
        BackupDirectory(root);
        var restored = 0; var deleted = 0; var directories = 0; var skipped = 0;
        await using (var gate = new FileStream(Path.Combine(dir, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
        {
            var manifest = await Load(root, dir, ct);
            if (manifest.CleanupState == "Finalizing" || (!File.Exists(Path.Combine(dir, "manifest.json")) && File.Exists(Path.Combine(dir, "cleanup-retirement.json"))))
            {
                await RetireBackup(dir, ct);
            }
            else
            {
                var plan = await Plan(root, dir, manifest, ct);
                if (plan.Conflicts.Any(c => !c.CanForceDelete)) throw new IOException(string.Join("\n", plan.Conflicts.Where(c => !c.CanForceDelete).Select(c => c.Path + ": " + c.Message)));
                manifest.CleanupState = "CleanupInterrupted";
                await SaveManifest(dir, manifest, ct);
                foreach (var record in manifest.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Resolve(root, record.RelativePath);
                    if (File.Exists(path) && !record.KnownHashes.Contains(TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)))) throw new IOException("File changed during cleanup");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await AtomicWrite(path, await ReadOriginal(dir, record, ct), ct);
                    if (TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != record.OriginalHash) throw new IOException("Restore verification failed");
                    restored++;
                }
                foreach (var record in manifest.CreatedFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Resolve(root, record.RelativePath);
                    if (!File.Exists(path)) continue;
                    var current = TextFiles.Hash(await File.ReadAllBytesAsync(path, ct));
                    if (current != record.AppliedHash && (forceDeleteHashes == null || !forceDeleteHashes.TryGetValue(record.RelativePath, out var approvedHash) || approvedHash != current)) { skipped++; continue; }
                    Resolve(root, record.RelativePath);
                    if (TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != current) throw new IOException("File changed before deletion");
                    File.Delete(path); deleted++;
                }
                foreach (var relative in manifest.CreatedDirectories.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length))
                {
                    ct.ThrowIfCancellationRequested();
                    var path = ResolveOwned(root, relative, false);
                    if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) { Directory.Delete(path); directories++; }
                }
                foreach (var record in manifest.Files)
                    if (TextFiles.Hash(await File.ReadAllBytesAsync(Resolve(root, record.RelativePath), ct)) != record.OriginalHash) throw new IOException("Final restore validation failed");
                if (skipped > 0)
                {
                    manifest.CreatedFiles.RemoveAll(f => !File.Exists(Resolve(root, f.RelativePath)));
                    manifest.CreatedDirectories.RemoveAll(d => !Directory.Exists(ResolveOwned(root, d, false)));
                    manifest.CleanupState = null; manifest.LastOperation = "CleanupPartial";
                    await SaveManifest(dir, manifest, ct);
                    return new(restored, deleted, directories, skipped, false);
                }
                // Persist finalization so a crash while retiring backup objects can be resumed without needing deleted originals.
                manifest.Files.Clear(); manifest.CreatedFiles.Clear(); manifest.CreatedDirectories.Clear();
                manifest.CleanupState = "Finalizing";
                // Originals are individually recorded for retirement; never recursively delete the backup directory.
                var retirement = Path.Combine(dir, "cleanup-retirement.json");
                if (!File.Exists(retirement))
                    await AtomicWrite(retirement, JsonSerializer.SerializeToUtf8Bytes(plan.FilesToRestore.Count == 0 ? Array.Empty<string>() :
                        (await Load(root, dir, ct)).Files.SelectMany(BackupObjects).ToArray()), ct);
                await SaveManifest(dir, manifest, ct);
                await RetireBackup(dir, ct);
            }
        }
        var lockPath = Path.Combine(dir, "operation.lock");
        if (File.Exists(lockPath))
        {
            if (File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe lock");
            File.Delete(lockPath);
        }
        var removed = !Directory.EnumerateFileSystemEntries(dir).Any();
        if (removed) Directory.Delete(dir);
        var registry = RegistryPath(root);
        if (File.Exists(registry)) File.Delete(registry);
        return new(restored, deleted, directories, skipped, removed);
    }

    private static async Task RetireBackup(string dir, CancellationToken ct)
    {
        var retirement = Path.Combine(dir, "cleanup-retirement.json");
        if (File.GetAttributes(retirement).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe retirement journal");
        var objects = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(retirement, ct)) ?? throw new IOException("Invalid retirement journal");
        foreach (var name in objects)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetFileName(name) != name || !name.EndsWith(".original", StringComparison.Ordinal) || !Guid.TryParseExact(name[..^9], "N", out _)) throw new IOException("Unsafe backup retirement path");
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe backup object");
            File.Delete(path);
        }
        // Commit completion only after all owned backup objects have been retired.
        File.Delete(Path.Combine(dir, "manifest.json"));
        File.Delete(retirement);
    }
}

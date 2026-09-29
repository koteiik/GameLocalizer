using System.Text.Json;
using GameLocalizer.Core.Models;
using Microsoft.Extensions.Logging;
namespace GameLocalizer.Infrastructure.FileSystem;

public sealed class BackupService(ILogger<BackupService> logger)
{
    private sealed record Manifest(string Root, List<Record> Files);
    private sealed record Record(string RelativePath, string OriginalHash, string ObjectName, List<string> KnownHashes, DateTimeOffset CreatedAt);
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".json", ".xml", ".csv", ".tsv", ".ini", ".po", ".lang", ".locale", ".loc", ".strings" };
    public static string Resolve(string root, string relative)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is ".." or "GameLocalizer_Backup")) throw new IOException("Unsafe relative path");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes game directory");
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Symbolic links/junctions are not writable");
        if (!Allowed.Contains(Path.GetExtension(path))) throw new IOException("Unsupported writable format");
        return path;
    }
    private static string BackupDirectory(string root)
    {
        var dir = Path.Combine(Path.GetFullPath(root), "GameLocalizer_Backup");
        for (string? p = dir; p != null; p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe backup directory");
        Directory.CreateDirectory(dir); return dir;
    }
    private static async Task<Manifest> Load(string root, string dir, CancellationToken ct)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe manifest");
        var manifest = File.Exists(path) ? JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct)) ?? throw new IOException("Invalid manifest") : new Manifest(Path.GetFullPath(root), []);
        if (!string.Equals(manifest.Root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Backup belongs to another game directory");
        return manifest;
    }
    private static async Task AtomicWrite(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true); }
            if (TextFiles.Hash(await File.ReadAllBytesAsync(temporary, ct)) != TextFiles.Hash(bytes)) throw new IOException("Write verification failed");
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task ApplyAsync(string root, IReadOnlyList<FileChange> changes, CancellationToken ct)
    {
        var dir = BackupDirectory(root);
        await using var gate = new FileStream(Path.Combine(dir, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifest = await Load(root, dir, ct);
        if (changes.Select(c => c.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count) throw new IOException("Duplicate paths");
        // Preflight every file before making any game changes.
        foreach (var change in changes)
        {
            var path = Resolve(root, change.RelativePath);
            if (new FileInfo(path).Length > TextFiles.MaxBytes || change.Content.Length > TextFiles.MaxBytes) throw new IOException("File size limit exceeded");
            if (TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != change.ExpectedHash) throw new IOException("Файл изменился после анализа: " + change.RelativePath);
        }
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested(); var path = Resolve(root, change.RelativePath);
            var original = await File.ReadAllBytesAsync(path, ct);
            if (TextFiles.Hash(original) != change.ExpectedHash) throw new IOException("File changed during operation");
            var record = manifest.Files.SingleOrDefault(r => r.RelativePath.Equals(change.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (record is null)
            {
                var objectName = Guid.NewGuid().ToString("N") + ".original";
                await AtomicWrite(Path.Combine(dir, objectName), original, ct);
                record = new(change.RelativePath, change.ExpectedHash, objectName, [change.ExpectedHash], DateTimeOffset.UtcNow);
                manifest.Files.Add(record);
            }
            else if (!record.KnownHashes.Contains(change.ExpectedHash)) throw new IOException("Игра изменена извне; сохранённый оригинал относится к другой версии.");
            await ReadOriginal(dir, record, ct); // Never proceed with a missing or corrupted original.
            var hash = TextFiles.Hash(change.Content); if (!record.KnownHashes.Contains(hash)) record.KnownHashes.Add(hash);
            // Durable recovery journal is committed before replacing the game file.
            await AtomicWrite(Path.Combine(dir, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
            if (TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != change.ExpectedHash) throw new IOException("File changed before replacement");
            await AtomicWrite(path, change.Content, ct);
            logger.LogInformation("Backup and apply: {File}", change.RelativePath);
        }
    }
    private static async Task<byte[]> ReadOriginal(string dir, Record record, CancellationToken ct)
    {
        if (Path.GetFileName(record.ObjectName) != record.ObjectName) throw new IOException("Invalid backup object path");
        var path = Path.Combine(dir, record.ObjectName);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > TextFiles.MaxBytes) throw new IOException("Unsafe backup object");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (TextFiles.Hash(bytes) != record.OriginalHash) throw new IOException("Backup SHA256 mismatch");
        return bytes;
    }
    public async Task RestoreAsync(string root, CancellationToken ct)
    {
        var dir = BackupDirectory(root);
        await using var gate = new FileStream(Path.Combine(dir, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var manifest = await Load(root, dir, ct);
        if (manifest.Files.Count == 0) throw new IOException("Резервные копии не найдены");
        foreach (var record in manifest.Files)
        {
            var path = Resolve(root, record.RelativePath); await ReadOriginal(dir, record, ct);
            if (File.Exists(path) && (new FileInfo(path).Length > TextFiles.MaxBytes || !record.KnownHashes.Contains(TextFiles.Hash(await File.ReadAllBytesAsync(path, ct))))) throw new IOException("Файл изменён извне, восстановление остановлено: " + record.RelativePath);
        }
        foreach (var record in manifest.Files)
        {
            var path = Resolve(root, record.RelativePath);
            if (File.Exists(path) && !record.KnownHashes.Contains(TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)))) throw new IOException("File changed during restore");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await AtomicWrite(path, await ReadOriginal(dir, record, ct), ct);
            logger.LogInformation("Restored {File}", record.RelativePath);
        }
    }
}

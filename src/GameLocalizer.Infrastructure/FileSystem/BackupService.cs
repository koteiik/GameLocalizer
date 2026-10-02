using System.Text.Json;
using GameLocalizer.Core.Models;
using Microsoft.Extensions.Logging;
namespace GameLocalizer.Infrastructure.FileSystem;

public sealed partial class BackupService(ILogger<BackupService> logger, string? stateDirectory = null)
{
    private sealed record Manifest(string Root, List<Record> Files)
    {
        public int FormatVersion { get; init; } = 2;
        public long Revision { get; set; }
        public string? GameId { get; set; }
        public DateTimeOffset LastInteraction { get; set; }
        public string LastOperation { get; set; } = "Apply";
        public List<Record> ModifiedFiles => Files; // Files retained for compatibility with earlier manifests.
        public bool CreatedByGameLocalizer { get; init; }
        public List<CreatedFile> CreatedFiles { get; init; } = [];
        public List<string> CreatedDirectories { get; init; } = [];
        public string? CleanupState { get; set; }
    }
    private sealed record CreatedFile(string RelativePath, string AppliedHash)
    {
        public bool CreatedByGameLocalizer { get; init; }
    }
    private sealed record Record(string RelativePath, string OriginalHash, string ObjectName, List<string> KnownHashes, DateTimeOffset CreatedAt)
    {
        public string? AppliedHash { get; set; }
        public string AdapterType { get; set; } = "";
        public LocalizationApplyMode ApplyMode { get; set; } = LocalizationApplyMode.CompatibleReplacement;
        public string LocalizationSlot { get; set; } = "";
        public string Encoding { get; set; } = "";
        public string Bom { get; set; } = "";
        public string LineEndings { get; set; } = "";
        public string BackupPath => "GameLocalizer_Backup/" + ObjectName;
        public List<Record> History { get; init; } = [];
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateDirectoryExclusive(string path, IntPtr securityAttributes);
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".json", ".xml", ".csv", ".tsv", ".ini", ".po", ".lang", ".locale", ".loc", ".strings" };
    public static string Resolve(string root, string relative)
        => ResolveOwned(root, relative, true);
    private static string ResolveOwned(string root, string relative, bool file)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || Path.IsPathRooted(relative) || relative.Split('\\', '/').Any(p => p is ".." or "." || p.Equals("GameLocalizer_Backup", StringComparison.OrdinalIgnoreCase))) throw new IOException("Unsafe relative path");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes game directory");
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Symbolic links/junctions are not writable");
        if (file && !Allowed.Contains(Path.GetExtension(path))) throw new IOException("Unsupported writable format");
        return path;
    }
    private static string BackupDirectory(string root)
    {
        var dir = Path.Combine(Path.GetFullPath(root), "GameLocalizer_Backup");
        for (string? p = dir; p != null; p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe backup directory");
        if (Directory.Exists(dir) && !File.Exists(Path.Combine(dir, "manifest.json")) && !File.Exists(Path.Combine(dir, "cleanup-retirement.json")) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new IOException("Existing backup directory has no ownership manifest");
        foreach (var name in new[] { "operation.lock", "manifest.json", "cleanup-retirement.json" })
        {
            var control = Path.Combine(dir, name);
            if ((File.Exists(control) || Directory.Exists(control)) && File.GetAttributes(control).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe backup control file");
        }
        Directory.CreateDirectory(dir); return dir;
    }
    private async Task<Manifest> Load(string root, string dir, CancellationToken ct)
    {
        var path = Path.Combine(dir, "manifest.json");
        var stored = ReadRegistry(root);
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe manifest");
        var manifest = File.Exists(path) ? JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct)) ?? throw new IOException("Invalid manifest") : new Manifest(Path.GetFullPath(root), []) { CreatedByGameLocalizer = true };
        ValidateManifest(root, manifest);
        if (stored != null && stored.Revision > manifest.Revision) manifest = stored;
        if (!string.Equals(NormalizeRoot(manifest.Root), NormalizeRoot(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Backup belongs to another game directory");
        return manifest;
    }
    private static async Task AtomicWrite(string path, byte[] bytes, CancellationToken ct, bool createOnly = false)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await output.WriteAsync(bytes, ct); await output.FlushAsync(ct); output.Flush(true); }
            if (TextFiles.Hash(await File.ReadAllBytesAsync(temporary, ct)) != TextFiles.Hash(bytes)) throw new IOException("Write verification failed");
            ct.ThrowIfCancellationRequested();
            if (!createOnly && File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task ApplyCoreAsync(string root, IReadOnlyList<FileChange> changes, CancellationToken ct)
    {
        var dir = BackupDirectory(root);
        await using var gate = new FileStream(Path.Combine(dir, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        var manifest = await Load(root, dir, ct);
        manifest.LastOperation = "Apply";
        if (manifest.CleanupState != null) throw new IOException("CleanupInterrupted: сначала продолжите очистку.");
        changes = changes.Select(c => c with { RelativePath = Path.GetRelativePath(root, Resolve(root, c.RelativePath)) }).ToArray();
        if (changes.Select(c => c.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count) throw new IOException("Duplicate paths");
        // Preflight every file before making any game changes.
        foreach (var change in changes)
        {
            var path = Resolve(root, change.RelativePath);
            if (!File.Exists(path))
            {
                if (change.ExpectedHash != "" || Directory.Exists(path)) throw new IOException("Expected existing file: " + change.RelativePath);
                if (change.Content.Length > TextFiles.MaxBytes) throw new IOException("File size limit exceeded");
                continue;
            }
            if (new FileInfo(path).Length > TextFiles.MaxBytes || change.Content.Length > TextFiles.MaxBytes) throw new IOException("File size limit exceeded");
            if (TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != change.ExpectedHash) throw new IOException("Файл изменился после анализа: " + change.RelativePath);
        }
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested(); var path = Resolve(root, change.RelativePath);
            var created = manifest.CreatedFiles.SingleOrDefault(f => Resolve(root, f.RelativePath).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (!File.Exists(path) || created != null)
            {
                if (!manifest.CreatedByGameLocalizer) throw new IOException("Legacy backup cannot acquire new ownership; restore and clean it first.");
                if (File.Exists(path) && (created == null || !created.CreatedByGameLocalizer || TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != created.AppliedHash)) throw new IOException("Created file changed externally");
                if (!File.Exists(path) && change.ExpectedHash != "") throw new IOException("File disappeared during apply");
                // Confirm exclusive creation before recording directory ownership.
                var missing = new Stack<string>();
                for (var parent = Path.GetDirectoryName(path)!; !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)!) missing.Push(parent);
                foreach (var parent in missing)
                {
                    ResolveOwned(root, Path.GetRelativePath(root, parent), false);
                    if (!CreateDirectoryExclusive(parent, IntPtr.Zero)) throw new IOException("Directory appeared during apply: " + parent);
                    manifest.CreatedDirectories.Add(Path.GetRelativePath(root, parent));
                    await SaveManifest(dir, manifest, ct);
                }
                if (created != null) manifest.CreatedFiles.Remove(created);
                manifest.CreatedFiles.Add(new(change.RelativePath, TextFiles.Hash(change.Content)) { CreatedByGameLocalizer = created?.CreatedByGameLocalizer == true });
                await SaveManifest(dir, manifest, ct);
                if (created == null && File.Exists(path)) throw new IOException("File appeared during apply");
                await AtomicWrite(path, change.Content, ct, created == null || !File.Exists(path));
                var pending = manifest.CreatedFiles.Single(f => f.RelativePath == change.RelativePath);
                manifest.CreatedFiles.Remove(pending);
                manifest.CreatedFiles.Add(pending with { CreatedByGameLocalizer = true });
                await SaveManifest(dir, manifest, ct);
                continue;
            }
            var original = await File.ReadAllBytesAsync(path, ct);
            if (TextFiles.Hash(original) != change.ExpectedHash) throw new IOException("File changed during operation");
            var record = manifest.Files.SingleOrDefault(r => Resolve(root, r.RelativePath).Equals(path, StringComparison.OrdinalIgnoreCase));
            if (record is null)
            {
                var objectName = Guid.NewGuid().ToString("N") + ".original";
                await AtomicWrite(Path.Combine(dir, objectName), original, ct);
                record = new(change.RelativePath, change.ExpectedHash, objectName, [change.ExpectedHash], DateTimeOffset.UtcNow);
                manifest.Files.Add(record);
            }
            else if (!record.KnownHashes.Contains(change.ExpectedHash))
            {
                // A fresh scan confirmed this version. Retire the previous original; never restore it over a game update.
                await ReadOriginal(dir, record, ct);
                var objectName = Guid.NewGuid().ToString("N") + ".original";
                await AtomicWrite(Path.Combine(dir, objectName), original, ct);
                var replacement = new Record(change.RelativePath, change.ExpectedHash, objectName, [change.ExpectedHash], DateTimeOffset.UtcNow);
                replacement.History.Add(record);
                manifest.Files.Remove(record); manifest.Files.Add(replacement); record = replacement;
            }
            var snapshot = await TextFiles.ReadAsync(path, ct);
            record.AdapterType = change.AdapterType; record.ApplyMode = change.ApplyMode; record.LocalizationSlot = change.LocalizationSlot;
            record.Encoding = snapshot.Encoding.WebName; record.Bom = Convert.ToHexString(snapshot.Preamble);
            record.LineEndings = snapshot.Text.Contains("\r\n") ? (snapshot.Text.Replace("\r\n", "").Contains('\n') ? "Mixed" : "CRLF") : snapshot.Text.Contains('\n') ? "LF" : snapshot.Text.Contains('\r') ? "CR" : "None";
            await ReadOriginal(dir, record, ct); // Never proceed with a missing or corrupted original.
            var hash = TextFiles.Hash(change.Content); if (!record.KnownHashes.Contains(hash)) record.KnownHashes.Add(hash);
            record.AppliedHash = hash;
            // Durable recovery journal is committed before replacing the game file.
            await SaveManifest(dir, manifest, ct);
            if (TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != change.ExpectedHash) throw new IOException("File changed before replacement");
            await AtomicWrite(path, change.Content, ct);
            if (TextFiles.Hash(await File.ReadAllBytesAsync(path, ct)) != hash) throw new IOException("Applied SHA256 mismatch");
            logger.LogInformation("Backup and apply: {File}", change.RelativePath);
        }
    }
    private static async Task<byte[]> ReadOriginal(string dir, Record record, CancellationToken ct)
    {
        if (Path.GetFileName(record.ObjectName) != record.ObjectName || !record.ObjectName.EndsWith(".original", StringComparison.Ordinal) || !Guid.TryParseExact(record.ObjectName[..^9], "N", out _)) throw new IOException("Invalid backup object path");
        var path = Path.Combine(dir, record.ObjectName);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > TextFiles.MaxBytes) throw new IOException("Unsafe backup object");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (TextFiles.Hash(bytes) != record.OriginalHash) throw new IOException("Backup SHA256 mismatch");
        return bytes;
    }
    public async Task RestoreAsync(string root, CancellationToken ct)
    {
        var dir = BackupDirectory(root);
        await using var gate = new FileStream(Path.Combine(dir, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        var manifest = await Load(root, dir, ct);
        if (manifest.Files.Count == 0 && manifest.CreatedFiles.Count == 0) throw new IOException("Резервные копии не найдены");
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
        manifest.LastOperation = "Restore";
        await SaveManifest(dir, manifest, ct);
    }
}

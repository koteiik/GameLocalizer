using System.Text;
using System.Text.Json;
using GameLocalizer.Core.Models;

namespace GameLocalizer.Infrastructure.FileSystem;

public sealed partial class BackupService
{
    private static string NormalizeRoot(string root) => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    private string RegistryPath(string root)
    {
        var directory = Path.Combine(stateDirectory ?? ApplicationPaths.UserData, "GameManifests");
        for (string? p = directory; p != null; p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe registry directory");
        var path = Path.Combine(directory, TextFiles.Hash(Encoding.UTF8.GetBytes(NormalizeRoot(root))) + ".json");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Unsafe registry file");
        return path;
    }
    private Manifest? ReadRegistry(string root)
    {
        var path = RegistryPath(root);
        if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) ?? throw new IOException("Invalid registry");
        ValidateManifest(root, manifest);
        return manifest;
    }
    private async Task SaveRegistry(Manifest manifest, CancellationToken ct)
    {
        var path = RegistryPath(manifest.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(manifest), ct);
    }
    private static void ValidateManifest(string root, Manifest manifest)
    {
        if (NormalizeRoot(root) != NormalizeRoot(manifest.Root)) throw new IOException("Manifest root mismatch");
        foreach (var record in manifest.Files)
        {
            Resolve(root, record.RelativePath);
            foreach (var name in BackupObjects(record))
                if (Path.GetFileName(name) != name || !name.EndsWith(".original", StringComparison.Ordinal) || !Guid.TryParseExact(name[..^9], "N", out _)) throw new IOException("Unsafe backup object");
        }
        foreach (var file in manifest.CreatedFiles) Resolve(root, file.RelativePath);
        foreach (var directory in manifest.CreatedDirectories) ResolveOwned(root, directory, false);
    }
}

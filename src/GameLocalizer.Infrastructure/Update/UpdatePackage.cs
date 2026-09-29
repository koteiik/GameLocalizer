using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLocalizer.Infrastructure.Update;

public static class UpdatePaths
{
    public static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    public static bool Inside(string path, string root) => Canonical(path).StartsWith(Canonical(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void NoLinks(string path)
    {
        for (var p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((Directory.Exists(p) || File.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки и junction в путях обновления запрещены.");
    }
    public static string RelativeFile(string root, string relative)
    {
        if (relative.Length == 0 || relative.Contains('\\') || Path.IsPathRooted(relative)) throw new InvalidDataException("Недопустимый путь ZIP.");
        var parts = relative.Split('/');
        foreach (var part in parts)
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') ||
                System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new InvalidDataException("Небезопасный путь ZIP.");
            if (new[] { "Models", "Settings", "Glossary", "logs", "jobs", "Updates", "GameLocalizer_Backup", "memory.db", "settings.json", "glossary.json" }.Contains(part, StringComparer.OrdinalIgnoreCase) || part.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".spm", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Обновление содержит пользовательские данные или модель.");
        }
        var result = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!Inside(result, root)) throw new InvalidDataException("ZIP выходит за пределы директории.");
        NoLinks(result); return result;
    }
    public static void WriteJson<T>(string path, T value)
    {
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void CopyTree(string source, string destination, CancellationToken ct)
    {
        NoLinks(source); NoLinks(destination); Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested(); NoLinks(entry); var target = Path.Combine(destination, Path.GetFileName(entry)); NoLinks(target);
            if (Directory.Exists(entry)) CopyTree(entry, target, ct);
            else
            {
                using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
                var buffer = new byte[81920]; int read;
                while ((read = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                output.Flush(true);
            }
        }
    }
}
public sealed record PackageFile(string Path, long Size, string Sha256);
public sealed record PackageManifest(string Version, IReadOnlyList<PackageFile> Files);

public static class UpdatePackage
{
    public const string ManifestName = "update-manifest.json";
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public static readonly string[] RequiredFiles = ["GameLocalizer.exe", "GameLocalizer.dll", "GameLocalizer.deps.json", "GameLocalizer.runtimeconfig.json", "GameLocalizer.Core.dll", "GameLocalizer.Infrastructure.dll", "GameLocalizer.ModelHost.exe", "GameLocalizer.ModelHost.dll", "GameLocalizer.ModelHost.deps.json", "GameLocalizer.ModelHost.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll", "Updater/GameLocalizer.Updater.exe", "Updater/GameLocalizer.Updater.dll", "Updater/GameLocalizer.Updater.runtimeconfig.json", "Updater/GameLocalizer.Updater.deps.json", "Updater/coreclr.dll", "Updater/hostfxr.dll", "Updater/hostpolicy.dll"];
    public static async Task VerifyHashAsync(string zip, string expected, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(expected, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Отсутствует SHA256.");
        UpdatePaths.NoLinks(zip); await using var stream = File.OpenRead(zip);
        if (stream.Length > ReleaseClient.MaximumZipBytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
    }
    public static PackageManifest Extract(string zipPath, string destination, string version, CancellationToken ct, string? expectedSha256 = null)
    {
        UpdatePaths.NoLinks(zipPath); UpdatePaths.NoLinks(destination);
        if (Directory.Exists(destination)) throw new IOException("Временная папка уже существует.");
        using var archive = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Keep the same non-writable handle from digest verification through extraction.
        if (archive.Length > ReleaseClient.MaximumZipBytes) throw new InvalidDataException("ZIP слишком велик.");
        if (expectedSha256 != null)
        {
            if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
            archive.Position = 0;
        }
        ct.ThrowIfCancellationRequested();
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("Слишком много файлов ZIP.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.TrimEnd('/'); UpdatePaths.RelativeFile(destination, name);
            if (!paths.Add(name) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Дубликат или ссылка в ZIP.");
            total = checked(total + entry.Length); if (total > MaximumExpandedBytes) throw new InvalidDataException("ZIP слишком велик после распаковки.");
        }
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("ZIP не содержит манифест обновления.");
        if (manifestEntry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Манифест слишком велик.");
        PackageManifest manifest;
        using (var input = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<PackageManifest>(input, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Манифест повреждён.");
        if (manifest.Files == null || manifest.Version != version || SemanticVersion.Parse(version).Prerelease.Length != 0) throw new InvalidDataException("Версия ZIP не совпадает с релизом.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            UpdatePaths.RelativeFile(destination, file.Path);
            if (!listed.Add(file.Path) || file.Size < 0 || !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("Недопустимый манифест.");
        }
        if (RequiredFiles.Any(p => !listed.Contains(p)) || !listed.SetEquals(zip.Entries.Where(e => !e.FullName.EndsWith('/') && e.FullName != ManifestName).Select(e => e.FullName))) throw new InvalidDataException("В ZIP отсутствуют обязательные файлы или есть неучтённые файлы.");
        Directory.CreateDirectory(destination);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested(); var entry = zip.GetEntry(file.Path) ?? throw new InvalidDataException("Файл манифеста отсутствует.");
            if (entry.Length != file.Size) throw new InvalidDataException("Неверный размер файла ZIP.");
            var target = UpdatePaths.RelativeFile(destination, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; long written = 0; int read;
            while ((read = input.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); written += read; if (written > file.Size) throw new InvalidDataException("ZIP превышает заявленный размер."); output.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); }
            output.Flush(true);
            if (written != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма файла ZIP не совпадает.");
        }
        UpdatePaths.WriteJson(Path.Combine(destination, ManifestName), manifest); return manifest;
    }
}

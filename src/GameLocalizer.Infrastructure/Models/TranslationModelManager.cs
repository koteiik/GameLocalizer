using System.Security.Cryptography;
using System.Text.Json;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.Models;

public sealed class TranslationModelManager : ITranslationModelManager
{
    private static readonly HttpClient Downloads = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    private readonly TranslationModelInfo info;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string ModelDirectory { get; }
    public TranslationModelManager(string modelsRoot, HttpClient? client = null, TranslationModelInfo? manifest = null)
    {
        this.client = client ?? Downloads;
        using var stream = typeof(TranslationModelManager).Assembly.GetManifestResourceStream("GameLocalizer.Infrastructure.Models.opus-mt-en-ru.json")!;
        info = manifest ?? JsonSerializer.Deserialize<TranslationModelInfo>(stream)!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(info.Id, "^[a-zA-Z0-9-]+$") || !System.Text.RegularExpressions.Regex.IsMatch(info.Version, "^[a-zA-Z0-9.-]+$")) throw new InvalidDataException("Invalid model identity");
        ModelDirectory = Path.GetFullPath(Path.Combine(modelsRoot, info.Id + "-" + info.Version));
        foreach (var file in info.Files) _ = FilePath(file);
    }
    private string FilePath(ModelFile file)
    {
        var full = Path.GetFullPath(Path.Combine(ModelDirectory, file.Name));
        if (!full.StartsWith(ModelDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe model path");
        return full;
    }
    private string Receipt => Path.Combine(ModelDirectory, "installed-version.txt");
    public TranslationModelInfo GetModelInfo() => info;
    public bool IsInstalled
    {
        get { try { return File.Exists(Receipt) && File.ReadAllText(Receipt) == info.Version && info.Files.All(f => File.Exists(FilePath(f)) && new FileInfo(FilePath(f)).Length == f.Size); } catch (IOException) { return false; } }
    }
    public IReadOnlyList<TranslationModelInfo> GetInstalledModels() => IsInstalled ? [info] : [];
    private async Task<bool> VerifyFiles(CancellationToken ct)
    {
        foreach (var file in info.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = FilePath(file);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
            await using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    public async Task<bool> VerifyModelAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!IsInstalled) { return false; }
            return await VerifyFiles(ct);
        }
        finally { gate.Release(); }
    }
    public async Task DownloadModelAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(ModelDirectory); long done = 0;
            foreach (var file in info.Files)
            {
                ct.ThrowIfCancellationRequested();
                var destination = FilePath(file); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination) && new FileInfo(destination).Length == file.Size)
                {
                    await using var existing = File.OpenRead(destination);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) { done += file.Size; progress?.Report(new(file.Name, done, info.DownloadBytes)); continue; }
                }
                var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
                try
                {
                    using var response = await client.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long length = 0;
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920]; int read;
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        while ((read = await input.ReadAsync(buffer, ct)) != 0)
                        {
                            length += read; if (length > file.Size) throw new InvalidDataException("Unexpected model size");
                            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            if (clock.ElapsedMilliseconds >= 200) { progress?.Report(new(file.Name, done + length, info.DownloadBytes)); clock.Restart(); }
                        }
                        await output.FlushAsync(ct);
                    }
                    if (length != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Model checksum/size mismatch");
                    File.Move(temporary, destination, true); done += length; progress?.Report(new(file.Name, done, info.DownloadBytes));
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (!await VerifyFiles(ct)) throw new InvalidDataException("Model verification failed");
            await File.WriteAllTextAsync(Receipt, info.Version, ct);
        }
        finally { gate.Release(); }
    }
    public async Task DeleteModelAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            // Delete only manifest-owned files; no recursive removal or access outside the fixed model directory.
            if (File.Exists(Receipt)) File.Delete(Receipt);
            foreach (var file in info.Files) { ct.ThrowIfCancellationRequested(); var path = FilePath(file); if (File.Exists(path)) File.Delete(path); }
        }
        finally { gate.Release(); }
    }
}

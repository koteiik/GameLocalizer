using System.Security.Cryptography;
using System.Text;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.FileSystem;

public record TextFile(string Text, Encoding Encoding, byte[] Preamble, string Hash)
{
    public byte[] Encode(string text) => Preamble.Concat(Encoding.GetBytes(text)).ToArray();
}
public static class TextFiles
{
    public const long MaxBytes = 4 * 1024 * 1024;
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static async Task<TextFile> ReadAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > MaxBytes) throw new IOException("Файл превышает лимит 4 MiB");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct);
        Encoding enc = new UTF8Encoding(false, true); int skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { enc = new UTF32Encoding(false, false, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { enc = new UTF32Encoding(true, false, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { enc = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { enc = new UnicodeEncoding(true, false, true); skip = 2; }
        var text = enc.GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0')) throw new InvalidDataException("Binary content");
        return new(text, enc, bytes[..skip], Hash(bytes));
    }
}
public sealed class ResourceScanner(IEnumerable<ILocalizationAdapter> adapters)
{
    private readonly ResourceClassifier classifier = new();
    public IEnumerable<Resource> Enumerate(string root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ResourceClassifier.IsRuntimePath(root))
        {
            yield return new(root, "Directory", false, "Технический каталог: содержимое не сканируется", ResourceKind.EngineRuntime);
            yield break;
        }
        foreach (var path in SafeTree.Enumerate(root, ct, descend: p => !ResourceClassifier.IsRuntimePath(p)))
        {
            ct.ThrowIfCancellationRequested();
            // Preserve mod context even when a user selects BepInEx/config itself as the game root.
            var kind = classifier.Classify(ResourceClassifier.IsModPath(root) || Core.Localization.BepInExLocalizationAdapter.TranslationPath(path) ? path : Path.GetRelativePath(root, path));
            if (Directory.Exists(path))
            {
                if (kind == ResourceKind.EngineRuntime) yield return new(path, "Directory", false, "Технический каталог: содержимое не сканируется", kind);
                continue;
            }
            var adapter = adapters.FirstOrDefault(a => a.CanHandle(path));
            bool tooLarge;
            try { tooLarge = new FileInfo(path).Length > TextFiles.MaxBytes; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (ResourceClassifier.IsExtractable(kind) && Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.ChangeExtension(path, ".dll")) || File.Exists(Path.ChangeExtension(path, ".exe"))) kind = ResourceKind.TechnicalDocumentation;
                else if (!tooLarge)
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                        if (ResourceClassifier.IsDocumentationXml(reader, ct)) kind = ResourceKind.TechnicalDocumentation;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or DecoderFallbackException)
                    { kind = ResourceKind.Unknown; }
                }
            }
            var candidate = ResourceClassifier.IsExtractable(kind);
            yield return new(path, adapter?.Name ?? Path.GetExtension(path), candidate && adapter != null && !tooLarge,
                !candidate ? "Исключён по умолчанию" : tooLarge ? "Размер > 4 MiB" : adapter == null ? "Только обнаружение: нужен адаптер" : "Доступен для извлечения", kind);
        }
    }
    // Compatibility API for small callers; the application consumes the streaming pipeline.
    public Task<IReadOnlyList<Resource>> ScanAsync(string root, CancellationToken ct) => Task.Run<IReadOnlyList<Resource>>(() => Enumerate(root, ct).ToArray(), ct);
}

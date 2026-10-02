using System.Text.Json;
namespace GameLocalizer.Infrastructure.FileSystem;
public sealed class ApplyDiagnosticReportLocator(string? directory = null)
{
    private readonly string reports = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLocalizer", "Logs", "ApplyDiagnostics");
    public string? FindLatest(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(reports)) return null;
        try
        {
            var root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var file in Directory.EnumerateFiles(reports, "ApplyDiagnostic_*.json").OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(p => p, StringComparer.Ordinal))
            {
                try
                {
                    if (new FileInfo(file).Length > 64 * 1024 * 1024 || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                    using var stream = File.OpenRead(file); using var json = JsonDocument.Parse(stream);
                    if (!json.RootElement.TryGetProperty("Root", out var property) || property.ValueKind != JsonValueKind.String) continue;
                    var stored = Path.GetFullPath(property.GetString()!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var text = Path.ChangeExtension(file, ".txt");
                    if (root.Equals(stored, StringComparison.OrdinalIgnoreCase) && File.Exists(text)) return text;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }
}

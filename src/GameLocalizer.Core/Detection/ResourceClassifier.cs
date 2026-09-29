using GameLocalizer.Core.Models;
namespace GameLocalizer.Core.Detection;

public sealed class ResourceClassifier
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".json", ".xml", ".csv", ".tsv", ".ini", ".yaml", ".yml", ".po", ".lang", ".locale", ".loc", ".strings", ".rpy" };
    private static readonly HashSet<string> Priority = new(StringComparer.OrdinalIgnoreCase)
        { "localization", "localisation", "locale", "locales", "languages", "language", "lang", "translations", "text", "dialogue", "dialog", "subtitles", "strings", "streamingassets" };
    public ResourceKind Classify(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant(); var ext = Path.GetExtension(path).ToLowerInvariant();
        var parts = path.Replace('\\', '/').Split('/');
        if (ext == ".log" || name is "output_log.txt" or "player.log" or "debug.txt" or "crash.txt" or "error_log.txt" ||
            name.EndsWith("_log.txt") || parts.Any(p => p.Equals("logs", StringComparison.OrdinalIgnoreCase))) return ResourceKind.LogFile;
        if (new[] { ".exe", ".dll", ".pak", ".pck", ".asset", ".bundle", ".png", ".dds", ".wav", ".ogg" }.Contains(ext)) return ResourceKind.Binary;
        if (new[] { ".config", ".cfg", ".cs", ".shader", ".meta", ".manifest" }.Contains(ext) ||
            name is "config.ini" or "boot.config" or "appsettings.json" or "package.json" or "package-lock.json" or "globalgamemanagers" ||
            parts.Any(p => p.Equals("cache", StringComparison.OrdinalIgnoreCase))) return ResourceKind.TechnicalFile;
        if (!TextExtensions.Contains(ext)) return ResourceKind.Unknown;
        if (parts.Any(p => Priority.Contains(p) || Priority.Contains(Path.GetFileNameWithoutExtension(p))) ||
            ext is ".po" or ".lang" or ".locale" or ".loc" or ".strings") return ResourceKind.LocalizationCandidate;
        return ResourceKind.PossibleTextResource;
    }
}

using System.Xml;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Core.Detection;

public sealed class ResourceClassifier
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".json", ".xml", ".csv", ".tsv", ".ini", ".yaml", ".yml", ".po", ".lang", ".locale", ".loc", ".strings", ".rpy" };
    private static readonly HashSet<string> RuntimeDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "Managed", "MonoBleedingEdge", "il2cpp_data", "il2cppOutput", "Mono", "assemblies", "assembly", "metadata", "ReferenceAssemblies" };
    private static readonly HashSet<string> ModDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "BepInEx", "MelonLoader", "Mods", "modloader" };
    private static readonly HashSet<string> LocalizationDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "Translation", "Translations", "Localization", "Localisation", "Language", "Languages", "Text", "Texts", "Locale", "Locales", "Strings", "Lang" };
    private static readonly HashSet<string> WritableLocalizationExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".json", ".xml", ".csv", ".tsv", ".ini", ".po", ".lang", ".locale", ".loc", ".strings" };
    public static bool IsModPath(string path) => Parts(path).Any(ModDirectories.Contains);
    public static bool IsConfiguration(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ini" or ".cfg" or ".config";
    public static bool HasLocalizationDirectory(string path) => Parts(path).SkipLast(1).Any(LocalizationDirectories.Contains);
    public static bool CanAutoSelect(double confidence, TextCategory category) => confidence >= .85 &&
        category is TextCategory.UI or TextCategory.Dialogue or TextCategory.Subtitle or TextCategory.Localization or TextCategory.Quest or TextCategory.Item or TextCategory.Story;
    private static string[] Parts(string path) => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    private static bool Has(string[] parts, params string[] names) => parts.Any(p => names.Contains(Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase));
    public static bool IsRuntimePath(string path)
    {
        var parts = Parts(path);
        // Mod trees are traversed, but individual non-localization files remain non-extractable.
        return !IsModPath(path) && (parts.Any(RuntimeDirectories.Contains) || parts.Any(p => p.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) &&
            parts.Any(p => p.Equals("Plugins", StringComparison.OrdinalIgnoreCase) || p.Equals("Native", StringComparison.OrdinalIgnoreCase)));
    }
    public static bool IsExtractable(ResourceKind kind) => kind is ResourceKind.LocalizationCandidate or ResourceKind.DialogueResource or ResourceKind.SubtitleResource or ResourceKind.UIResource or ResourceKind.PossibleTextResource or ResourceKind.QuestResource or ResourceKind.ItemResource or ResourceKind.StoryResource;
    public ResourceKind Classify(string path)
    {
        var parts = Parts(path);
        var name = parts.LastOrDefault()?.ToLowerInvariant() ?? "";
        var ext = Path.GetExtension(name);
        // Check only directories below the mod root: a parent game's Text folder is not evidence for mod configs.
        var modIndex = Array.FindIndex(parts, ModDirectories.Contains);
        var modLocalization = modIndex >= 0 && parts.Skip(modIndex + 1).SkipLast(1).Any(LocalizationDirectories.Contains) && WritableLocalizationExtensions.Contains(ext);
        var toolName = name.StartsWith("doorstop_config", StringComparison.OrdinalIgnoreCase) || name.StartsWith("winhttp.dll.", StringComparison.OrdinalIgnoreCase) || name == "winhttp.ini" ||
            name.Contains("autotranslator", StringComparison.OrdinalIgnoreCase) || parts.SkipLast(1).Any(p => p.Contains("autotranslator", StringComparison.OrdinalIgnoreCase));
        if (modIndex >= 0 && !modLocalization)
            return IsConfiguration(path) || parts.Skip(modIndex + 1).Any(p => p.Equals("config", StringComparison.OrdinalIgnoreCase) || p.Equals("Launcher", StringComparison.OrdinalIgnoreCase))
                ? ResourceKind.ToolConfiguration : ResourceKind.ModInfrastructure;
        if (toolName && !(HasLocalizationDirectory(path) && WritableLocalizationExtensions.Contains(ext))) return ResourceKind.ToolConfiguration;
        if (parts.SkipLast(1).Any(p => new[] { "config", "configuration", "launcher", "tools" }.Contains(p, StringComparer.OrdinalIgnoreCase)) &&
            !(HasLocalizationDirectory(path) && WritableLocalizationExtensions.Contains(ext))) return ResourceKind.ToolConfiguration;
        if (IsRuntimePath(path) || name is "unity_builtin_extra" or "globalgamemanagers" or "unityplayer.dll") return ResourceKind.EngineRuntime;
        if (ext == ".log" || name is "output_log.txt" or "debug.txt" or "crash.txt" or "error_log.txt" || name.EndsWith("_log.txt") || Has(parts, "logs")) return ResourceKind.LogFile;
        if (ext is ".pdb" or ".meta" or ".manifest" or ".asmdef" or ".asmref" || name.EndsWith(".deps.json") || name.EndsWith(".runtimeconfig.json") ||
            name is "package.json" or "package-lock.json" or "packages-lock.json" || name == "manifest.json" && Has(parts, "Packages") || Has(parts, "PackageCache")) return ResourceKind.AssemblyMetadata;
        if (ext is ".exe" or ".dll" or ".pak" or ".pck" or ".asset" or ".bundle" or ".png" or ".dds" or ".wav" or ".ogg") return ResourceKind.Binary;
        if (modLocalization) return ResourceKind.LocalizationCandidate;
        if (HasLocalizationDirectory(path) && name == "config.ini") return ResourceKind.LocalizationCandidate;
        if (ext is ".config" or ".cfg" or ".cs" or ".shader" || name is "config.ini" or "boot.config" or "appsettings.json" || Has(parts, "cache")) return ResourceKind.TechnicalFile;
        if (!TextExtensions.Contains(ext)) return ResourceKind.Unknown;
        if (Has(parts, "dialogue", "dialog", "dialogs", "scenario", "script") ||
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(name), @"^dialogue[-_]\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return ResourceKind.DialogueResource;
        if (Has(parts, "story", "stories")) return ResourceKind.StoryResource;
        if (Has(parts, "quest", "quests")) return ResourceKind.QuestResource;
        if (Has(parts, "item", "items")) return ResourceKind.ItemResource;
        if (Has(parts, "subtitle", "subtitles")) return ResourceKind.SubtitleResource;
        if (Has(parts, "ui", "menu", "menus", "tooltips")) return ResourceKind.UIResource;
        if (Has(parts, "localization", "localisation", "language", "languages", "locale", "locales", "text", "texts", "strings", "streamingassets", "lang", "translation", "translations") || ext is ".po" or ".lang" or ".locale" or ".loc" or ".strings") return ResourceKind.LocalizationCandidate;
        return ResourceKind.PossibleTextResource;
    }
    // Streaming reader: bounded document, prohibited DTDs, no resolver, cancellation between nodes.
    public static bool IsDocumentationXml(TextReader source, CancellationToken ct = default)
    {
        using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var membersDepth = -1;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Depth == 0 && reader.LocalName != "doc") return false;
                if (reader.Depth == 1 && reader.LocalName == "members") membersDepth = reader.Depth;
                if (membersDepth == 1 && reader.Depth == 2 && reader.LocalName == "member" &&
                    reader.GetAttribute("name") is { Length: > 2 } name && name[1] == ':' && "TMPFE!N".Contains(name[0])) return true;
            }
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == membersDepth) membersDepth = -1;
        }
        return false;
    }
    public static TextCategory Category(ResourceKind kind) => kind switch
    {
        ResourceKind.UIResource => TextCategory.UI, ResourceKind.DialogueResource => TextCategory.Dialogue,
        ResourceKind.SubtitleResource => TextCategory.Subtitle, ResourceKind.LocalizationCandidate => TextCategory.Localization,
        ResourceKind.QuestResource => TextCategory.Quest, ResourceKind.ItemResource => TextCategory.Item, ResourceKind.StoryResource => TextCategory.Story,
        ResourceKind.PossibleTextResource => TextCategory.Possible, _ => TextCategory.Technical
    };
}

namespace GameLocalizer.Core.Models;

public enum EngineType { Unknown, Unity, Unreal, Godot, RenPy, RpgMaker }
public record EngineDetection(EngineType EngineType, double Confidence, IReadOnlyList<string> DetectedEvidence);
public sealed class Game(string id, string name, string path, string platform, string? library = null) : System.ComponentModel.INotifyPropertyChanged
{
    // WPF Selector stores selected items in a hash table. Identity must not depend on mutable state.
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Platform { get; } = platform;
    public string? Library { get; } = library;
    [System.Text.Json.Serialization.JsonIgnore]
    public double EngineConfidence { get; set; }
    private string engine = "Unknown", status = "Не анализирована";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Engine { get => engine; set { engine = value; PropertyChanged?.Invoke(this, new(nameof(Engine))); } }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status { get => status; set { status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => $"{Name} · {Platform}";
}
public enum ResourceKind { LocalizationCandidate, DialogueResource, SubtitleResource, UIResource, PossibleTextResource, TechnicalDocumentation, AssemblyMetadata, EngineRuntime, TechnicalFile, LogFile, Binary, Unknown, ModInfrastructure, ToolConfiguration, QuestResource, ItemResource, StoryResource }
public record Resource(string Path, string Format, bool Editable, string Detail, ResourceKind Kind = ResourceKind.PossibleTextResource);
public record TextEntry(string Key, string Text, string Context = "", string? EntryId = null)
{
    public string Id => EntryId ?? Key;
}
// A local locator, never a provider ID. Including the physical line preserves duplicate keys.
public static class LocalizationEntryId
{
    public static string KeyValue(int line, string key) => $"kv:{line}:{key}";
    public static string DisplayKey(string id)
    {
        if (!id.StartsWith("kv:", StringComparison.Ordinal)) return id;
        var colon = id.IndexOf(':', 3);
        return colon > 3 && int.TryParse(id.AsSpan(3, colon - 3), out var line) && line > 0 ? id[(colon + 1)..] : id;
    }
}
public record TranslationItem(string Id, string Text, string Context, string Category = "Possible", string Key = "");
public record TranslationBatch(IReadOnlyList<TranslationItem> Items);
public record TranslationRequest(TranslationBatch Batch, string TargetLanguage = "ru", string SourceLanguage = "auto")
{
    public const string Instruction = "Translate video game text into Russian. Preserve meaning, style, IDs, placeholders and markup. Keep character names consistent. Return ID to translation mapping.";
}
public record TranslationResult(IReadOnlyDictionary<string, string> Translations, bool Cancelled = false);
public record MemoryEntry(string SourceText, string TranslatedText, string SourceLanguage, string TargetLanguage,
    string GameId, string GameName, string FilePath, string Key, string Context, string SourceHash, string Provider,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Category = "Possible", string TranslationModel = "", string TranslationModelVersion = "", string GlossaryVersion = "", DateTimeOffset? LastUsedAt = null, bool IsManual = false);
public enum LocalizationApplyMode { CompatibleReplacement, SeparateTargetLocale }
public record FileChange(string RelativePath, string ExpectedHash, byte[] Content)
{
    public LocalizationApplyMode ApplyMode { get; init; } = LocalizationApplyMode.CompatibleReplacement;
    public List<string> SelectedEntryIds { get; init; } = [];
    public Dictionary<string, string> EntryCategories { get; init; } = [];
    public string AdapterType { get; init; } = "";
    public string LocalizationSlot { get; init; } = "";
}
public record BackupEntry(string RelativePath, string OriginalHash, string AppliedHash, string ObjectName, DateTimeOffset CreatedAt);
public class AppSettings
{
    public string Language { get; set; } = "ru";
    public string TranslationProvider { get; set; } = "Offline";
    public OfflineSettings Offline { get; set; } = new();
    public string TargetLanguage { get; set; } = "ru";
    public LocalizationApplyMode ApplyMode { get; set; } = LocalizationApplyMode.CompatibleReplacement;
    public bool DiagnosticApplyTrace { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string GitHubRepository { get; set; } = "koteiik/GameLocalizer";
    public List<Game> ManualGames { get; set; } = [];
}

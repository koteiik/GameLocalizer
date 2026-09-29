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
    private string engine = "Unknown", status = "Не анализирована";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Engine { get => engine; set { engine = value; PropertyChanged?.Invoke(this, new(nameof(Engine))); } }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status { get => status; set { status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => $"{Name} · {Platform}";
}
public enum ResourceKind { LocalizationCandidate, PossibleTextResource, TechnicalFile, LogFile, Binary, Unknown }
public record Resource(string Path, string Format, bool Editable, string Detail, ResourceKind Kind = ResourceKind.PossibleTextResource);
public record TextEntry(string Key, string Text, string Context = "");
public record TranslationItem(string Id, string Text, string Context);
public record TranslationBatch(IReadOnlyList<TranslationItem> Items);
public record TranslationRequest(TranslationBatch Batch, string TargetLanguage = "ru", string SourceLanguage = "auto")
{
    public const string Instruction = "Translate video game text into Russian. Preserve meaning, style, IDs, placeholders and markup. Keep character names consistent. Return ID to translation mapping.";
}
public record TranslationResult(IReadOnlyDictionary<string, string> Translations);
public record MemoryEntry(string SourceText, string TranslatedText, string SourceLanguage, string TargetLanguage,
    string GameId, string GameName, string FilePath, string Key, string Context, string SourceHash, string Provider,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public record FileChange(string RelativePath, string ExpectedHash, byte[] Content);
public record BackupEntry(string RelativePath, string OriginalHash, string AppliedHash, string ObjectName, DateTimeOffset CreatedAt);
public class AppSettings
{
    public string Language { get; set; } = "ru";
    public string TranslationProvider { get; set; } = "Mock";
    public string TargetLanguage { get; set; } = "ru";
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string GitHubRepository { get; set; } = "koteiik/GameLocalizer";
    public List<Game> ManualGames { get; set; } = [];
}

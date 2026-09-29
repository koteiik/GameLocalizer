using GameLocalizer.Core.Models;
namespace GameLocalizer.Core.Interfaces;

public interface IGameDiscoveryService { Task<IReadOnlyList<Game>> DiscoverAsync(CancellationToken cancellationToken); }
public interface IEngineDetector { EngineDetection Detect(string directory, CancellationToken cancellationToken); }
public interface ILocalizationAdapter
{
    string Name { get; }
    bool CanHandle(string path);
    IReadOnlyList<TextEntry> Extract(string text);
    string ApplyTranslations(string text, IReadOnlyDictionary<string, string> translations);
    bool Validate(string original, string modified, IReadOnlyDictionary<string, string> translations);
}
public interface ITranslationProvider
{
    string Name { get; }
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
public interface ITranslationMemoryService
{
    Task<string?> FindAsync(string text, string sourceLanguage, string targetLanguage, string gameId, string context, string provider, CancellationToken ct);
    Task SaveAsync(MemoryEntry entry, CancellationToken ct);
}
// Future implementations live outside Core and are registered through dependency injection.
public interface IScreenCaptureService { Task<byte[]> CaptureAsync(nint window, CancellationToken ct); }
public interface ITextRecognitionService { Task<IReadOnlyList<TextEntry>> RecognizeAsync(byte[] image, CancellationToken ct); }
public interface IOverlayService { Task ShowAsync(IReadOnlyList<TextEntry> translatedRegions, CancellationToken ct); }
public interface ISecretStore { void Save(string name, string secret); string? Read(string name); }

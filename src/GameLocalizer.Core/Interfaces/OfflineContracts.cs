using GameLocalizer.Core.Models;
namespace GameLocalizer.Core.Interfaces;

public interface ITranslationModelManager
{
    string ModelDirectory { get; }
    TranslationModelInfo GetModelInfo();
    bool IsInstalled { get; }
    IReadOnlyList<TranslationModelInfo> GetInstalledModels();
    Task DownloadModelAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct);
    Task<bool> VerifyModelAsync(CancellationToken ct);
    Task DeleteModelAsync(CancellationToken ct);
}
public interface ITranslationRuntime : IDisposable
{
    long MemoryBytes => 0;
    bool IsLoaded { get; }
    string Device { get; }
    Task LoadAsync(string directory, TranslationDevice device, CancellationToken ct);
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> text, CancellationToken ct);
    void Unload();
}
public interface IHardwareDetectionService { HardwareInfo Detect(); }
public interface ITranslationSessionProvider
{
    long MemoryBytes => 0;
    bool IsLoaded { get; }
    string Device { get; }
    void EndJob(bool cancelled = false);
}

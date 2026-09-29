using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.TranslationProviders;

public sealed class ConfiguredTranslationProvider(AppSettings settings, LocalOfflineTranslationProvider offline, MockTranslationProvider mock)
    : ITranslationProvider, ITranslationSessionProvider
{
    private ITranslationProvider Active => settings.TranslationProvider == "Mock" ? mock : offline;
    public string Name => Active.Name;
    public string ModelName => Active.ModelName;
    public string ModelVersion => Active.ModelVersion;
    public long MemoryBytes => offline.MemoryBytes;
    public bool IsLoaded => offline.IsLoaded;
    public string Device => settings.TranslationProvider == "Mock" ? "CPU (Mock)" : offline.Device;
    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken) => Active.TranslateAsync(request, cancellationToken);
    public void EndJob(bool cancelled = false) => offline.EndJob(cancelled);
}

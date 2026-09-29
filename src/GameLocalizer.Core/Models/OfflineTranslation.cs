namespace GameLocalizer.Core.Models;

public enum TranslationDevice { Auto, GPU, CPU }
public enum TranslationStatus { NotTranslated, Queued, Translating, Translated, FromMemory, Manual, ValidationError, Cancelled, Failed }
public enum TranslationJobStatus { Pending, Running, Completed, PartiallyCompleted, Cancelled, Failed }
public sealed class OfflineSettings
{
    public string Model { get; set; } = "opus-mt-en-ru-int8";
    public TranslationDevice Device { get; set; } = TranslationDevice.Auto;
    public int BatchSize { get; set; } = 4;
    public bool KeepModelLoaded { get; set; }
}
public record ModelFile(string Name, long Size, string Sha256, string Url);
public record TranslationModelInfo(string Id, string Name, string Version, string License, IReadOnlyList<ModelFile> Files)
{
    public long DownloadBytes => Files.Sum(f => f.Size);
    public string Requirements => "Windows x64; рекомендуется 4 GB RAM, 1 GB свободной RAM; CPU или DirectML GPU; CUDA не требуется.";
}
public record ModelDownloadProgress(string File, long DownloadedBytes, long TotalBytes);
public record HardwareInfo(string Cpu, long RamBytes, string Gpu, long? GpuMemoryBytes, bool CanAttemptGpu);
public record MemoryKey(string SourceText, string SourceLanguage, string TargetLanguage, string GameId, string Context,
    string Provider, string Model, string ModelVersion, string GlossaryVersion, string Category = "Possible");
public record CachedTranslation(string Text, bool Manual);
public record TranslationOutcome(string Id, string Translation, TranslationStatus Status, string? Error = null);
public record TranslationPreflight(long TotalStrings, long CachedStrings, long ManualStrings, long Characters, string Model, string Device)
{
    public long RequiresTranslation => TotalStrings - CachedStrings - ManualStrings;
    public long EstimatedTokens => (Characters + 3) / 4;
}
public sealed class TranslationJob
{
    public string Device { get; set; } = "CPU";
    public bool TestOnly { get; set; }
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long TotalStrings { get; set; }
    public long CachedStrings { get; set; }
    public long TranslatedStrings { get; set; }
    public long FailedStrings { get; set; }
    public long CancelledStrings { get; set; }
    public TranslationJobStatus Status { get; set; } = TranslationJobStatus.Pending;
}
public record TranslationJobProgress(TranslationJob Job, int CurrentBatch, bool ModelLoaded, string Device, long RamBytes);
public record GlossaryEntry(string Original, string Russian, bool CaseSensitive = true, string? Category = null);

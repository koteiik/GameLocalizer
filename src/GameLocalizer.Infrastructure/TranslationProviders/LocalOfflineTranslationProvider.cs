using System.Text.RegularExpressions;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.TranslationProviders;

public sealed class LocalOfflineTranslationProvider(ITranslationModelManager models, ITranslationRuntime runtime,
    IHardwareDetectionService hardware, OfflineSettings settings) : ITranslationProvider, ITranslationSessionProvider, IDisposable
{
    public string Name => "Offline";
    public string ModelName => models.GetModelInfo().Id;
    public string ModelVersion => models.GetModelInfo().Version + ":greedy-v1";
    public long MemoryBytes => runtime.MemoryBytes;
    public bool IsLoaded => runtime.IsLoaded;
    public string Device => runtime.Device;
    private static readonly Regex Protected = new("(__GL_[a-f0-9]{32}_[0-9]+__)", RegexOptions.Compiled);
    private async Task EnsureLoaded(CancellationToken ct)
    {
        if (runtime.IsLoaded) return;
        if (!await models.VerifyModelAsync(ct)) throw new InvalidDataException("Офлайн-модель не установлена или повреждена. Скачайте/проверьте модель в настройках.");
        var device = settings.Device == TranslationDevice.Auto ? (hardware.Detect().CanAttemptGpu ? TranslationDevice.GPU : TranslationDevice.CPU) : settings.Device;
        try { await runtime.LoadAsync(models.ModelDirectory, device, ct); }
        catch (Exception e) when (device == TranslationDevice.GPU && !ct.IsCancellationRequested && e is not IOException and not UnauthorizedAccessException)
        { runtime.Unload(); await runtime.LoadAsync(models.ModelDirectory, TranslationDevice.CPU, ct); }
    }
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (request.TargetLanguage != "ru" || request.SourceLanguage is not ("en" or "auto")) throw new NotSupportedException("Эта модель поддерживает только EN → RU.");
        var parts = request.Batch.Items.ToDictionary(i => i.Id, i => Protected.Split(i.Text));
        var segments = new List<(string Id, int Index, string Text)>();
        foreach (var (id, chunks) in parts) for (var index = 0; index < chunks.Length; index++)
            if (!Protected.IsMatch(chunks[index]) && chunks[index].Any(char.IsLetter)) segments.Add((id, index, chunks[index].Trim()));
        if (segments.Count != 0) await EnsureLoaded(cancellationToken);
        var batchSize = Math.Clamp(settings.BatchSize, 1, Device.StartsWith("GPU", StringComparison.Ordinal) ? 16 : 8);
        var position = 0;
        while (position < segments.Count)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var batch = segments.Skip(position).Take(batchSize).ToArray();
            try
            {
                var translated = await runtime.TranslateAsync(batch.Select(s => s.Text).ToArray(), cancellationToken);
                if (translated.Count != batch.Length) throw new InvalidDataException("Model returned wrong batch size");
                for (var i = 0; i < batch.Length; i++)
                {
                    var original = parts[batch[i].Id][batch[i].Index];
                    var leading = original[..(original.Length - original.TrimStart().Length)];
                    var trailing = original[original.TrimEnd().Length..];
                    parts[batch[i].Id][batch[i].Index] = leading + translated[i].Trim() + trailing;
                }
                position += batch.Length;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception e) when (IsOutOfMemory(e) && batchSize > 1) { batchSize = Math.Max(1, batchSize / 2); }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && Device.StartsWith("GPU", StringComparison.Ordinal) && (IsOutOfMemory(e) || e is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or InvalidOperationException))
            { runtime.Unload(); await runtime.LoadAsync(models.ModelDirectory, TranslationDevice.CPU, cancellationToken); batchSize = 1; }
        }
        var unfinished = segments.Skip(position).Select(s => s.Id).ToHashSet();
        return new(parts.Where(p => !unfinished.Contains(p.Key)).ToDictionary(p => p.Key, p => string.Concat(p.Value)), position < segments.Count);
    }
    private static bool IsOutOfMemory(Exception e) => e is OutOfMemoryException || e.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Failed to allocate", StringComparison.OrdinalIgnoreCase);
    public void EndJob(bool cancelled = false) { if (cancelled || !settings.KeepModelLoaded) runtime.Unload(); }
    public void Dispose() => runtime.Dispose();
}

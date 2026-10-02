using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Translation;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.Database;
using Microsoft.Extensions.Logging;
namespace GameLocalizer.Infrastructure.TranslationProviders;

public sealed class TranslationService(ITranslationProvider provider, ITranslationMemoryService memory, ILogger<TranslationService> logger, GlossaryService? glossary = null)
{
    private readonly GlossaryService glossary = glossary ?? new();
    public string Profile => System.Text.Json.JsonSerializer.Serialize(new[] { provider.Name, provider.ModelName, provider.ModelVersion, glossary.Version });
    public string Provider => provider.Name;
    public string Model => provider.ModelName;
    public long MemoryBytes => provider is ITranslationSessionProvider session ? session.MemoryBytes : 0;
    public bool IsLoaded => provider is ITranslationSessionProvider session && session.IsLoaded;
    public string Device => provider is ITranslationSessionProvider session ? session.Device : "CPU (Mock)";
    public void EndJob(bool cancelled = false) { if (provider is ITranslationSessionProvider session) session.EndJob(cancelled); }
    public MemoryKey Key(Game game, TranslationItem item) => new(item.Text, "en", "ru", game.Id,
        item.Context, provider.Name, provider.ModelName, provider.ModelVersion, glossary.Version, item.Category);
    public async Task<CachedTranslation?> FindAsync(Game game, TranslationItem item, CancellationToken ct)
    {
        var cached = await memory.FindAsync(Key(game, item), ct);
        return cached != null && new TranslationValidator().Validate(item.Text, cached.Text, out _) ? cached : null;
    }
    public async Task<IReadOnlyDictionary<string, CachedTranslation>> FindManyAsync(Game game, IReadOnlyList<TranslationItem> items, CancellationToken ct)
    {
        var keys = items.ToDictionary(i => i.Id, i => Key(game, i));
        var cached = await memory.FindManyAsync(keys.Values.ToArray(), ct); var result = new Dictionary<string, CachedTranslation>();
        foreach (var item in items) if (cached.TryGetValue(TranslationMemoryService.Identity(keys[item.Id]), out var value) && new TranslationValidator().Validate(item.Text, value.Text, out _)) result[item.Id] = value;
        return result;
    }
    public async Task SaveManualAsync(Game game, string file, TranslationItem item, string translation, CancellationToken ct)
    {
        if (!new TranslationValidator().Validate(item.Text, translation, out var error)) throw new InvalidDataException(error);
        await Save(game, file, item, translation, true, ct);
    }
    private Task Save(Game game, string file, TranslationItem item, string translated, bool manual, CancellationToken ct)
    {
        var key = Key(game, item); var now = DateTimeOffset.UtcNow;
        return memory.SaveAsync(new(item.Text, translated, key.SourceLanguage, key.TargetLanguage, game.Id, game.Name, file, LocalizationEntryId.DisplayKey(item.Key.Length == 0 ? item.Id : item.Key),
            key.Context, TranslationMemoryService.Identity(key, manual), provider.Name, now, now, item.Category, key.Model, key.ModelVersion, key.GlossaryVersion, now, manual), ct);
    }
    public async Task<IReadOnlyList<TranslationOutcome>> TranslateDetailedAsync(Game game, string file, IReadOnlyList<TranslationItem> items, bool ignoreMemory, CancellationToken ct, bool persistResults = true)
    {
        var result = new Dictionary<string, TranslationOutcome>(); var pending = new List<TranslationItem>();
        var cache = await FindManyAsync(game, items, ct);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            cache.TryGetValue(item.Id, out var cached);
            if (cached != null && (!ignoreMemory || cached.Manual)) result[item.Id] = new(item.Id, cached.Text, cached.Manual ? TranslationStatus.Manual : TranslationStatus.FromMemory);
            else if (glossary.Match(item.Text, item.Category) is { } term)
            { if(persistResults) await Save(game, file, item, term, false, ct); result[item.Id] = new(item.Id, term, TranslationStatus.Translated); }
            else pending.Add(item);
        }
        var unique = pending.GroupBy(i => (i.Text, i.Context, i.Category)).Select(g => g.First()).ToArray();
        foreach (var batch in new TranslationBatchBuilder().Build(unique, 16)) await TranslateBatch(batch);
        logger.LogInformation("Translation: {Count} rows, {Cached} cached/glossary, {Failed} failed", items.Count, items.Count - pending.Count, result.Values.Count(r => r.Status is TranslationStatus.Failed or TranslationStatus.ValidationError));
        return items.Select(i => result[i.Id]).ToArray();

        async Task TranslateBatch(IReadOnlyList<TranslationItem> batch)
        {
            ct.ThrowIfCancellationRequested();
            var protector = new PlaceholderProtector(); var maps = batch.ToDictionary(i => i.Id, i => protector.Protect(i.Text));
            TranslationResult response;
            // Provider IDs are opaque batch ordinals. Local keys/locators never cross this boundary.
            var providerIds = batch.Select((item, index) => (item.Id, WireId: index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToDictionary(p => p.Id, p => p.WireId);
            try { response = await provider.TranslateAsync(new(new(batch.Select(i => new TranslationItem(providerIds[i.Id], maps[i.Id].Text, i.Context, i.Category)).ToArray())), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                if (batch.Count > 1) { foreach (var item in batch) await TranslateBatch([item]); return; }
                Complete(batch[0], "", TranslationStatus.Failed, $"{e.GetType().Name}: {e.Message}"); return;
            }
            foreach (var item in batch)
            {
                try
                {
                    if (!response.Translations.TryGetValue(providerIds[item.Id], out var translated))
                    {
                        if (response.Cancelled) { Complete(item, "", TranslationStatus.Cancelled); continue; }
                        throw new InvalidDataException("Provider omitted ID");
                    }
                    translated = protector.Restore(translated, maps[item.Id].Tokens);
                    if (string.IsNullOrWhiteSpace(translated) || !new TranslationValidator().Validate(item.Text, translated, out _)) throw new InvalidDataException("Placeholder/markup validation failed");
                    // A completed response is durable even if cancellation arrives before the next batch.
                    if(persistResults) await Save(game, file, item, translated, false, CancellationToken.None);
                    Complete(item, translated, TranslationStatus.Translated);
                }
                catch (InvalidDataException e) { Complete(item, "", TranslationStatus.ValidationError, e.Message); }
            }
        }
        void Complete(TranslationItem item, string translation, TranslationStatus status, string? error = null)
        {
            foreach (var duplicate in pending.Where(i => i.Text == item.Text && i.Context == item.Context && i.Category == item.Category)) result[duplicate.Id] = new(duplicate.Id, translation, status, error);
        }
    }
    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(Game game, string file, IReadOnlyList<TranslationItem> items, CancellationToken ct)
    {
        try
        {
            var outcomes = await TranslateDetailedAsync(game, file, items, false, ct);
            ct.ThrowIfCancellationRequested();
            if (outcomes.Any(o => o.Status is TranslationStatus.Failed or TranslationStatus.ValidationError)) throw new InvalidDataException("Translation failed or failed validation");
            return outcomes.ToDictionary(o => o.Id, o => o.Translation);
        }
        finally { EndJob(ct.IsCancellationRequested); }
    }
}

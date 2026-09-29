using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.Database;
using Microsoft.Extensions.Logging;
namespace GameLocalizer.Infrastructure.TranslationProviders;

public sealed class TranslationService(ITranslationProvider provider, ITranslationMemoryService memory, ILogger<TranslationService> logger)
{
    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(Game game, string file, IReadOnlyList<TranslationItem> items, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(); var pending = new List<TranslationItem>();
        foreach (var item in items)
        {
            var cached = await memory.FindAsync(item.Text, "auto", "ru", game.Id, item.Context, provider.Name, ct);
            if (cached != null && new TranslationValidator().Validate(item.Text, cached, out _)) result[item.Id] = cached;
            else pending.Add(item);
        }
        foreach (var batch in pending.GroupBy(i => (i.Text, i.Context)).Select(g => g.First()).Chunk(40))
        {
            var unique = batch.GroupBy(i => (i.Text, i.Context)).Select(g => g.First()).ToArray();
            var protector = new PlaceholderProtector(); var maps = unique.ToDictionary(i => i.Id, i => protector.Protect(i.Text));
            var response = await provider.TranslateAsync(new(new(unique.Select(i => i with { Text = maps[i.Id].Text }).ToArray())), ct);
            foreach (var item in unique)
            {
                if (!response.Translations.TryGetValue(item.Id, out var translated)) throw new InvalidDataException("Provider omitted ID");
                translated = protector.Restore(translated, maps[item.Id].Tokens);
                if (!new TranslationValidator().Validate(item.Text, translated, out var error)) throw new InvalidDataException(error);
                foreach (var duplicate in pending.Where(i => i.Text == item.Text && i.Context == item.Context)) result[duplicate.Id] = translated;
                var now = DateTimeOffset.UtcNow;
                await memory.SaveAsync(new(item.Text, translated, "auto", "ru", game.Id, game.Name, file, item.Id, item.Context, TranslationMemoryService.Hash(item.Text), provider.Name, now, now), ct);
            }
        }
        logger.LogInformation("Translation: {Count} rows, {Cached} cached", items.Count, items.Count - pending.Count);
        return result;
    }
}

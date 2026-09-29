using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;
namespace GameLocalizer.Infrastructure.FileSystem;

public sealed partial class ScanWorkspaceService
{
    private static TranslationItem Item(ScanRow row) => new(row.Id.ToString(), row.Original, row.Context, row.Category.ToString(), row.Key);
    public string Profile => translator.Profile;
    public string Provider => translator.Provider;
    public string Model => translator.Model;
    public long ModelMemoryBytes => translator.MemoryBytes;
    public bool IsModelLoaded => translator.IsLoaded;
    public void UnloadModel() => translator.EndJob(true);
    public TranslationJob? FindIncomplete(Game game) => jobs?.FindIncomplete(game.Id);
    public async Task RestoreMemoryAsync(Game game, string session, CancellationToken ct)
    {
        long afterId = 0;
        while (true)
        {
            var rows = await repository.ReadRowsAsync(session, afterId, ct); if (rows.Count == 0) break;
            var cached = await translator.FindManyAsync(game, rows.Select(Item).ToArray(), ct);
            var edits = rows.Where(r => r.Status != "Manual" && cached.ContainsKey(r.Id.ToString())).Select(r =>
            {
                var value = cached[r.Id.ToString()]; return new ScanEdit(r.Id, value.Text, r.Selected, value.Manual ? TranslationStatus.Manual : TranslationStatus.FromMemory);
            }).ToArray();
            if (edits.Length != 0) await repository.SaveEditsAsync(session, edits, ct);
            afterId = rows[^1].Id;
        }
    }
    public async Task SaveManualEditsAsync(Game game, string session, IReadOnlyList<ScanEdit> edits, CancellationToken ct)
    {
        foreach (var group in edits.Where(e => (e.Status is null or TranslationStatus.Manual) && !string.IsNullOrWhiteSpace(e.Translation)).Chunk(1000))
        {
            var rows = await repository.RowsByIdAsync(session, group.Select(e => e.Id).ToHashSet(), ct);
            foreach (var row in rows)
            {
                var edit = group.First(e => e.Id == row.Id);
                if (new TranslationValidator().Validate(row.Original, edit.Translation, out _)) await translator.SaveManualAsync(game, row.FilePath, Item(row), edit.Translation, ct);
            }
        }
    }
    public async Task<TranslationPreflight> PreflightAsync(Game game, string session, bool ignoreMemory, CancellationToken ct)
    {
        long afterId = 0, total = 0, cached = 0, manual = 0, characters = 0;
        while (true)
        {
            var rows = await repository.ReadSelectedAsync(session, afterId, false, ct); if (rows.Count == 0) break;
            var found = await translator.FindManyAsync(game, rows.Select(Item).ToArray(), ct);
            foreach (var row in rows)
            {
                total++; characters += row.Original.Length; found.TryGetValue(row.Id.ToString(), out var previous);
                if (row.Status == "Manual" || previous?.Manual == true) manual++;
                else if (!ignoreMemory && (previous != null || row.Status is "Translated" or "FromMemory")) cached++;
            }
            afterId = rows[^1].Id;
        }
        return new(total, cached, manual, characters, translator.Model, translator.Device);
    }
    public Task<IReadOnlyList<ScanRow>> GetTestRowsAsync(string session, CancellationToken ct) => TestSample(session, ct);
    private async Task<IReadOnlyList<ScanRow>> TestSample(string session, CancellationToken ct)
    {
        var categories = new Dictionary<TextCategory, List<ScanRow>>(); long afterId = 0;
        while (true)
        {
            var rows = await repository.ReadSelectedAsync(session, afterId, false, ct); if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                if (!categories.TryGetValue(row.Category, out var list)) categories[row.Category] = list = [];
                if (list.Count < 20) list.Add(row);
            }
            afterId = rows[^1].Id;
        }
        return Enumerable.Range(0, 20).SelectMany(i => categories.Values.Where(v => v.Count > i).Select(v => v[i])).Take(20).ToArray();
    }
    public async Task<TranslationJob> RunTranslationJobAsync(Game game, string session, bool ignoreMemory, bool testOnly,
        IProgress<TranslationJobProgress>? progress, CancellationToken ct)
    {
        var preflight = await PreflightAsync(game, session, ignoreMemory, ct);
        var test = testOnly ? await TestSample(session, ct) : null;
        var job = new TranslationJob { TestOnly = testOnly, GameId = game.Id, Provider = translator.Provider, Model = translator.Model, TotalStrings = test?.Count ?? preflight.TotalStrings, StartedAt = DateTimeOffset.UtcNow, Status = TranslationJobStatus.Running };
        if (jobs != null) await jobs.SaveAsync(job, ct);
        int batchNumber = 0; long afterId = 0;
        try
        {
            if (!testOnly) await repository.SetPendingStatusAsync(session, TranslationStatus.Queued, ct);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = test ?? await repository.ReadSelectedAsync(session, afterId, false, ct);
                if (page.Count == 0) break;
                foreach (var group in page.GroupBy(r => r.FilePath)) foreach (var batch in group.Chunk(16))
                {
                    ct.ThrowIfCancellationRequested(); batchNumber++;
                    var pending = batch.Where(r => r.Status != "Manual" && (ignoreMemory || r.Status is not ("Translated" or "FromMemory"))).ToArray();
                    job.CachedStrings += batch.Length - pending.Length;
                    if (pending.Length != 0)
                    {
                        await repository.SaveEditsAsync(session, pending.Select(r => new ScanEdit(r.Id, r.Translation, r.Selected, TranslationStatus.Translating)).ToArray(), ct);
                        progress?.Report(Report());
                        var outcomes = await translator.TranslateDetailedAsync(game, group.Key, pending.Select(Item).ToArray(), ignoreMemory, ct);
                        await repository.SaveEditsAsync(session, outcomes.Select(o => new ScanEdit(long.Parse(o.Id), o.Translation, true, o.Status)).ToArray(), CancellationToken.None);
                        job.CachedStrings += outcomes.Count(o => o.Status is TranslationStatus.FromMemory or TranslationStatus.Manual);
                        job.TranslatedStrings += outcomes.Count(o => o.Status == TranslationStatus.Translated);
                        job.FailedStrings += outcomes.Count(o => o.Status is TranslationStatus.Failed or TranslationStatus.ValidationError);
                    }
                    if (jobs != null) await jobs.SaveAsync(job, CancellationToken.None);
                    progress?.Report(Report());
                }
                if (testOnly) break;
                afterId = page[^1].Id;
            }
            ct.ThrowIfCancellationRequested();
            job.Status = job.FailedStrings == 0 ? TranslationJobStatus.Completed : job.FailedStrings == job.TotalStrings ? TranslationJobStatus.Failed : TranslationJobStatus.PartiallyCompleted;
        }
        catch (OperationCanceledException)
        {
            job.Status = TranslationJobStatus.Cancelled;
            await RestoreMemoryAsync(game, session, CancellationToken.None);
            var completed = await PreflightAsync(game, session, false, CancellationToken.None);
            job.CachedStrings = Math.Min(job.TotalStrings, preflight.CachedStrings + preflight.ManualStrings);
            job.TranslatedStrings = Math.Max(job.TranslatedStrings, Math.Min(job.TotalStrings, completed.CachedStrings + completed.ManualStrings) - job.CachedStrings);
            job.CancelledStrings = Math.Max(0, job.TotalStrings - job.TranslatedStrings - job.CachedStrings - job.FailedStrings);
            await repository.SetPendingStatusAsync(session, TranslationStatus.Cancelled, CancellationToken.None);
        }
        catch
        {
            job.Status = TranslationJobStatus.Failed;
            await repository.SetPendingStatusAsync(session, TranslationStatus.Failed, CancellationToken.None); throw;
        }
        finally
        {
            translator.EndJob(job.Status is TranslationJobStatus.Cancelled or TranslationJobStatus.Failed);
            job.Device = translator.Device;
            job.FinishedAt = DateTimeOffset.UtcNow;
            if (jobs != null) await jobs.SaveAsync(job, CancellationToken.None);
            progress?.Report(Report());
        }
        return job;
        TranslationJobProgress Report() => new(job, batchNumber, translator.IsLoaded, translator.Device, System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 + translator.MemoryBytes);
    }
    public async Task<long> TranslateAsync(Game game, string session, IProgress<long>? progress, CancellationToken ct)
    {
        var job = await RunTranslationJobAsync(game, session, false, false, null, ct);
        progress?.Report(job.CachedStrings + job.TranslatedStrings);
        return job.CachedStrings + job.TranslatedStrings;
    }
}

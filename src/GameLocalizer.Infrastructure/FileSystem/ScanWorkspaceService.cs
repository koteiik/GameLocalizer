using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.TranslationProviders;

namespace GameLocalizer.Infrastructure.FileSystem;

public sealed partial class ScanWorkspaceService(ScanPipeline pipeline, ScanResultRepository repository,
    TranslationService translator, BackupService backup, IEnumerable<ILocalizationAdapter> adapters, TranslationJobStore? jobs = null)
{
    public async Task<ScanProgress> ScanAsync(Game game, string session, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var latest = new ScanProgress(0, 0, 0, 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var batch in pipeline.ScanAsync(game.Path, ct))
        {
            await repository.AppendAsync(session, game.Id, batch, ct);
            latest = batch.Progress;
            if (clock.ElapsedMilliseconds >= 100) { progress?.Report(latest); clock.Restart(); }
        }
        progress?.Report(latest);
        await RestoreMemoryAsync(game, session, ct);
        return latest;
    }
    public async Task<int> ApplyAsync(Game game, string session, CancellationToken ct)
    {
        var files = await repository.SelectedFilesAsync(session, ct);
        var staging = Path.Combine(Path.GetTempPath(), "GameLocalizer", "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var prepared = new List<(ScannedFile File, string StagedPath)>();
        try
        {
            // Prevalidate every selected file, including off-page edits, before changing any game file.
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var path = BackupService.Resolve(game.Path, file.FilePath);
                var snapshot = await TextFiles.ReadAsync(path, ct);
                if (snapshot.Hash != file.SourceHash) throw new IOException("Файл изменился после анализа: " + file.FilePath);
                var rows = await repository.FileRowsAsync(session, file.FilePath, ct);
                foreach (var row in rows)
                    if (!new TranslationValidator().Validate(row.Original, row.Translation, out var error)) throw new InvalidDataException(error);
                var adapter = LocalizationAdapterSelector.Select(adapters, path, snapshot.Text); var translations = rows.ToDictionary(r => r.Key, r => r.Translation);
                var modified = adapter.ApplyTranslations(snapshot.Text, translations);
                if (!adapter.Validate(snapshot.Text, modified, translations)) throw new InvalidDataException("Structure validation failed: " + file.FilePath);
                var bytes = snapshot.Encode(modified);
                if (bytes.Length > TextFiles.MaxBytes) throw new IOException("Переведённый файл превышает лимит 4 MiB: " + file.FilePath);
                var stagedPath = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                await File.WriteAllBytesAsync(stagedPath, bytes, ct); prepared.Add((file, stagedPath));
            }
            foreach (var (file, stagedPath) in prepared)
            {
                ct.ThrowIfCancellationRequested();
                await backup.ApplyAsync(game.Path, [new(file.FilePath, file.SourceHash, await File.ReadAllBytesAsync(stagedPath, ct))], ct);
            }
            return files.Count;
        }
        finally { Directory.Delete(staging, true); }
    }
}

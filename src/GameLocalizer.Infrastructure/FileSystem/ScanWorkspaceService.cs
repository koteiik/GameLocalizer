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
    public UiResourceDiscovery UiDiscovery => new(adapters);
    public UiResourceDiscovery CreateUiDiscovery(ScanDiagnosticLog diagnostics) => new(adapters, diagnostics);
    public string? ScanDiagnosticLogPath => pipeline.DiagnosticLog.LastLogPath;
    public int ScanErrorCount => Math.Max(pipeline.ErrorCount,analysisErrors);
    public string? LastDiagnosticReportPath => backup.LastDiagnosticReportPath;
    public ApplyDiagnosticReport? LastApplyReport => backup.LastDiagnosticReport;
    public Task VerifyApplyOwnershipAsync(Game game,IReadOnlyDictionary<string,string> files,CancellationToken ct) => backup.VerifyOwnershipAsync(game.Path,files,ct);
    public string DiagnosticSummary => backup.LastDiagnosticReport?.Summary ?? "";
    public bool DiagnosticApplyTrace { get => backup.DiagnosticApplyTrace; set => backup.DiagnosticApplyTrace = value; }
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
    public Task RecordBlockedApplyAsync(Game game, ApplySelectionSummary summary) => backup.RecordPreparationFailureAsync(game.Path, new InvalidDataException("Blocking ValidationError"), summary);
    public Task<ApplySelectionSummary> ApplySelectionAsync(string session, CancellationToken ct) => repository.ApplySelectionAsync(session, ct);
    public async Task<string> ApplyDescriptionAsync(string session, CancellationToken ct, bool skipEmpty = false)
    {
        var files = await repository.SelectedFilesAsync(session, skipEmpty, ct);
        var descriptions = new List<string>();
        foreach (var file in files)
        {
            var row = (await repository.FileRowsAsync(session, file.FilePath, skipEmpty, ct)).First();
            var slot = row.LocalizationSlot.Length > 0 ? row.LocalizationSlot : ActiveLocalizationResolver.SlotFromPath(file.FilePath);
            descriptions.Add($"Существующий слот: {(slot.Equals("en", StringComparison.OrdinalIgnoreCase) ? "English (en)" : slot)} · Файл: {file.FilePath}");
        }
        return "Режим: Совместимый режим (рекомендуется)\n" +
            string.Join("\n", descriptions) +
            "\nРусский текст записывается поверх существующей локализации, которую игра уже поддерживает. Игра продолжает считать её прежним языком, поэтому отдельная поддержка русского не требуется.\nОригинальные файлы будут сохранены в резервной копии.\n" + FontCompatibilityService.Warning;
    }
    public Task<int> ApplyAsync(Game game, string session, CancellationToken ct) => ApplyAsync(game, session, LocalizationApplyMode.CompatibleReplacement, ct);
    public Task<int> ApplyAsync(Game game, string session, LocalizationApplyMode mode, CancellationToken ct) => ApplyAsync(game, session, mode, false, ct);
    public async Task<int> ApplyAsync(Game game, string session, LocalizationApplyMode mode, bool skipEmpty, CancellationToken ct, IReadOnlySet<string>? verifiedFiles = null)
    {
        if (mode != LocalizationApplyMode.CompatibleReplacement) throw new NotSupportedException("SeparateTargetLocale — будущий расширенный режим; пока недоступен.");
        var selection = await repository.ApplySelectionAsync(session, ct);
        var files = (await repository.SelectedFilesAsync(session, skipEmpty, ct)).Where(f => verifiedFiles == null || !verifiedFiles.Contains(f.FilePath)).ToArray();
        var staging = Path.Combine(Path.GetTempPath(), "GameLocalizer", "staging", Guid.NewGuid().ToString("N"));
        if (files.Length == 0) { if (skipEmpty) await backup.RecordNoWriteApplyAsync(game.Path, selection); return 0; }
        var configuration = await LocalizationConfiguration.ReadAsync(game.Path, ct);
        Directory.CreateDirectory(staging);
        var prepared = new List<FileChange>();
        var backupStarted = false;
        var validations = new Dictionary<string, (ILocalizationAdapter Adapter, string Original, Dictionary<string, string> Translations)>();
        try
        {
            // Prevalidate every selected file, including off-page edits, before changing any game file.
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var path = BackupService.Resolve(game.Path, file.FilePath);
                var snapshot = await TextFiles.ReadAsync(path, ct);
                if (snapshot.Hash != file.SourceHash) throw new IOException("Файл изменился после анализа: " + file.FilePath);
                var rows = await repository.FileRowsAsync(session, file.FilePath, skipEmpty, ct);
                foreach (var row in rows)
                    if (!new TranslationValidator().Validate(row.Original, row.Translation, out var error)) throw new InvalidDataException(error);
                var resolved = new ActiveLocalizationResolver().Resolve(path, snapshot.Text, adapters, configuration);
                foreach (var row in rows)
                    if (row.PhysicalSourceFile.Length > 0 && !Path.GetFullPath(row.PhysicalSourceFile).Equals(path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Physical source file mismatch");
                var adapter = LocalizationAdapterSelector.Select(adapters, path, snapshot.Text); var translations = rows.ToDictionary(r => r.Key, r => r.Translation);
                var modified = adapter.ApplyTranslations(snapshot.Text, translations);
                if (!adapter.Validate(snapshot.Text, modified, translations)) throw new InvalidDataException("Structure validation failed: " + file.FilePath);
                var bytes = snapshot.Encode(modified);
                if (bytes.Length > TextFiles.MaxBytes) throw new IOException("Переведённый файл превышает лимит 4 MiB: " + file.FilePath);
                var stagedPath = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                await File.WriteAllBytesAsync(stagedPath, bytes, ct);
                var verified = await TextFiles.ReadAsync(stagedPath, ct);
                if (!adapter.Validate(snapshot.Text, verified.Text, translations)) throw new InvalidDataException("Encoded structure validation failed");
                prepared.Add(new(file.FilePath, file.SourceHash, bytes) { ApplyMode = mode, AdapterType = resolved.AdapterType, LocalizationSlot = resolved.LocalizationSlot, SelectedEntryIds = rows.Select(r => r.Key).ToList(), EntryCategories = rows.ToDictionary(r => r.Key, r => r.Category.ToString()) });
                validations.Add(file.FilePath, (adapter, snapshot.Text, translations));
            }
            backupStarted = true;
            await backup.ApplyAsync(game.Path, prepared, ct, selection);
            foreach (var change in prepared)
            {
                var written = await TextFiles.ReadAsync(BackupService.Resolve(game.Path, change.RelativePath), ct);
                if (written.Hash != TextFiles.Hash(change.Content)) throw new IOException("Applied SHA256 mismatch");
                var validation = validations[change.RelativePath];
                if (!validation.Adapter.Validate(validation.Original, written.Text, validation.Translations)) throw new InvalidDataException("Applied parser validation failed");
            }
            await RefreshAppliedStampsAsync(game,session,true,ct);
            return files.Length;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { if (!backupStarted) await backup.RecordPreparationFailureAsync(game.Path, e, selection); throw new IOException(backup.LastDiagnosticReport?.Summary ?? e.Message, e); }
        finally { Directory.Delete(staging, true); }
    }
}

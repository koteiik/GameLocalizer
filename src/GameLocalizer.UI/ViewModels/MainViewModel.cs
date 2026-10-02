using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.Infrastructure.TranslationProviders;
using GameLocalizer.UI.Commands;
using GameLocalizer.UI.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameLocalizer.UI.ViewModels;

public sealed partial class MainViewModel : Observable
{
    private readonly IGameDiscoveryService discovery;
    private readonly IEngineDetector detector;
    private readonly ResourceScanner scanner;
    private readonly ScanResultRepository repository;
    private readonly ScanWorkspaceService workspace;
    private readonly BackupService backup;
    private readonly SettingsService settingsService;
    private readonly UpdateService updater;
    private readonly ILogger<MainViewModel> logger;
    private readonly OfflineSettingsViewModel? offlineSettings;
    private readonly GlossaryService? glossary;
    private readonly Dictionary<string, Game> sessionGames = [];
    private readonly SemaphoreSlim editGate = new(1, 1);
    private readonly Dictionary<(string Session, long Id), ScanEdit> edits = [];
    private CancellationTokenSource? cancellation, pageCancellation, editCancellation;
    private long selectionVersion, operationVersion, pageVersion;
    private Game? game;
    private string? session;
    private bool busy;
    private string status = "Готово", engine = "Unknown", search = "", fileFilter = "", mode = "Все", progressText = "";
    private double minimumConfidence = .6;
    private string filter = "Все";
    private long userTextCount, doubtfulCount, technicalCount;
    private int pageIndex;
    private long totalCount, matchingCount, selectedCount;
    private ScanSort sort;
    private bool descending;
    private sealed record Operation(Game? Game, long SelectionVersion, long OperationVersion, string? Session);
    public ObservableCollection<Game> Games { get; } = [];
    public ObservableCollection<Resource> Resources { get; } = [];
    public ObservableCollection<TranslationRow> Rows { get; } = [];
    public AppSettings Settings { get; }
    public string WindowTitle => "GameLocalizer " + ApplicationVersion.Label;
    public string VersionDescription => ApplicationVersion.Label + " · Русская локализация игр · Ранний MVP";
    public string[] Modes { get; } = ["Все", "Ошибки перевода", .. Enum.GetNames<TranslationStatus>()];
    public string[] Filters { get; } = ["Все", "Выбранные", "Высокая уверенность", "Сомнительные", "Технические", "UI", "Short UI", "Localization", "Unsupported UI", "Пропущенные UI", "Пустые переводы"];
    public string Filter { get => filter; set { if (filter == value) return; Set(ref filter, value); PreviewTabIndex = value == "Unsupported UI" ? 2 : 0; FilterChanged(); } }
    public ScanSort[] Sorts { get; } = Enum.GetValues<ScanSort>();
    public Task PreviewTask { get; private set; } = Task.CompletedTask;
    public Task EditSaveTask { get; private set; } = Task.CompletedTask;
    public Game? SelectedGame
    {
        get => game;
        set
        {
            if (ReferenceEquals(game, value)) return;
            selectionVersion++;
            cancellation?.Cancel();
            pageCancellation?.Cancel();
            EditSaveTask = SaveEditsSafelyAsync();
            Set(ref game, value);
            RefreshDiagnosticReport(); RefreshCollector();
            session = null; applyResult=null;
            ClearPreview();
            Resources.Clear(); UnsupportedUi.Clear(); UiSearchResults.Clear(); applyNeedsAnalysis = false; uiFound = shortUiFound = uiSelected = missedUi = 0; Changed(nameof(CoverageSummary));
            Engine = value?.Engine ?? "Unknown";
            analysisLoading = false;
            hasAnalysis = false; Changed(nameof(FindButtonLabel));
            Status = value == null ? "Выберите игру" : "Выбрана игра: " + value.Name;
            ProgressText = "";
            Changed(nameof(HasGame));
            CommandManager.InvalidateRequerySuggested();
            if (value != null)
            {
                AnalysisLoadTask = repository.Persistent ? LoadCachedAnalysisAsync(value, selectionVersion) : Task.CompletedTask;
                try
                {
                    if (backup.IsCleanupInterrupted(value.Path))
                    {
                        Status = "CleanupInterrupted: очистка была прервана. Нажмите «Убрать», чтобы продолжить.";
                        var recovery = new ChoiceWindow("Очистка прервана", Status, "Продолжить очистку", "Отмена") { Owner = Application.Current?.MainWindow };
                        if (recovery.ShowDialog() == true) _ = CleanupSelectedAsync();
                    }
                }
                catch (IOException e) { Status = e.Message; }
            }
        }
    }
    public bool HasGame => game != null;
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(Idle)); CommandManager.InvalidateRequerySuggested(); } }
    public bool Idle => !Busy && !Updates.Busy && !analysisLoading;
    public string Status { get => status; private set { Set(ref status, value); Changed(nameof(StatusSummary)); } }
    public string Engine { get => engine; private set { Set(ref engine, value); Changed(nameof(EngineSummary)); } }
    public string ProgressText { get => progressText; private set { Set(ref progressText, value); Changed(nameof(ProgressSummary)); } }
    public string Search { get => search; set { if (search == value) return; Set(ref search, value); FilterChanged(); } }
    public string FileFilter { get => fileFilter; set { if (fileFilter == value) return; Set(ref fileFilter, value); FilterChanged(); } }
    public bool ShowingTranslationErrors => Mode == "Ошибки перевода";
    public string Mode { get => mode; set { if (mode == value) return; Set(ref mode, value); Changed(nameof(ShowingTranslationErrors)); FilterChanged(); } }
    public double MinimumConfidence { get => minimumConfidence; set { if (minimumConfidence == value) return; Set(ref minimumConfidence, value); FilterChanged(); } }
    public ScanSort Sort { get => sort; set { Set(ref sort, value); FilterChanged(); } }
    public bool Descending { get => descending; set { Set(ref descending, value); FilterChanged(); } }
    public long TotalCount { get => totalCount; private set { Set(ref totalCount, value); Changed(nameof(Counters)); } }
    public long MatchingCount { get => matchingCount; private set { Set(ref matchingCount, value); Changed(nameof(PageSummary)); CommandManager.InvalidateRequerySuggested(); } }
    public long SelectedCount { get => selectedCount; private set { Set(ref selectedCount, value); Changed(nameof(Counters)); CommandManager.InvalidateRequerySuggested(); } }
    public int PageIndex => pageIndex;
    public string Counters => $"Найдено всего: {TotalCount:N0}   ·   Пользовательский текст: {userTextCount:N0}   ·   Сомнительные: {doubtfulCount:N0}   ·   Технические: {technicalCount:N0}   ·   Выбрано: {SelectedCount:N0}";
    public string PageSummary => MatchingCount == 0 ? "Нет совпадений" : $"{pageIndex * ScanResultRepository.PageSize + 1:N0}–{pageIndex * ScanResultRepository.PageSize + Rows.Count:N0} из {MatchingCount:N0}";
    public ICommand InitializeCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand TranslateCommand { get; }
    public ICommand OpenDiagnosticReportCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand CleanupCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SettingsCommand { get; }
    public UpdateViewModel Updates { get; }
    public ICommand AboutCommand { get; }
    public ICommand IssuesCommand { get; }
    public ICommand SelectVisibleCommand { get; }
    public ICommand ClearVisibleCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PreviousPageCommand { get; }

    public MainViewModel(IGameDiscoveryService discovery, IEngineDetector detector, ResourceScanner scanner,
        ScanResultRepository repository, ScanWorkspaceService workspace, BackupService backup,
        SettingsService settingsService, UpdateService updater, ILogger<MainViewModel> logger, AppSettings? applicationSettings = null, OfflineSettingsViewModel? offlineSettings = null, GlossaryService? glossary = null, ApplyDiagnosticReportLocator? diagnosticReports = null, Func<ApplySummary, ApplySummaryDecision>? applySummaryChoice = null, UiResourceDiscovery? uiDiscovery = null, ScanDiagnosticLog? scanDiagnostics = null, GameLocalizer.Infrastructure.Runtime.RuntimeDictionaryService? runtimeDictionary = null)
    {
        this.runtimeDictionary = runtimeDictionary;
        this.scanDiagnostics = scanDiagnostics ?? new(); this.uiDiscovery = uiDiscovery ?? workspace.CreateUiDiscovery(this.scanDiagnostics);
        this.applySummaryChoice = applySummaryChoice;
        this.diagnosticReports = diagnosticReports ?? new();
        this.discovery = discovery; this.detector = detector; this.scanner = scanner; this.repository = repository;
        this.workspace = workspace; this.backup = backup; this.settingsService = settingsService; this.updater = updater; this.logger = logger;
        applyStates = new ApplyStateStore(Path.Combine(settingsService.DataDirectory,"ApplyStates"));
        Settings = applicationSettings ?? settingsService.Load(); this.offlineSettings = offlineSettings; this.glossary = glossary;
        var installation = new InstallationInfoService().GetInfo();
        Updates = new UpdateViewModel(new ReleaseClient(installed: installation.Installed), () => Busy || offlineSettings?.Busy == true, async () => { await ShutdownAsync(); workspace.UnloadModel(); }, installation);
        Updates.PropertyChanged += (_, _) => { Changed(nameof(Idle)); CommandManager.InvalidateRequerySuggested(); };
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Busy)) Updates.RefreshAvailability(); };
        if (offlineSettings != null) offlineSettings.PropertyChanged += (_, _) => { Changed(nameof(ProviderDescription)); Updates.RefreshAvailability(); };
        foreach (var manual in Settings.ManualGames.Where(g => Directory.Exists(g.Path))) Games.Add(manual);
        InitializeCollectorCommands();
        InitializeCommand = new AsyncCommand(RefreshGamesAsync, () => Idle);
        AddCommand = new RelayCommand(AddFolder, () => Idle);
        AnalyzeCommand = new AsyncCommand(AnalyzeSelectedAsync, () => Idle && HasGame);
        FindCommand = new AsyncCommand(FindSelectedAsync, () => Idle && HasGame);
        RefreshAnalysisCommand = new AsyncCommand(FindSelectedAsync, () => Idle && HasGame);
        FullRescanCommand = new AsyncCommand(() => FindSelectedAsync(true), () => Idle && HasGame);
        ForgetAnalysisCommand = new AsyncCommand(() => ForgetAnalysisAsync(MessageBox.Show("Забыть только анализ выбранной игры? Переводы в памяти, модели и резервные копии сохранятся.","Забыть анализ игры",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes), () => Idle && HasGame);
        TranslateCommand = new AsyncCommand(() => StartTranslationInteractiveAsync(false), () => Idle && session != null && SelectedCount > 0);
        RetranslateCommand = new AsyncCommand(() => StartTranslationInteractiveAsync(true), () => Idle && session != null && SelectedCount > 0);
        OpenScanLogCommand = new RelayCommand(OpenScanLog, () => ScanDiagnosticLogPath != null && File.Exists(ScanDiagnosticLogPath));
        OpenDiagnosticReportCommand = new RelayCommand(OpenDiagnosticReport, () => diagnosticReportPath != null);
        SearchUiResourcesCommand = new AsyncCommand(SearchUiResourcesAsync, () => Idle && HasGame && !string.IsNullOrWhiteSpace(UiSearchTerm));
        ApplyCommand = new AsyncCommand(ApplySelectedAsync, () => Idle && session != null && SelectedCount > 0 && !applyNeedsAnalysis);
        RestoreCommand = new AsyncCommand(() => Run(async (op, ct) =>
        {
            if (op.Game == null || MessageBox.Show("Закройте игру. Восстановить все сохранённые оригиналы?", "Восстановление", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await Task.Run(() => backup.RestoreAsync(op.Game.Path, ct), ct);
            await UpdateCacheAfterRestoreAsync(op.Game,ct);
            if (IsCurrent(op)) { await RefreshPreviewAsync(); Status = "Оригинальные файлы восстановлены. Кэш анализа и переводы сохранены."; }
        }), () => Idle && HasGame);
        CleanupCommand = new AsyncCommand(CleanupSelectedAsync, () => Idle && HasGame);
        CancelCommand = new RelayCommand(Cancel, () => Busy);
        SettingsCommand = new RelayCommand(() =>
        {
            var profile = workspace.Profile; workspace.UnloadModel();
            new SettingsWindow(Settings, offlineSettings, glossary, resetPanel: () => PanelLayout.Reset()) { Owner = Application.Current.MainWindow }.ShowDialog();
            if (profile != workspace.Profile) { session = null; ClearPreview(); Status = "Настройки переводчика/glossary изменены. Нажмите «Найти текст»: ручные переводы сохраняются."; }
            Changed(nameof(ProviderDescription));
            try { settingsService.Save(Settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = e.Message; }
        }, () => Idle);
        AboutCommand = new RelayCommand(() => new AboutWindow(Settings.GitHubRepository, Updates) { Owner = Application.Current.MainWindow }.ShowDialog());
        IssuesCommand = new RelayCommand(() => Open($"https://github.com/{Settings.GitHubRepository}/issues"));
        SelectVisibleCommand = new RelayCommand(() => { foreach (var row in Rows) row.Selected = true; }, () => Idle);
        ClearVisibleCommand = new RelayCommand(() => { foreach (var row in Rows) row.Selected = false; }, () => Idle);
        NextPageCommand = new AsyncCommand(() => ChangePageAsync(pageIndex + 1), () => Idle && ((long)pageIndex + 1) * ScanResultRepository.PageSize < MatchingCount);
        PreviousPageCommand = new AsyncCommand(() => ChangePageAsync(pageIndex - 1), () => Idle && pageIndex > 0);
        InitializeShell();
    }
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку игры" };
        if (dialog.ShowDialog() != true) return;
        var path = Path.GetFullPath(dialog.FolderName);
        var existing = Games.FirstOrDefault(g => SamePath(g.Path, path));
        if (existing != null) { SelectedGame = existing; return; }
        var added = new Game("manual:" + TranslationMemoryService.Hash(path.ToUpperInvariant()), Path.GetFileName(path), path, "Manual");
        Games.Add(added); SelectedGame = added; Settings.ManualGames.Add(added);
        Changed(nameof(ProviderDescription));
            try { settingsService.Save(Settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "Не удалось сохранить список папок"; }
    }
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    public Task RefreshGamesAsync() => Run(async (op, ct) =>
    {
        if (offlineSettings != null) await offlineSettings.InitializeAsync();
        ct.ThrowIfCancellationRequested();
        var found = await discovery.DiscoverAsync(ct);
        ct.ThrowIfCancellationRequested();
        // Preserve existing object references, especially the selected instance, across refreshes.
        foreach (var item in found) if (Games.All(g => !SamePath(g.Path, item.Path))) Games.Add(item);
        if (IsCurrent(op)) Status = $"В библиотеке игр: {Games.Count}. Выбор сохранён.";
        if (Settings.CheckUpdatesOnStartup) await Updates.CheckAsync(ct);
    });
    public void Cancel() => cancellation?.Cancel();
    private bool IsCurrent(Operation op) => op.SelectionVersion == selectionVersion && op.OperationVersion == operationVersion && ReferenceEquals(op.Game, game);
    private async Task Run(Func<Operation, CancellationToken, Task> action, bool isScan = false)
    {
        if (!Idle) return;
        var op = new Operation(game, selectionVersion, ++operationVersion, session);
        using var source = new CancellationTokenSource(); cancellation = source; Busy = true; Status = "Выполняется…";
        try { await action(op, source.Token); }
        catch (OperationCanceledException) { if (IsCurrent(op)) Status = "Отменено. Уже найденные строки доступны; применённые файлы можно восстановить."; }
        catch (Exception e)
        {
            logger.LogError(e, "Operation failed");
            if (isScan) { scanDiagnostics.Record(op.Game?.Path ?? Environment.CurrentDirectory, "Main supported text scan", e); Changed(nameof(ScanDiagnosticLogPath)); ScanStatus = "FAILED"; }
            if (IsCurrent(op)) Status = isScan ? "Поиск текста завершился с ошибкой. Подробности: «Открыть журнал»." : "Ошибка: " + e.Message;
        }
        finally
        {
            if (source.IsCancellationRequested) workspace.UnloadModel();
            var current = IsCurrent(op);
            operationVersion++; cancellation = null; Busy = false;
            if (current && session != null) { PreviewTask = RefreshPreviewAsync(); await PreviewTask; }
        }
    }
    public async Task AnalyzeSelectedAsync() { await AnalysisLoadTask; await Run((op, ct) => Analyze(op, ct), true); }
    private async Task Analyze(Operation op, CancellationToken ct, bool includeOptional = true)
    {
        if (op.Game == null) return;
        await FlushEditsAsync(); ct.ThrowIfCancellationRequested();
        if (!IsCurrent(op)) return;
        // Preserve the current text session until replacement scan stages succeed.
        var result = await Task.Run(() =>
        {
            var detected = detector.Detect(op.Game.Path, ct);
            var catalog = new PriorityQueue<Resource, int>();
            long count = 0;
            foreach (var resource in scanner.Enumerate(op.Game.Path, ct))
            {
                count++; catalog.Enqueue(resource, -(int)resource.Kind);
                if (catalog.Count > ScanResultRepository.PageSize) catalog.Dequeue();
            }
            return (detected, count, resources: catalog.UnorderedItems.Select(i => i.Element).OrderBy(r => r.Kind).ToArray());
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (!IsCurrent(op)) return;
        Engine = $"{result.detected.EngineType} · {result.detected.Confidence:P0} · {string.Join(", ", result.detected.DetectedEvidence)}";
        op.Game.Engine = result.detected.EngineType.ToString(); op.Game.Status = "Анализ завершён";
        op.Game.EngineConfidence = result.detected.Confidence;
        Resources.Clear(); foreach (var resource in result.resources) Resources.Add(resource);
        if (includeOptional) await RefreshUnityDiscoveryAsync(op, ct);
        if (!IsCurrent(op)) return;
        if (includeOptional)
        {
            if (await repository.LoadSnapshotAsync(op.Game.Path,ct) == null) await repository.CommitSnapshotAsync(op.Game,Guid.NewGuid().ToString("N"),Engine,result.detected.Confidence,[],ct);
            else await repository.UpdateAnalysisEngineAsync(op.Game.Path,Engine,result.detected.Confidence,ct);
        }
        ScanStatus = DiscoveryErrorCount > 0 ? "PARTIAL" : "SUCCESS";
        Status = $"Просмотрено файлов: {result.count:N0}. Показано ресурсов: {Resources.Count:N0}. Нажмите «Найти текст».";
        logger.LogInformation("Analysis: {Engine}, {Count} files", result.detected.EngineType, result.count);
    }
    public async Task FindSelectedAsync() { await AnalysisLoadTask; await FindSelectedAsync(false); }
    public Task FindSelectedAsync(bool full) => Run(async (op, ct) =>
    {
        if (op.Game == null) return;
        if (full || session == null) await Analyze(op, ct, false);
        ct.ThrowIfCancellationRequested(); if (!IsCurrent(op)) return;
        var activeSession = Guid.NewGuid().ToString("N");
        Status = "Сканирование текстовых ресурсов…";

        var progress = new Progress<ScanProgress>(p =>
        {
            if (!IsCurrent(op)) return;
            ProgressText = $"Основной поиск: обработано {p.Processed:N0} строк · файлов {p.FilesVisited:N0}";
        });
        var final = await Task.Run(() => workspace.RefreshAnalysisAsync(op.Game, activeSession, Engine, op.Game.EngineConfidence, full, progress, ct), ct);
        if (!IsCurrent(op)) return;
        session = activeSession; sessionGames[activeSession] = op.Game; applyNeedsAnalysis = false; ClearPreview();
        hasAnalysis=true;Changed(nameof(FindButtonLabel));
        ShowScanProgress(final); await RefreshPreviewAsync();
        if (workspace.LastValidFilesRescanned > 0 || full) await RefreshUnityDiscoveryAsync(op, ct); if (!IsCurrent(op)) return;
        ScanStatus = DiscoveryErrorCount > 0 || workspace.ScanErrorCount > 0 ? "PARTIAL" : "SUCCESS";
        op.Game.Status = $"Строк: {final.Candidates:N0}";
        Status = $"Найдено {final.Candidates:N0} строк. Перечитано файлов: {workspace.LastFilesRescanned}; из кэша: {workspace.LastFilesReused}." + (DiscoveryErrorCount > 0 ? $" Не удалось исследовать {DiscoveryErrorCount} Unity-ресурс. Основной поиск текста продолжен." : "") + $"\nScan status: {ScanStatus} · Unsupported Unity resources: {UnsupportedUnityResourceCount} · Discovery errors: {DiscoveryErrorCount} · Пропущено text файлов: {final.SkippedFiles:N0}";
        logger.LogInformation("Scan completed: {Files} files, {Rows} candidates, {Processed} processed", final.FilesVisited, final.Candidates, final.Processed);
    }, true);
    private void ShowScanProgress(ScanProgress p)
    {
        TotalCount = p.Candidates;
        SelectedCount = p.Selected;
        ProgressText = $"Просмотрено файлов: {p.FilesVisited:N0} · Потенциальных строк: {p.Candidates:N0} · Обработано строк: {p.Processed:N0}";
    }
    public Task TranslateSelectedAsync() => Run(async (op, ct) =>
    {
        if (op.Game == null || op.Session == null) return;
        await FlushEditsAsync(); ct.ThrowIfCancellationRequested();
        await ExecuteTranslationJobAsync(op, false, false, false, ct);
    });
    private ScanQuery Query() => new(Search, FileFilter, Mode, MinimumConfidence, Sort, Descending, Filter);
    private void ClearPreview()
    {
        pageVersion++; Rows.Clear(); pageIndex = 0; TotalCount = MatchingCount = SelectedCount = 0; userTextCount = doubtfulCount = technicalCount = 0;
        Changed(nameof(Counters)); Changed(nameof(PageSummary)); Changed(nameof(PageIndex));
        translationReady = false; translationApplied = false; currentTranslationJob = null; NotifyTranslationJob();
    }
    private void FilterChanged() { pageIndex = 0; PreviewTask = RefreshPreviewAsync(true); }
    public Task ChangePageAsync(int index)
    {
        pageIndex = Math.Max(0, index); Changed(nameof(PageIndex));
        return PreviewTask = RefreshPreviewAsync();
    }
    public async Task RefreshPreviewAsync(bool debounce = false)
    {
        pageCancellation?.Cancel(); pageCancellation?.Dispose();
        using var source = new CancellationTokenSource(); pageCancellation = source;
        var token = source.Token; var request = ++pageVersion; var activeSession = session; var version = selectionVersion;
        var query = Query(); var page = pageIndex;
        if (activeSession == null) { pageCancellation = null; return; }
        try
        {
            if (debounce) await Task.Delay(200, token);
            await FlushEditsAsync(); token.ThrowIfCancellationRequested();
            var result = await repository.QueryAsync(activeSession, query, page, token);
            if (request != pageVersion || version != selectionVersion || session != activeSession) return;
            if(currentTranslationJob == null && game != null) {
                var saved = workspace.FindLatestJob(game);
                if(saved != null && (saved.SessionId == activeSession || saved.SessionId == "")) currentTranslationJob = saved;
            }
            Rows.Clear();
            foreach (var item in result.Rows)
            {
                var error = currentTranslationJob?.SessionId == activeSession ? currentTranslationJob.Errors.FirstOrDefault(e => e.RowId == item.Id) : null;
                var row = new TranslationRow { ErrorType = error?.ErrorType ?? (item.Status is "Failed" or "ValidationError" ? item.Status : ""), ErrorMessage = error?.Message ?? (item.Status is "Failed" or "ValidationError" ? "Причина не сохранена в предыдущем задании" : ""), RetryStatus = error?.RetryStatus ?? (item.Status is "Failed" or "ValidationError" ? "Ожидает повтора" : ""), Id = item.Id, Original = item.Original, File = item.FilePath, PhysicalSourceFile = item.PhysicalSourceFile.Length > 0 ? item.PhysicalSourceFile : Path.Combine(game!.Path, item.FilePath), LocalizationSlot = item.LocalizationSlot, Key = item.DisplayKey, Context = item.Context,
                    Category = item.Category, Confidence = item.Confidence, Applied = item.Applied, Selected = item.Selected, Russian = item.Translation, State = Enum.TryParse<TranslationStatus>(item.Status, out var state) ? state : TranslationStatus.NotTranslated };
                var lastSelected = row.Selected;
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is not (nameof(TranslationRow.Selected) or nameof(TranslationRow.Russian))) return;
                    edits[(activeSession, row.Id)] = new(row.Id, row.Russian, row.Selected, Enum.Parse<TranslationStatus>(row.Status));
                    if (session == activeSession && row.Selected != lastSelected) SelectedCount += row.Selected ? 1 : -1;
                    lastSelected = row.Selected;
                    ScheduleEditSave();
                };
                Rows.Add(row);
            }
            uiFound = result.Ui; shortUiFound = result.ShortUi; uiSelected = result.UiSelected; missedUi = result.MissedUi; Changed(nameof(CoverageSummary));
            userTextCount = result.UserText; doubtfulCount = result.Doubtful; technicalCount = result.Technical;
            TotalCount = Busy ? Math.Max(TotalCount, result.Total) : result.Total;
            MatchingCount = result.Matching; SelectedCount = Busy ? Math.Max(SelectedCount, result.Selected) : result.Selected;
            Changed(nameof(Counters)); Changed(nameof(PageSummary)); Changed(nameof(PageIndex));
            await RefreshShellAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested && request == pageVersion && version == selectionVersion) Status = "Ошибка чтения предпросмотра: " + e.Message;
        }
        finally { if (ReferenceEquals(pageCancellation, source)) pageCancellation = null; }
    }
    private void ScheduleEditSave()
    {
        editCancellation?.Cancel(); editCancellation?.Dispose(); editCancellation = new();
        EditSaveTask = SaveEditsSafelyAsync(editCancellation.Token);
    }
    private async Task SaveEditsSafelyAsync(CancellationToken delay = default)
    {
        var version = selectionVersion;
        try { if (delay.CanBeCanceled) await Task.Delay(250, delay); await FlushEditsAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception e) { logger.LogError("Preview save failed: {Type}", e.GetType().Name); if (version == selectionVersion) Status = "Не удалось сохранить правки: " + e.Message; }
    }
    public async Task FlushEditsAsync()
    {
        await editGate.WaitAsync();
        try
        {
            var pending = edits.ToArray();
            foreach (var group in pending.GroupBy(p => p.Key.Session))
            {
                var changes = group.Select(p => p.Value).ToArray();
                if (sessionGames.TryGetValue(group.Key, out var owner)) await workspace.SaveManualEditsAsync(owner, group.Key, changes, CancellationToken.None);
                await repository.SaveEditsAsync(group.Key, changes, CancellationToken.None);
            }
            foreach (var item in pending) if (edits.TryGetValue(item.Key, out var current) && current == item.Value) edits.Remove(item.Key);
        }
        finally { editGate.Release(); }
        await RefreshShellAsync();
    }
    public async Task ShutdownAsync()
    {
        applyWatch?.Stop();
        pageCancellation?.Cancel(); editCancellation?.Cancel();
        await RuntimeEditSaveTask; await AnalysisLoadTask; await PreviewTask; await EditSaveTask; await FlushEditsAsync(); workspace.UnloadModel();
    }
    internal static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { MessageBox.Show("Не удалось открыть браузер."); }
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.TranslationProviders;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.Commands;
using GameLocalizer.UI.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
namespace GameLocalizer.UI.ViewModels;

public sealed class MainViewModel : Observable
{
    private readonly IGameDiscoveryService discovery;
    private readonly IEngineDetector detector;
    private readonly ResourceScanner scanner;
    private readonly ILocalizationAdapter[] adapters;
    private readonly BackupService backup;
    private readonly TranslationService translator;
    private readonly SettingsService settingsService;
    private readonly UpdateService updater;
    private readonly ILogger<MainViewModel> logger;
    private readonly Dictionary<string, TextFile> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
    private Game? game;
    private bool busy;
    private string status = "Готово", engine = "Unknown", search = "", fileFilter = "", mode = "Все";
    private double minimumConfidence = .5;
    public ObservableCollection<Game> Games { get; } = [];
    public ObservableCollection<Resource> Resources { get; } = [];
    public ObservableCollection<TranslationRow> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    public AppSettings Settings { get; }
    public string[] Modes { get; } = ["Все", "Не переведено", "Validation Error"];
    public Game? SelectedGame { get => game; set { if (Busy) return; Set(ref game, value); Rows.Clear(); Resources.Clear(); snapshots.Clear(); Engine = "Unknown"; Changed(nameof(HasGame)); CommandManager.InvalidateRequerySuggested(); } }
    public bool HasGame => SelectedGame != null;
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(Idle)); CommandManager.InvalidateRequerySuggested(); } }
    public bool Idle => !Busy;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Engine { get => engine; private set => Set(ref engine, value); }
    public string Search { get => search; set { Set(ref search, value); RowsView.Refresh(); } }
    public string FileFilter { get => fileFilter; set { Set(ref fileFilter, value); RowsView.Refresh(); } }
    public string Mode { get => mode; set { Set(ref mode, value); RowsView.Refresh(); } }
    public double MinimumConfidence { get => minimumConfidence; set { Set(ref minimumConfidence, value); RowsView.Refresh(); } }
    public ICommand InitializeCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand TranslateCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand IssuesCommand { get; }
    public ICommand SelectVisibleCommand { get; }
    public ICommand ClearVisibleCommand { get; }

    public MainViewModel(IGameDiscoveryService discovery, IEngineDetector detector, ResourceScanner scanner,
        IEnumerable<ILocalizationAdapter> adapters, BackupService backup, TranslationService translator,
        SettingsService settingsService, UpdateService updater, ILogger<MainViewModel> logger)
    {
        this.discovery = discovery; this.detector = detector; this.scanner = scanner; this.adapters = adapters.ToArray();
        this.backup = backup; this.translator = translator; this.settingsService = settingsService; this.updater = updater; this.logger = logger;
        Settings = settingsService.Load(); RowsView = CollectionViewSource.GetDefaultView(Rows);
        foreach (var manual in Settings.ManualGames.Where(g => Directory.Exists(g.Path))) Games.Add(manual);
        RowsView.Filter = obj => obj is TranslationRow row && row.Confidence >= MinimumConfidence &&
            (Mode == "Все" || row.Status == Mode) && row.File.Contains(FileFilter, StringComparison.OrdinalIgnoreCase) &&
            (row.Original.Contains(Search, StringComparison.OrdinalIgnoreCase) || row.Russian.Contains(Search, StringComparison.OrdinalIgnoreCase) || row.Key.Contains(Search, StringComparison.OrdinalIgnoreCase));
        InitializeCommand = new AsyncCommand(() => Run(async ct =>
        {
            foreach (var found in await discovery.DiscoverAsync(ct)) if (Games.All(g => g.Path != found.Path)) Games.Add(found);
            Status = $"Найдено игр: {Games.Count}. Можно добавить папку вручную.";
            if (Settings.CheckUpdatesOnStartup && await updater.CheckAsync(Settings.GitHubRepository, ct) is { } release)
            {
                var dialog = new UpdateWindow(release.Version) { Owner = Application.Current.MainWindow };
                if (dialog.ShowDialog() == true) Open(release.Url);
            }
        }), () => Idle);
        AddCommand = new RelayCommand(() =>
        {
            var dialog = new OpenFolderDialog { Title = "Выберите папку игры" };
            if (dialog.ShowDialog() != true) return;
            var path = Path.GetFullPath(dialog.FolderName);
            var existing = Games.FirstOrDefault(g => g.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { SelectedGame = existing; return; }
            var added = new Game("manual:" + Infrastructure.Database.TranslationMemoryService.Hash(path.ToUpperInvariant()), Path.GetFileName(path), path, "Manual");
            Games.Add(added); SelectedGame = added; Settings.ManualGames.Add(added);
            try { settingsService.Save(Settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "Не удалось сохранить список папок"; }
        }, () => Idle);
        AnalyzeCommand = new AsyncCommand(() => Run(Analyze), () => Idle && HasGame);
        FindCommand = new AsyncCommand(() => Run(Find), () => Idle && HasGame);
        TranslateCommand = new AsyncCommand(() => Run(Translate), () => Idle && Rows.Count > 0);
        ApplyCommand = new AsyncCommand(() => Run(Apply), () => Idle && Rows.Count > 0);
        RestoreCommand = new AsyncCommand(() => Run(async ct =>
        {
            if (MessageBox.Show("Закройте игру. Восстановить все сохранённые оригиналы?", "Восстановление", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await backup.RestoreAsync(SelectedGame!.Path, ct); Rows.Clear(); snapshots.Clear(); Status = "Оригинальные файлы восстановлены. Выполните повторный анализ.";
        }), () => Idle && HasGame);
        CancelCommand = new RelayCommand(Cancel, () => Busy);
        SettingsCommand = new RelayCommand(() =>
        {
            new SettingsWindow(Settings) { Owner = Application.Current.MainWindow }.ShowDialog();
            try { settingsService.Save(Settings); } catch (IOException e) { Status = e.Message; }
        }, () => Idle);
        AboutCommand = new RelayCommand(() => new AboutWindow(Settings.GitHubRepository) { Owner = Application.Current.MainWindow }.ShowDialog());
        IssuesCommand = new RelayCommand(() => OpenRepository("issues"));
        SelectVisibleCommand = new RelayCommand(() => { foreach (TranslationRow r in RowsView) r.Selected = true; }, () => Idle);
        ClearVisibleCommand = new RelayCommand(() => { foreach (TranslationRow r in RowsView) r.Selected = false; }, () => Idle);
    }
    public void Cancel() => cancellation?.Cancel();
    private async Task Run(Func<CancellationToken, Task> action)
    {
        Busy = true; cancellation = new(); Status = "Выполняется…";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "Отменено. Применённые файлы можно восстановить."; }
        catch (Exception e) { logger.LogError("Operation failed: {Type}", e.GetType().Name); Status = "Ошибка: " + e.Message; MessageBox.Show(Status, "GameLocalizer", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { cancellation.Dispose(); cancellation = null; Busy = false; RowsView.Refresh(); }
    }
    private async Task Analyze(CancellationToken ct)
    {
        Rows.Clear(); snapshots.Clear(); Resources.Clear();
        var result = await Task.Run(() => detector.Detect(SelectedGame!.Path, ct), ct);
        Engine = $"{result.EngineType} · {result.Confidence:P0} · {string.Join(", ", result.DetectedEvidence)}";
        SelectedGame!.Engine = result.EngineType.ToString(); SelectedGame.Status = "Анализ завершён";
        foreach (var resource in await scanner.ScanAsync(SelectedGame!.Path, ct)) Resources.Add(resource);
        Status = $"Ресурсов: {Resources.Count}; поддерживаются: {Resources.Count(r => r.Editable)}. Нажмите «Найти текст».";
        logger.LogInformation("Analysis: {Engine}, {Count} resources", result.EngineType, Resources.Count);
    }
    private async Task Find(CancellationToken ct)
    {
        if (Resources.Count == 0) await Analyze(ct);
        Rows.Clear(); snapshots.Clear(); var candidate = new TextCandidateDetector(); int skipped = 0;
        foreach (var resource in Resources.Where(r => r.Editable))
        {
            ct.ThrowIfCancellationRequested(); Status = "Извлечение: " + Path.GetFileName(resource.Path);
            try
            {
                var snapshot = await TextFiles.ReadAsync(resource.Path, ct); var adapter = adapters.First(a => a.CanHandle(resource.Path));
                var entries = await Task.Run(() => adapter.Extract(snapshot.Text), ct);
                var frequencies = entries.GroupBy(e => e.Text).ToDictionary(g => g.Key, g => g.Count());
                var relative = Path.GetRelativePath(SelectedGame!.Path, resource.Path); snapshots[relative] = snapshot;
                foreach (var entry in entries)
                {
                    var confidence = candidate.Score(entry.Text, frequencies[entry.Text]);
                    if (confidence < .35) continue;
                    if (Rows.Count >= 50000) throw new IOException("Лимит 50 000 строк. Выберите меньшую папку.");
                    Rows.Add(new() { Original = entry.Text, File = relative, Key = entry.Key, Context = entry.Context, Confidence = confidence, Selected = confidence >= .6 });
                    if (Rows.Count % 200 == 0) { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background); ct.ThrowIfCancellationRequested(); }
                }
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or System.Xml.XmlException or FormatException or System.Text.DecoderFallbackException or InvalidDataException)
            { skipped++; logger.LogWarning("Skipped text resource: {File}, {Type}", Path.GetFileName(resource.Path), e.GetType().Name); }
        }
        Status = $"Найдено строк: {Rows.Count}; пропущено неподдерживаемых файлов: {skipped}. Игра не изменена.";
        logger.LogInformation("Extracted {Count} rows, skipped {Skipped}", Rows.Count, skipped);
        SelectedGame!.Status = $"Строк: {Rows.Count}";
    }
    private async Task Translate(CancellationToken ct)
    {
        foreach (var group in Rows.Where(r => r.Selected && string.IsNullOrWhiteSpace(r.Russian)).GroupBy(r => r.File))
        {
            ct.ThrowIfCancellationRequested(); Status = "Mock-перевод: " + group.Key;
            var result = await Task.Run(() => translator.TranslateAsync(SelectedGame!, group.Key, group.Select(r => new TranslationItem(r.Key, r.Original, r.Context)).ToArray(), ct), ct);
            foreach (var row in group) row.Russian = result[row.Key];
        }
        Status = "Mock-перевод готов. [ДЕМО] означает отсутствие реального перевода. Проверьте текст вручную.";
    }
    private async Task Apply(CancellationToken ct)
    {
        var selected = Rows.Where(r => r.Selected).ToArray();
        if (selected.Length == 0) { Status = "Нет выбранных строк"; return; }
        if (selected.Any(r => r.Status != "Готово")) throw new InvalidDataException("Выбранные строки содержат пустые переводы или Validation Error.");
        if (MessageBox.Show($"Закройте игру. Применить {selected.Length} строк?\nБудет создана резервная копия. Проверьте выбранные строки, включая скрытые фильтром.", "Применить перевод", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var changes = await Task.Run(() =>
        {
            var list = new List<FileChange>();
            foreach (var group in selected.GroupBy(r => r.File))
            {
                ct.ThrowIfCancellationRequested(); var snapshot = snapshots[group.Key]; var adapter = adapters.First(a => a.CanHandle(group.Key));
                var translations = group.ToDictionary(r => r.Key, r => r.Russian);
                foreach (var row in group) if (!new TranslationValidator().Validate(row.Original, row.Russian, out var error)) throw new InvalidDataException(error);
                var modified = adapter.ApplyTranslations(snapshot.Text, translations);
                if (!adapter.Validate(snapshot.Text, modified, translations)) throw new InvalidDataException("Structure validation failed: " + group.Key);
                list.Add(new(group.Key, snapshot.Hash, snapshot.Encode(modified)));
            }
            return list;
        }, ct);
        await backup.ApplyAsync(SelectedGame!.Path, changes, ct);
        Rows.Clear(); snapshots.Clear(); Status = $"Изменено файлов: {changes.Count}. Резервные копии сохранены. Перед следующим переводом выполните анализ.";
    }
    internal static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { MessageBox.Show("Не удалось открыть браузер."); }
    }
    private void OpenRepository(string suffix)
    {
        if (!UpdateService.ValidRepository(Settings.GitHubRepository)) { MessageBox.Show("Адрес репозитория ещё не настроен. Укажите owner/GameLocalizer в настройках после публикации."); return; }
        Open($"https://github.com/{Settings.GitHubRepository}/{suffix}");
    }
}

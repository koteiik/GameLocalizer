using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using GameLocalizer.Core.Models;
using GameLocalizer.UI.Commands;
namespace GameLocalizer.UI.ViewModels;

// Presentation state only: navigation never changes the active game or editor filters.
public sealed partial class MainViewModel
{
    private string currentSection = "Библиотека", librarySearch = "", librarySort = "Недавняя активность";
    private bool libraryList;
    private readonly Dictionary<string, DateTime> libraryActivity = new(StringComparer.OrdinalIgnoreCase);
    public string CurrentSection { get => currentSection; set { if (currentSection == value) return; Set(ref currentSection, value); if(IsSettings) EnsureSettingsContent(); foreach (var name in new[] { nameof(IsLibrary), nameof(IsTranslation), nameof(IsReview), nameof(IsSettings), nameof(IsWorkspace) }) Changed(name); } }
    public bool IsLibrary { get => CurrentSection == "Библиотека"; set { if(value) CurrentSection = "Библиотека"; } }
    public bool IsTranslation { get => CurrentSection == "Перевод"; set { if(value) CurrentSection = "Перевод"; } }
    public bool IsReview { get => CurrentSection == "Проверка"; set { if(value) CurrentSection = "Проверка"; } }
    public bool IsSettings { get => CurrentSection == "Настройки"; set { if(value) CurrentSection = "Настройки"; } }
    public System.Windows.FrameworkElement? SettingsContent { get; private set; }
    private void EnsureSettingsContent()
    {
        if(SettingsContent != null) return;
        var profile = workspace.Profile;
        workspace.UnloadModel();
        var window = new Views.SettingsWindow(Settings, offlineSettings, glossary, () =>
        {
            if(profile != workspace.Profile) { session = null; ClearPreview(); Status = "Настройки переводчика/glossary изменены. Нажмите «Найти текст»: ручные переводы сохраняются."; }
            workspace.UnloadModel();
            try { settingsService.Save(Settings); } catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException) { Status = e.Message; }
            profile = workspace.Profile; Changed(nameof(ProviderDescription));
        }, resetPanel: () => PanelLayout.Reset());
        SettingsContent = (System.Windows.FrameworkElement)window.Content;
        window.Content = null;
        window.Close(); // The detached content lives in the main window; do not retain a hidden application window.
        Changed(nameof(SettingsContent));
    }
    public bool IsWorkspace => IsTranslation || IsReview;
    public ICollectionView LibraryGames { get; private set; } = null!;
    public string[] LibrarySorts { get; } = ["Недавняя активность", "Название", "Статус"];
    public string LibrarySearch { get => librarySearch; set { Set(ref librarySearch,value); LibraryGames.Refresh(); } }
    public string LibrarySort { get => librarySort; set { Set(ref librarySort,value); LibraryGames.Refresh(); } }
    public bool LibraryList { get => libraryList; set => Set(ref libraryList,value); }
    public ICommand OpenGameFolderCommand { get; private set; } = null!;
    private bool translationReady, translationApplied;
    private long shellRevision;
    public Task WorkflowRefreshTask { get; private set; } = Task.CompletedTask;
    public string LanguageSlots => "Языки (предпросмотр): " + (Rows.Any(r => !string.IsNullOrWhiteSpace(r.LocalizationSlot)) ? string.Join(", ", Rows.Select(r => r.LocalizationSlot).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()) : "не определены");
    public string AnalysisStage => session == null ? "Не готово" : applyNeedsAnalysis ? "Требует обновления" : "✓ Готово";
    public string TranslationStage => IsTranslating ? TranslationProgressText : currentTranslationJob?.Status == TranslationJobStatus.Cancelled ? "Перевод отменён" : HasTranslationIssues ? $"{(currentTranslationJob!.Successful > 0 ? "⚠ Завершено частично" : "Ошибка перевода")} · ошибок {currentTranslationJob.FailedStrings:N0} · пропущено {currentTranslationJob.SkippedStrings:N0}" : translationReady ? "✓ Готово" : session == null ? "Ожидает анализа" : "Требует перевода";
    public string ApplyStage => IsApplying ? "Применение…" : ApplyState == GameLocalizer.Infrastructure.FileSystem.GameApplyState.ApplyFailed ? "⚠ Ошибка применения" : ApplyState == GameLocalizer.Infrastructure.FileSystem.GameApplyState.ApplyPartiallyCompleted ? "⚠ Применено частично" : applyNeedsAnalysis ? "Требуется обновление" : translationApplied ? "✓ Применено" : applyNeedsAnalysis ? "Обновите анализ" : translationReady ? "Готово к применению" : "Ожидает перевода";
    public bool MainActionEnabled => Idle && (!translationApplied || applyNeedsAnalysis || RuntimeUi.Any(r => string.IsNullOrWhiteSpace(r.Russian)));
    public string MainActionLabel => IsApplying ? "Применение…" : HasApplyError && session != null && !applyNeedsAnalysis ? (ApplyState == GameLocalizer.Infrastructure.FileSystem.GameApplyState.ApplyPartiallyCompleted ? "Повторить ошибки применения" : "Повторить применение") : IsTranslating ? $"Перевод… {currentTranslationJob?.ProcessedCount ?? 0:N0}/{currentTranslationJob?.TotalStrings ?? SelectedCount:N0}" : session == null ? "Анализировать" : applyNeedsAnalysis || RuntimeUi.Any(r => string.IsNullOrWhiteSpace(r.Russian)) ? "Обновить перевод" : translationApplied ? "Перевод применён" : translationReady ? "Применить перевод" : "Перевести";
    public ICommand MainActionCommand => session == null || applyNeedsAnalysis ? FindCommand : RuntimeUi.Any(r => string.IsNullOrWhiteSpace(r.Russian)) ? TranslateRuntimeNewCommand : translationReady || HasApplyError ? ApplyCommand : TranslateCommand;
    private void InitializeShell()
    {
        var view = new ListCollectionView(Games);
        view.Filter = item => item is Game g && g.Name.Contains(LibrarySearch, StringComparison.OrdinalIgnoreCase);
        view.CustomSort = Comparer<Game>.Create((a,b) => LibrarySort == "Название" ? StringComparer.CurrentCultureIgnoreCase.Compare(a.Name,b.Name) : LibrarySort == "Статус" ? StringComparer.CurrentCultureIgnoreCase.Compare(a.Status,b.Status) : libraryActivity.GetValueOrDefault(b.Path).CompareTo(libraryActivity.GetValueOrDefault(a.Path)));
        LibraryGames = view;
        InitializeLibraryArtwork();
        OpenGameFolderCommand = new RelayCommand(() => { if(SelectedGame != null) Open(SelectedGame.Path); }, () => HasGame);
        PropertyChanged += (_,e) =>
        {
            if(e.PropertyName == nameof(SelectedGame) && SelectedGame != null) { libraryActivity[SelectedGame.Path] = DateTime.UtcNow; LibraryGames.Refresh(); }
            if(e.PropertyName is nameof(SelectedGame) or nameof(Idle) or nameof(Counters) or nameof(Status) or nameof(FindButtonLabel)) { if(e.PropertyName == nameof(SelectedGame)) { translationReady = false; translationApplied = false; currentTranslationJob = null; NotifyTranslationJob(); } NotifyWorkflow(); }
        };
        RuntimeUi.CollectionChanged += (_,_) => NotifyWorkflow();
    }
    private async Task RefreshShellAsync()
    {
        var revision = ++shellRevision;
        var active = session;
        if(active == null) {translationReady=false;translationApplied=false;NotifyApplyState();return;}
        try
        {
            var summary = await workspace.ApplySelectionAsync(active, CancellationToken.None);
            if(revision != shellRevision || active != session) return;
            translationReady = summary.AppliedEntries > 0 && summary.ValidationErrorEntries == 0;
            if(currentTranslationJob == null && SelectedGame != null) {
                var saved = workspace.FindLatestJob(SelectedGame);
                if(saved != null && (saved.SessionId == active || saved.SessionId == "")) currentTranslationJob = saved;
                NotifyTranslationJob();
            }
            if(translationReady)
            {
                long after = 0; var applied = true;
                while(true)
                {
                    var rows = await repository.ReadSelectedAsync(active, after, false, CancellationToken.None);
                    if(rows.Count == 0) break;
                    applied &= rows.Where(r => !string.IsNullOrWhiteSpace(r.Translation)).All(r => r.Applied); after = rows[^1].Id;
                }
                if(revision != shellRevision || active != session) return;
                translationApplied = applied;
            }
            if(SelectedGame != null) await LoadApplyResultAsync(SelectedGame,active,translationApplied,revision);
            if(revision != shellRevision || active != session)return;
            NotifyApplyState();
        }
        catch(Exception e) when(e is ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException or System.IO.IOException) { }
    }
    private void NotifyWorkflow()
    {
        foreach(var name in new[] { nameof(LanguageSlots), nameof(AnalysisStage), nameof(TranslationStage), nameof(ApplyStage), nameof(MainActionLabel), nameof(MainActionCommand), nameof(MainActionEnabled) }) Changed(name);
        CommandManager.InvalidateRequerySuggested();
    }
}

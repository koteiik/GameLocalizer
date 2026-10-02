using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.UI.Views;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    private readonly Func<ApplySelectionSummary, string>? emptyTranslationChoice;
    private readonly Func<string, bool>? confirmApply;
    private bool applyNeedsAnalysis;
    private long uiFound, shortUiFound, uiSelected, missedUi;
    private int previewTabIndex;
    public int PreviewTabIndex { get => previewTabIndex; set => Set(ref previewTabIndex, value); }
    private string uiTerm = "";
    public ObservableCollection<UnsupportedUiCandidate> UnsupportedUi { get; } = [];
    public ObservableCollection<UiResourceSearchHit> UiSearchResults { get; } = [];
    public ICommand SearchUiResourcesCommand { get; private set; } = null!;
    public string UiSearchTerm { get => uiTerm; set { Set(ref uiTerm, value); CommandManager.InvalidateRequerySuggested(); } }
    public string UiDiscoveryNotes => uiDiscovery.Limitations + $"\nUnsupported/binary containers: {uiDiscovery.Statistics.UnsupportedContainers} · Bounded files: {uiDiscovery.Statistics.BoundedFiles} · Discovery errors: {DiscoveryErrorCount}";
    public string CoverageSummary => $"UI strings found: {uiFound:N0} · Short UI: {shortUiFound:N0} · UI selected: {uiSelected:N0} · Unsupported UI candidates: {UnsupportedUi.Count:N0} · Technical skipped: {technicalCount:N0}\nFound user-visible candidates but not selected: {missedUi:N0}";
    public async Task<bool> ResolveEmptyTranslationChoiceAsync(ApplySelectionSummary summary, string choice)
    {
        if (summary.EmptyTranslationRows.Count == 0) return true;
        if (choice == "Показать")
        {
            Search = ""; FileFilter = ""; Mode = "Все"; MinimumConfidence = 0; Filter = "Пустые переводы"; pageIndex = 0;
            await RefreshPreviewAsync(); return false;
        }
        return choice == "Да";
    }
    public Task ApplySelectedAsync() => Run(async (op, ct) =>
    {
        if (op.Game == null || op.Session == null || applyNeedsAnalysis) return;
        await FlushEditsAsync(); ct.ThrowIfCancellationRequested();
        var selection = await workspace.ApplySelectionAsync(op.Session, ct);
        var skipEmpty = selection.EmptyTranslationRows.Count > 0;
        if (skipEmpty)
        {
            var choice = emptyTranslationChoice?.Invoke(selection);
            if (choice == null)
            {
                var dialog = new ChoiceWindow("Есть пустые строки", $"Среди выбранных строк есть пустые переводы: {selection.SkippedEmptyTranslations:N0}.\n\nПропустить их и применить остальные?", "Да", "Показать", "Нет") { Owner = Application.Current?.MainWindow };
                dialog.ShowDialog(); choice = dialog.Choice;
            }
            if (!await ResolveEmptyTranslationChoiceAsync(selection, choice)) return;
        }
        if (selection.ValidationErrorEntries > 0) { await workspace.RecordBlockedApplyAsync(op.Game, selection); RefreshDiagnosticReport(); throw new System.IO.InvalidDataException(selection.Description + "\nВыбранные строки содержат Validation Error; исправьте их перед Apply."); }
        var description = await workspace.ApplyDescriptionAsync(op.Session, ct, skipEmpty);
        var text = selection.Description + "\n\n" + description + "\n\nЗакройте игру. Применить перевод?\nСкрытые фильтром выбранные строки тоже включены. Будет создана резервная копия.";
        var accepted = confirmApply?.Invoke(text) ?? MessageBox.Show(text, "Применить перевод", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!accepted) return;
        workspace.DiagnosticApplyTrace = Settings.DiagnosticApplyTrace;
        try { await Task.Run(() => workspace.ApplyAsync(op.Game, op.Session, Settings.ApplyMode, skipEmpty, ct), ct); }
        finally { RefreshDiagnosticReport(); }
        if (IsCurrent(op))
        {
            applyNeedsAnalysis = false; CommandManager.InvalidateRequerySuggested();
            Status = $"Применено: {selection.AppliedEntries:N0} строк. Пропущено пустых: {selection.SkippedEmptyTranslations:N0}.\n" + workspace.DiagnosticSummary + "\nОригиналы сохранены; кэш анализа обновлён.";
        }
    });
    private Task SearchUiResourcesAsync() => Run(async (op, ct) =>
    {
        if (op.Game == null) return;
        var term = UiSearchTerm;
        var hits = await Task.Run(() => uiDiscovery.SearchAsync(op.Game.Path, term, ct), ct);
        if (!IsCurrent(op)) return;
        PreviewTabIndex = 3; UiSearchResults.Clear(); foreach (var hit in hits) UiSearchResults.Add(hit);
        Changed(nameof(DiscoveryErrorCount)); Changed(nameof(UnsupportedUnityResourceCount)); Changed(nameof(UiDiscoveryNotes)); Changed(nameof(ScanDiagnosticLogPath)); CommandManager.InvalidateRequerySuggested();
        Status = $"Found: {(hits.Count > 0 ? "YES" : "NO")} · Совпадений: {hits.Count:N0}\n" + UiDiscoveryNotes;
    });
}

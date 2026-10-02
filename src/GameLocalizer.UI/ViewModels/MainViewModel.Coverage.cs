using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.UI.Views;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
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
        if(selection.ValidationErrorEntries>0) {
            var error=new InvalidDataException("Blocking ValidationError");
            await workspace.RecordBlockedApplyAsync(op.Game,selection);
            applyResult=ResultFromReport(op.Game,op.Session,workspace.LastApplyReport,error,workspace.LastDiagnosticReportPath,null);
            await applyStates.SaveAsync(applyResult);RefreshDiagnosticReport();NotifyApplyState();Status=ApplyErrorSummary;return;
        }
        var files=await repository.SelectedFilesAsync(op.Session,skipEmpty,ct);
        var description=await workspace.ApplyDescriptionAsync(op.Session,ct,skipEmpty);
        var summary=new ApplySummary(selection,files.Count,selection.Description+"\n\n"+description,!Settings.SkipCombinedApplyWarning);
        ApplySummaryDecision decision;
        if(applySummaryChoice != null)decision=applySummaryChoice(summary);
        else {var dialog=new ApplySummaryWindow(summary){Owner=Application.Current?.MainWindow};dialog.ShowDialog();decision=dialog.Decision;}
        if(decision.Choice=="Показать пропущенные") {IsTranslation=true;await ResolveEmptyTranslationChoiceAsync(selection,"Показать");return;}
        if(decision.Choice!="Применить")return;
        if(decision.DontAskAgain){Settings.SkipCombinedApplyWarning=true;settingsService.Save(Settings);}
        var previous=applyResult?.State == GameApplyState.ApplyPartiallyCompleted ? applyResult : null;
        IReadOnlySet<string>? verified=null;
        if(previous != null) {
            if(await ApplyStateStore.VerifyFilesAsync(previous,ct)) {await workspace.VerifyApplyOwnershipAsync(op.Game,previous.VerifiedFiles,ct);verified=previous.VerifiedFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);}
            else previous=null;
        }
        applyResult=new(){Root=op.Game.Path,Session=op.Session,State=GameApplyState.Applying};
        NotifyApplyState();
        workspace.DiagnosticApplyTrace=Settings.DiagnosticApplyTrace;
        try {
            await applyStates.SaveAsync(applyResult);
            await Task.Run(()=>workspace.ApplyAsync(op.Game,op.Session,Settings.ApplyMode,skipEmpty,ct,verified),ct);
            var result=ResultFromReport(op.Game,op.Session,workspace.LastApplyReport,null,workspace.LastDiagnosticReportPath,previous);
            if(result.FilesApplied==0)throw new IOException("No verified apply output");
            if(!await ApplyStateStore.VerifyFilesAsync(result,ct))throw new IOException("Applied output verification failed");
            await workspace.VerifyApplyOwnershipAsync(op.Game,result.VerifiedFiles,ct);
            await applyStates.SaveAsync(result);
            if(IsCurrent(op)){applyResult=result;applyNeedsAnalysis=false;translationApplied=true;Status=$"Применено: {selection.AppliedEntries:N0} строк. Пропущено пустых: {selection.SkippedEmptyTranslations:N0}.";}
        }
        catch(Exception error) {
            var result=ResultFromReport(op.Game,op.Session,workspace.LastApplyReport,error,workspace.LastDiagnosticReportPath,previous);
            if(IsCurrent(op)){applyResult=result;translationApplied=false;Status=ApplyErrorSummary;}
            try {await applyStates.SaveAsync(result);} catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        finally {if(IsCurrent(op)){RefreshDiagnosticReport();NotifyApplyState();}}
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

using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLocalizer.Core.Models;
using GameLocalizer.UI.Commands;
using Microsoft.Extensions.Logging;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    public Task AnalysisLoadTask { get; private set; } = Task.CompletedTask;
    public ICommand RefreshAnalysisCommand { get; private set; } = null!;
    public ICommand FullRescanCommand { get; private set; } = null!;
    public ICommand ForgetAnalysisCommand { get; private set; } = null!;
    private bool analysisLoading;
    private bool hasAnalysis;
    public string FindButtonLabel => hasAnalysis ? "Обновить анализ" : "Найти текст";
    public async Task LoadCachedAnalysisAsync(Game selected,long version)
    {
        analysisLoading=true;Changed(nameof(Idle));CommandManager.InvalidateRequerySuggested();
        try
        {
            await EditSaveTask;
            var snapshot=await workspace.LoadAnalysisAsync(selected,CancellationToken.None);
            if(version!=selectionVersion || !ReferenceEquals(selected,game) || Busy || snapshot==null)return;
            var changes=await workspace.ValidateAnalysisAsync(snapshot,CancellationToken.None);
            if(version!=selectionVersion || !ReferenceEquals(selected,game) || Busy)return;
            session=snapshot.Session;sessionGames[session]=selected;Engine=snapshot.Engine;selected.EngineConfidence=snapshot.EngineConfidence;applyNeedsAnalysis=changes>0;
            hasAnalysis=true;Changed(nameof(FindButtonLabel));
            await RefreshPreviewAsync();
            var unsupported=await repository.LoadUiDiscoveryAsync(selected.Path,CancellationToken.None);
            if(version!=selectionVersion || !ReferenceEquals(selected,game))return;
            UnsupportedUi.Clear();foreach(var row in unsupported)UnsupportedUi.Add(row);Changed(nameof(CoverageSummary));
            ScanStatus=changes>0?"STALE":"CACHED";
            NotifyApplyState();
            Status=$"Кэш анализа загружен. Найдено строк: {TotalCount:N0} · Пользовательский текст: {userTextCount:N0} · Переведено: {snapshot.Translated:N0}\nПоследний анализ: {snapshot.Timestamp.LocalDateTime:g}. "+selected.Status;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            logger.LogWarning(e,"Analysis cache could not load for {Root}",selected.Path);
            if(version==selectionVersion)Status="Кэш анализа недоступен. Выполните «Найти текст». Подробности в журнале.";
        }
        finally { if(version==selectionVersion){analysisLoading=false;Changed(nameof(Idle));CommandManager.InvalidateRequerySuggested();} }
    }
    public Task ForgetAnalysisAsync(bool confirmed) => Run(async(op,ct)=>
    {
        if(!confirmed || op.Game==null)return;await FlushEditsAsync();await repository.ForgetSnapshotAsync(op.Game.Path,ct);
        if(IsCurrent(op)){session=null;hasAnalysis=false;Changed(nameof(FindButtonLabel));ClearPreview();Resources.Clear();UnsupportedUi.Clear();Status="Анализ игры забыт. Translation Memory, модели, настройки и резервные копии сохранены.";}
    });
    private async Task UpdateCacheAfterRestoreAsync(Game owner,CancellationToken ct)
    {
        var snapshot=await workspace.LoadAnalysisAsync(owner,ct);
        if(snapshot!=null)await workspace.RefreshAppliedStampsAsync(owner,snapshot.Session,false,ct);
        applyNeedsAnalysis=false; applyResult=null;
        await applyStates.SaveAsync(new(){Root=owner.Path,State=GameLocalizer.Infrastructure.FileSystem.GameApplyState.ReadyToApply});
        NotifyApplyState();
    }
}

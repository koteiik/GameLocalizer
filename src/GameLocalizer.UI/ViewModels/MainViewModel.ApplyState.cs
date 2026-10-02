using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.UI.Commands;
using GameLocalizer.UI.Views;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    private ApplyStateStore applyStates = null!;
    private GameApplyResult? applyResult;
    private readonly Func<ApplySummary,ApplySummaryDecision>? applySummaryChoice;
    public GameApplyState? ApplyState => HasApplyError ? applyResult!.State : applyNeedsAnalysis ? GameApplyState.NeedsUpdate : applyResult?.State ?? (translationReady ? GameApplyState.ReadyToApply : null);
    public bool IsApplying => applyResult?.State == GameApplyState.Applying;
    public bool HasApplyError => applyResult?.State is GameApplyState.ApplyFailed or GameApplyState.ApplyPartiallyCompleted;
    public string ApplyErrorSummary => HasApplyError ? "Не удалось применить перевод. " + applyResult!.Reason : "";
    public string ApplyFilesSummary => applyResult?.State == GameApplyState.ApplyPartiallyCompleted ? $"Применено файлов: {applyResult.FilesApplied:N0} · Ошибок: {applyResult.FilesFailed:N0}" : "";
    public ICommand ApplyDetailsCommand => applyDetailsCommand ??= new RelayCommand(() => MessageBox.Show(applyResult?.Details ?? "", "Подробности применения", MessageBoxButton.OK,MessageBoxImage.Information),()=>HasApplyError);
    private ICommand? applyDetailsCommand;
    private System.Windows.Threading.DispatcherTimer? applyWatch;
    private bool checkingApplied;
    public async Task CheckAppliedFilesAsync()
    {
        var owner=SelectedGame;var result=applyResult;
        if(checkingApplied || !Idle || owner == null || result?.State != GameApplyState.Applied || applyNeedsAnalysis)return;
        checkingApplied=true;
        try {
            var valid=await ApplyStateStore.VerifyFilesAsync(result,CancellationToken.None);
            if(valid)await workspace.VerifyApplyOwnershipAsync(owner,result.VerifiedFiles,CancellationToken.None);
            if(!ReferenceEquals(owner,SelectedGame) || !ReferenceEquals(result,applyResult))return;
            if(!valid){translationApplied=false;applyNeedsAnalysis=true;Status="Файлы изменены после применения. Требуется обновление.";NotifyApplyState();}
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) {if(ReferenceEquals(owner,SelectedGame)&&ReferenceEquals(result,applyResult)){translationApplied=false;applyNeedsAnalysis=true;Status="Проверка применённых файлов не прошла. Требуется обновление.";NotifyApplyState();}}
        finally {checkingApplied=false;}
    }
    private void NotifyApplyState()
    {
        foreach(var name in new[]{nameof(ApplyState),nameof(IsApplying),nameof(HasApplyError),nameof(ApplyErrorSummary),nameof(ApplyFilesSummary)}) Changed(name);
        if(SelectedGame != null) SelectedGame.Status = applyNeedsAnalysis ? "Требуется обновление" : HasApplyError ? "Ошибка применения" : translationApplied ? "Перевод применён" : translationReady ? "Готово к применению" : session != null ? "Требуется перевод" : "Не анализирована";
        if(applyResult?.State==GameApplyState.Applied && !applyNeedsAnalysis) {
            if(applyWatch == null){applyWatch=new(){Interval=TimeSpan.FromSeconds(3)};applyWatch.Tick+=async(_,_)=>await CheckAppliedFilesAsync();}
            applyWatch.Start();
        } else applyWatch?.Stop();
        NotifyWorkflow();
    }
    private static string FriendlyApplyError(Exception error)
    {
        var errors=new List<string>();for(Exception? e=error;e!=null;e=e.InnerException)errors.Add(e.Message);
        var text=string.Join(" ",errors);
        if(text.Contains("physical file hash did not change",StringComparison.OrdinalIgnoreCase))return "Файл уже содержит ожидаемый результат. Повторное применение проверит готовые строки и остальные файлы.";
        if(errors.Any(m=>m.Contains("изменился после анализа",StringComparison.OrdinalIgnoreCase)))return "Файл был изменён после анализа. Обновите анализ перед применением.";
        if(error is UnauthorizedAccessException || text.Contains("access",StringComparison.OrdinalIgnoreCase) || text.Contains("доступ",StringComparison.OrdinalIgnoreCase))return "Нет доступа к файлу. Закройте игру и проверьте права записи.";
        if(text.Contains("missing",StringComparison.OrdinalIgnoreCase) || text.Contains("не найден",StringComparison.OrdinalIgnoreCase) || error is FileNotFoundException)return "Не найден исходный файл или файл резервной копии.";
        if(text.Contains("Validation",StringComparison.OrdinalIgnoreCase) || text.Contains("Placeholder",StringComparison.OrdinalIgnoreCase))return "Проверка перевода не пройдена. Исправьте строки с ошибками.";
        if(text.Contains("backup",StringComparison.OrdinalIgnoreCase) || text.Contains("ownership",StringComparison.OrdinalIgnoreCase) || text.Contains("manifest",StringComparison.OrdinalIgnoreCase))return "Ошибка резервной копии или проверки принадлежности файлов.";
        if(text.Contains("verification",StringComparison.OrdinalIgnoreCase) || text.Contains("SHA256",StringComparison.OrdinalIgnoreCase) || text.Contains("validation",StringComparison.OrdinalIgnoreCase))return "Проверка записи не прошла. Подробности сохранены в отчёте.";
        if(error is OperationCanceledException)return "Применение было отменено. Проверьте записанные файлы в отчёте.";
        return "Не удалось записать файл. Закройте игру и откройте отчёт для подробностей.";
    }
    private async Task LoadApplyResultAsync(Game owner,string active,bool rowsApplied,long revision)
    {
        if(applyResult == null) {
            var stored=await Task.Run(()=>applyStates.Load(owner.Path));
            if(!ReferenceEquals(owner,SelectedGame) || active!=session || revision!=shellRevision)return;
            if(applyResult != null)return;
            applyResult=stored;
            if(stored == null) {
                var path=await Task.Run(()=>diagnosticReports.FindLatest(owner.Path));
                if(path != null) {
                    try {
                        var report=JsonSerializer.Deserialize<ApplyDiagnosticReport>(await File.ReadAllTextAsync(Path.ChangeExtension(path,".json")));
                        if(!ReferenceEquals(owner,SelectedGame) || active!=session || revision!=shellRevision)return;
                        if(report?.Status is "FAILED" or "PARTIAL") {
                            applyResult=ResultFromReport(owner,active,report,new IOException(report.Error),path,null);
                            await applyStates.SaveAsync(applyResult);
                        }
                    } catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException) { }
                }
            }
        }
        if(applyResult?.State == GameApplyState.Applied) {
            var saved=applyResult;
            try {
                var valid=await ApplyStateStore.VerifyFilesAsync(saved,CancellationToken.None);
                if(valid)await workspace.VerifyApplyOwnershipAsync(owner,saved.VerifiedFiles,CancellationToken.None);
                if(!ReferenceEquals(owner,SelectedGame) || active!=session || revision!=shellRevision)return;
                if(!valid) {translationApplied=false;applyNeedsAnalysis=true;}
                else if(!rowsApplied && ReferenceEquals(saved,applyResult)) {applyResult=null;} // prepared edits supersede the previous applied version
            } catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { if(ReferenceEquals(owner,SelectedGame)&&active==session){translationApplied=false;applyNeedsAnalysis=true;} }
        }
        if(HasApplyError)translationApplied=false;
        NotifyApplyState();
    }
    private static GameApplyResult ResultFromReport(Game owner,string active,ApplyDiagnosticReport? report,Exception? error,string? reportPath,GameApplyResult? previous)
    {
        if(report != null && !SamePath(report.Root,owner.Path)){report=null;reportPath=null;}
        var result=new GameApplyResult {Root=owner.Path,Session=active,State=error == null ? GameApplyState.Applied : GameApplyState.ApplyFailed,Reason=error == null ? "" : FriendlyApplyError(error),Details=report?.Summary ?? error?.ToString() ?? "",ReportPath=reportPath};
        if(previous != null) foreach(var file in previous.VerifiedFiles)result.VerifiedFiles[file.Key]=file.Value;
        if(report != null)foreach(var file in report.Files.Where(f=>f.Error.Length==0 && f.ParseSuccess && f.BackupMatchesOriginal && f.Entries.Where(e=>e.Selected).All(e=>e.ValidationStatus=="PASS")))
            result.VerifiedFiles[Path.GetRelativePath(owner.Path,file.PhysicalTargetFile)]=file.AfterHash;
        result.FilesApplied=result.VerifiedFiles.Count;
        result.FilesFailed=report?.Files.Count(f=>f.Error.Length>0 || !f.ParseSuccess) ?? (error != null ? 1 : 0);
        if(error != null && result.FilesApplied>0 && report?.Status == "PARTIAL")result.State=GameApplyState.ApplyPartiallyCompleted;
        return result;
    }
}

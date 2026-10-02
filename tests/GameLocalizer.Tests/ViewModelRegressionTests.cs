using System.Windows.Threading;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;
using GameApplyState=GameLocalizer.Infrastructure.FileSystem.GameApplyState;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class ViewModelRegressionTests
{
    [Fact] public Task RuntimeExportCommandDisablesForEmptyAndClearsOnGameChange() => OnDispatcher(async () =>
    {
        using var f = new Fixture();
        Assert.False(f.Vm.ExportCollectorCommand.CanExecute(null));
        Assert.False(f.Vm.OpenCollectorDataFolderCommand.CanExecute(null));
        f.Vm.SelectedGame = f.First;
        Assert.False(f.Vm.ExportCollectorCommand.CanExecute(null));
        Assert.Equal("Сначала импортируйте строки Runtime UI.", f.Vm.RuntimeExportToolTip);
        f.Vm.RuntimeUi.Add(new() { Text = "Chat", SeenCount = 4 });
        Assert.True(f.Vm.ExportCollectorCommand.CanExecute(null)); Assert.Null(f.Vm.RuntimeExportToolTip);
        f.Vm.SelectedGame = f.Second;
        Assert.Empty(f.Vm.RuntimeUi); Assert.False(f.Vm.ExportCollectorCommand.CanExecute(null));
        await f.Vm.ShutdownAsync();
    });

    [Fact] public Task RuntimeExportUsesImportedRowsWithoutGameOrRawCaptureAndReportsPaths() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); f.Vm.SelectedGame = f.First;
        for (var i = 0; i < 571; i++) f.Vm.RuntimeUi.Add(new() { Text = $"日本語/Совет {i}", Hierarchy = "Canvas/Menu/Text", SeenCount = i + 1 });
        var path = Path.Combine(f.Root, "GameLocalizer_RuntimeUI_First_test.json");
        await f.Vm.ExportRuntimeUiAsync(path);
        Assert.Equal(571, f.Vm.RuntimeUi.Count); Assert.Equal(0, f.Vm.TotalCount);
        Assert.Contains("Экспортировано: 571 строк", f.Vm.RuntimeCollectorStatus);
        Assert.Contains(path, f.Vm.RuntimeCollectorStatus); Assert.Contains(Path.ChangeExtension(path, ".txt"), f.Vm.RuntimeCollectorStatus);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)); Assert.Equal(571, json.RootElement.GetArrayLength());
        await f.Vm.ShutdownAsync();
    });

    [Fact] public Task RuntimeDataFolderCommandHandlesMissingStorageWithoutLaunchingExplorer() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); f.Vm.SelectedGame = f.First;
        Assert.True(f.Vm.OpenCollectorDataFolderCommand.CanExecute(null));
        f.Vm.OpenCollectorDataFolderCommand.Execute(null);
        Assert.Contains("Папка данных Runtime UI Collector ещё не существует", f.Vm.RuntimeCollectorStatus);
        await f.Vm.ShutdownAsync();
    });
    [Fact] public Task PersistentCacheAutomaticallyLoadsWithWorkingTranslateAndApplyAfterRestart() => OnDispatcher(async()=>
    {
        using var f=new Fixture(persistent:true);f.Vm.SelectedGame=f.First;await f.Vm.FindSelectedAsync();
        f.Vm.Rows[0].Russian="Начать игру";await f.Vm.ShutdownAsync();
        using var restartedRepository=new ScanResultRepository(Path.Combine(f.Root,"scan.db"),true);
        var ws=ScanRegressionTests.Workspace(restartedRepository,Path.Combine(f.Root,"memory.db"));
        var detector=new FailingDetector{Fail=true};
        var restarted=new MainViewModel(new Discovery(f.First,f.Second),detector,new(ScanRegressionTests.Adapters()),restartedRepository,ws,new(NullLogger<BackupService>.Instance),new SettingsService(f.Root),new UpdateService(),NullLogger<MainViewModel>.Instance);
        restarted.SelectedGame=f.First;await restarted.AnalysisLoadTask;
        Assert.Equal("Начать игру",Assert.Single(restarted.Rows).Russian);Assert.Equal("CACHED",restarted.ScanStatus);Assert.True(restarted.TranslateCommand.CanExecute(null));Assert.True(restarted.ApplyCommand.CanExecute(null));Assert.Contains("Кэш анализа загружен",restarted.Status);
        Assert.Equal(0,ws.LastFilesRescanned);await restarted.FindSelectedAsync();Assert.Equal(0,ws.LastFilesRescanned);
        await restarted.ShutdownAsync();
    });
    [Fact] public Task CorruptPersistentCacheSelectionDoesNotCrashAndFindRebuildsIt() => OnDispatcher(async()=>
    {
        using var f=new Fixture(persistent:true);File.WriteAllText(Path.Combine(f.Root,"scan.db"),"broken snapshot");f.Vm.SelectedGame=f.First;await f.Vm.AnalysisLoadTask;
        await f.Vm.FindSelectedAsync();Assert.Equal("Start Game",Assert.Single(f.Vm.Rows).Original);await f.Vm.ShutdownAsync();
    });
    [Fact] public Task OptionalDecoderFailurePreservesSupportedBepRows() => OnDispatcher(async () =>
    {
        var logFolder = Path.Combine(Path.GetTempPath(), "DiscoveryLog", Guid.NewGuid().ToString("N"));
        try
        {
            var discovery = new UiResourceDiscovery(ScanRegressionTests.Adapters(), new ScanDiagnosticLog(logFolder), BinaryDiscoveryRegressionTests.LegacyOverflow);
            using var f = new Fixture(uiDiscovery: discovery);
            var text = Path.Combine(f.First.Path,"BepInEx","Translation","en","Text","ui.txt"); Directory.CreateDirectory(Path.GetDirectoryName(text)!);
            File.WriteAllText(text,"おしゃべり=Chat\n助言=Give Advice"); File.WriteAllBytes(Path.Combine(f.First.Path,"sharedassets0.assets"),[0]);
            f.Vm.SelectedGame = f.First; await f.Vm.FindSelectedAsync();
            Assert.True(f.Vm.TotalCount >= 2); Assert.Contains(f.Vm.Rows,r => r.Original == "Chat");
            Assert.Equal("PARTIAL",f.Vm.ScanStatus); Assert.Equal(1,f.Vm.DiscoveryErrorCount); Assert.Equal(1,f.Vm.UnsupportedUnityResourceCount);
        }
        finally { if (Directory.Exists(logFolder)) Directory.Delete(logFolder,true); }
    });
    private sealed class FailingDetector : IEngineDetector
    {
        public bool Fail { get; set; }
        public EngineDetection Detect(string directory, CancellationToken ct) => Fail ? throw new IOException("Regression primary scan failure") : new EngineDetector().Detect(directory,ct);
    }
    [Fact] public Task PrimaryFailurePreservesPreviousRowsAndOffersLog() => OnDispatcher(async () =>
    {
        var detector = new FailingDetector(); using var f = new Fixture(detector); f.Vm.SelectedGame = f.First;
        await f.Vm.FindSelectedAsync(); var old = Assert.Single(f.Vm.Rows).Original; detector.Fail = true;
        await f.Vm.FindSelectedAsync(true); Assert.Equal(old,Assert.Single(f.Vm.Rows).Original);
        Assert.Equal("FAILED",f.Vm.ScanStatus); Assert.True(f.Vm.OpenScanLogCommand.CanExecute(null)); Assert.DoesNotContain("IOException",f.Vm.Status);
    });
    private static Task OnDispatcher(Func<Task> action, int timeoutSeconds = 60)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); } catch (Exception e) { completion.TrySetException(e); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "GameLocalizerVmTests", Guid.NewGuid().ToString("N"));
        public MainViewModel Vm { get; }
        public ScanResultRepository Repository { get; }
        public Game First { get; }
        public Game Second { get; }
        public Fixture(IEngineDetector? detector = null, Func<ApplySelectionSummary, string>? emptyChoice = null, Func<string, bool>? applyConfirmation = null, UiResourceDiscovery? uiDiscovery = null, bool persistent = false, ITranslationProvider? provider = null, Func<GameLocalizer.UI.Views.ApplySummary,GameLocalizer.UI.Views.ApplySummaryDecision>? summaryChoice = null, string? existingRoot = null)
        {
            if(existingRoot != null)Root=existingRoot;
            First = new("1", "First", Path.Combine(Root, "first"), "Steam"); Second = new("2", "Second", Path.Combine(Root, "second"), "Steam");
            Directory.CreateDirectory(First.Path); Directory.CreateDirectory(Second.Path);
            if(existingRoot == null){File.WriteAllText(Path.Combine(First.Path, "dialogue.txt"), "Start Game"); File.WriteAllText(Path.Combine(Second.Path, "dialogue.txt"), "Continue");}
            var settings = new SettingsService(Root); if(existingRoot == null)settings.Save(new() { CheckUpdatesOnStartup = false });
            Repository = new(Path.Combine(Root, "scan.db"), persistent);
            Vm = new(new Discovery(First, Second), detector ?? new EngineDetector(), new(ScanRegressionTests.Adapters()), Repository,
                ScanRegressionTests.Workspace(Repository, Path.Combine(Root, "memory.db"), provider, new TranslationJobStore(Path.Combine(Root, "jobs"))), new(NullLogger<BackupService>.Instance), settings,
                new UpdateService(), NullLogger<MainViewModel>.Instance, diagnosticReports: new ApplyDiagnosticReportLocator(Path.Combine(Root, "reports")), applySummaryChoice: summaryChoice ?? (summary => {
                    var choice=summary.Selection.SkippedEmptyTranslations>0 ? emptyChoice?.Invoke(summary.Selection) ?? "Да" : "Да";
                    if(choice!="Да")return new(choice=="Показать"?"Показать пропущенные":"Отмена");
                    return new(applyConfirmation?.Invoke(summary.Description) != false ? "Применить":"Отмена");
                }), uiDiscovery: uiDiscovery, scanDiagnostics: new ScanDiagnosticLog(Path.Combine(Root, "logs")));
            Vm.Games.Add(First); Vm.Games.Add(Second);
        }
        public void Dispose() { Repository.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if(Directory.Exists(Root))Directory.Delete(Root, true); }
    }

    private sealed class PartialProvider : ITranslationProvider {
        public string Name => "Regression";
        public bool Fail { get; set; } = true;
        public Action? ObserveRunning { get; set; }
        public bool Cancel { get; set; }
        public List<string> Inputs { get; } = [];
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct) {
            if(Cancel) throw new OperationCanceledException();
            Inputs.AddRange(request.Batch.Items.Select(i => i.Text)); ObserveRunning?.Invoke();
            return Task.FromResult(new TranslationResult(request.Batch.Items
                .Where(i => !Fail || !i.Text.StartsWith("Failure", StringComparison.Ordinal))
                .ToDictionary(i => i.Id, i => "Перевод " + i.Text)));
        }
    }
    [Fact] public Task Existing5712Of5725FinalizesShowsErrorsAppliesAndRetriesOnlyFailures() => OnDispatcher(async () => {
        var provider = new PartialProvider(); ApplySelectionSummary? confirmation = null;
        using var f = new Fixture(provider: provider, emptyChoice: summary => { confirmation = summary; return "Да"; }, applyConfirmation: _ => true);
        File.WriteAllText(Path.Combine(f.First.Path,"dialogue.txt"), "");
        File.WriteAllText(Path.Combine(f.First.Path,"dialogue.csv"), "id,text\n" + string.Join("\n", Enumerable.Range(0,5725).Select(i => $"id{i},{(i >= 5712 ? "Failure" : "Welcome")} to the village number {i}")));
        f.Vm.SelectedGame = f.First; await f.Vm.FindSelectedAsync();
        Assert.Same(f.Vm.TranslateCommand, f.Vm.MainActionCommand);
        provider.ObserveRunning = () => { Assert.True(f.Vm.IsTranslating); Assert.False(f.Vm.MainActionEnabled); Assert.StartsWith("Перевод…",f.Vm.MainActionLabel); };
        await f.Vm.TranslateSelectedAsync(); await f.Vm.WorkflowRefreshTask;
        var job = Assert.IsType<TranslationJob>(f.Vm.CurrentTranslationJob);
        Assert.False(job.IsRunning); Assert.Equal(TranslationJobStatus.PartiallyCompleted,job.Status);
        Assert.Equal(5712,job.Successful); Assert.Equal(13,job.FailedStrings);
        Assert.Equal(5725,job.ProcessedCount); Assert.Equal(100,job.ProgressPercent);
        Assert.True(f.Vm.Idle); Assert.False(f.Vm.IsTranslating);
        Assert.Same(f.Vm.ApplyCommand,f.Vm.MainActionCommand); Assert.Contains("частично",f.Vm.TranslationStage);
        Assert.Equal("Готово к применению",f.Vm.ApplyStage);
        await SaveFinalizationViewAsync(f.Vm, "partial");
        await f.Vm.ShowTranslationErrorsAsync(); Assert.Equal(13,f.Vm.Rows.Count);
        Assert.All(f.Vm.Rows,r => { Assert.StartsWith("Failure",r.Original); Assert.NotEmpty(r.ErrorType); Assert.NotEmpty(r.ErrorMessage); Assert.Equal("Ожидает повтора",r.RetryStatus); });
        await f.Vm.ApplySelectedAsync(); Assert.Equal(13,confirmation!.SkippedEmptyTranslations);
        Assert.Equal(5712,confirmation.AppliedEntries); Assert.Equal("Перевод применён",f.Vm.MainActionLabel);
        Assert.False(f.Vm.MainActionEnabled);
        provider.Inputs.Clear();
        await f.Vm.RetryFailedAsync(); Assert.Equal(13,provider.Inputs.Count);
        Assert.Equal(TranslationJobStatus.PartiallyCompleted,f.Vm.CurrentTranslationJob!.Status);
        Assert.All(f.Vm.CurrentTranslationJob.Errors,error => Assert.Equal("Повторная ошибка",error.RetryStatus));
        provider.Inputs.Clear(); provider.Fail = false;
        await f.Vm.RetryFailedAsync(); await f.Vm.WorkflowRefreshTask;
        Assert.Equal(13,provider.Inputs.Count); Assert.All(provider.Inputs,text => Assert.StartsWith("Failure",text));
        Assert.Equal(TranslationJobStatus.Completed,f.Vm.CurrentTranslationJob!.Status);
        Assert.Equal(5725,f.Vm.CurrentTranslationJob.Successful); Assert.Equal(100,f.Vm.TranslationProgressPercent);
        Assert.Same(f.Vm.ApplyCommand,f.Vm.MainActionCommand); Assert.Equal("Применить перевод",f.Vm.MainActionLabel);
        await f.Vm.ShowTranslationErrorsAsync(); Assert.Empty(f.Vm.Rows);
        await f.Vm.ShutdownAsync();
    }, timeoutSeconds: 120);
    [Theory] [InlineData(false,TranslationJobStatus.Failed)] [InlineData(true,TranslationJobStatus.Cancelled)]
    public Task FailedAndCancelledJobsReleaseUiWithoutUnlockingEmptyApply(bool cancel, TranslationJobStatus expected) => OnDispatcher(async () => {
        var provider = new PartialProvider { Cancel = cancel };
        using var f = new Fixture(provider:provider);
        File.WriteAllText(Path.Combine(f.First.Path,"dialogue.txt"),"Failure to complete the quest");
        f.Vm.SelectedGame=f.First; await f.Vm.FindSelectedAsync(); await f.Vm.TranslateSelectedAsync();
        var job=Assert.IsType<TranslationJob>(f.Vm.CurrentTranslationJob);
        Assert.Equal(expected,job.Status); Assert.False(job.IsRunning); Assert.Equal(100,job.ProgressPercent);
        Assert.True(f.Vm.Idle); Assert.False(f.Vm.IsTranslating); Assert.Equal(0,job.Successful);
        Assert.Same(f.Vm.TranslateCommand,f.Vm.MainActionCommand); Assert.Equal("Ожидает перевода",f.Vm.ApplyStage);
        await f.Vm.ShutdownAsync();
    });
    [Fact] public Task RetryProtectsManualAndCachedRows() => OnDispatcher(async () => {
        var provider = new PartialProvider(); using var f = new Fixture(provider:provider);
        File.WriteAllText(Path.Combine(f.First.Path,"dialogue.txt"),"");
        File.WriteAllText(Path.Combine(f.First.Path,"dialogue.csv"),"id,text\na,Failure to complete quest one\nb,Failure to complete quest two\nc,Failure to complete quest three");
        f.Vm.SelectedGame=f.First; await f.Vm.FindSelectedAsync(); await f.Vm.TranslateSelectedAsync();
        await f.Vm.ShowTranslationErrorsAsync(); Assert.Equal(3,f.Vm.Rows.Count);
        var session=f.Vm.CurrentTranslationJob!.SessionId;
        await f.Repository.SaveEditsAsync(session,[new(f.Vm.Rows[0].Id,"Ручной перевод",true,TranslationStatus.Manual),new(f.Vm.Rows[1].Id,"Из памяти",true,TranslationStatus.FromMemory)],default);
        provider.Inputs.Clear(); provider.Fail=false; await f.Vm.RetryFailedAsync();
        Assert.Single(provider.Inputs); Assert.Equal("Failure to complete quest three",provider.Inputs[0]);
        var rows=await f.Repository.ReadSelectedAsync(session,0,false,default);
        Assert.Equal("Ручной перевод",rows[0].Translation); Assert.Equal("Manual",rows[0].Status);
        Assert.Equal("Из памяти",rows[1].Translation); Assert.Equal("FromMemory",rows[1].Status);
        Assert.Equal(TranslationJobStatus.Completed,f.Vm.CurrentTranslationJob!.Status);
        await f.Vm.ShutdownAsync();
    });
    [Theory] [InlineData(true,true)][InlineData(false,false)][InlineData(true,false)]
    public Task OneApplySummaryCombinesWarningsAndPersistsConsent(bool empty,bool suppressCombined) => OnDispatcher(async () => {
        var calls=0;GameLocalizer.UI.Views.ApplySummary? shown=null;
        using var f=new Fixture(summaryChoice: summary=>{calls++;shown=summary;return new("Применить",true);});
        f.Vm.Settings.SkipCombinedApplyWarning=suppressCombined;
        File.WriteAllText(Path.Combine(f.First.Path,"dialogue.txt"),"Start Game\nContinue");
        f.Vm.SelectedGame=f.First;await f.Vm.FindSelectedAsync();f.Vm.Rows[0].Russian="Начать игру";
        if(!empty)f.Vm.Rows[1].Russian="Продолжить";
        var transitions=new List<GameApplyState?>();f.Vm.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(MainViewModel.ApplyState))transitions.Add(f.Vm.ApplyState);};
        await f.Vm.ApplySelectedAsync();Assert.Equal(1,calls);Assert.Equal(!suppressCombined,shown!.ShowCombinedWarning);
        Assert.Equal(empty?1:0,shown.Selection.SkippedEmptyTranslations);Assert.Contains(GameApplyState.Applying,transitions);Assert.Contains(GameApplyState.Applied,transitions);
        Assert.Equal(GameApplyState.Applied,f.Vm.ApplyState);Assert.Equal("Перевод применён",f.Vm.MainActionLabel);Assert.False(f.Vm.MainActionEnabled);
        Assert.True(new SettingsService(f.Root).Load().SkipCombinedApplyWarning);
        f.Vm.IsTranslation=true;f.Vm.IsLibrary=true;Assert.Equal(GameApplyState.Applied,f.Vm.ApplyState);
        await f.Vm.ShutdownAsync();
    });
    [Fact] public Task SummaryShowsSkippedAndCriticalValidationNeverConfirms() => OnDispatcher(async () => {
        var calls=0;using var f=new Fixture(summaryChoice:_=>{calls++;return new("Показать пропущенные");});
        await SetupEmptyUi(f);await f.Vm.ApplySelectedAsync();Assert.Equal(1,calls);Assert.True(f.Vm.IsTranslation);
        Assert.Equal("Пустые переводы",f.Vm.Filter);Assert.Equal(2,f.Vm.Rows.Count);
        f.Vm.Filter="Все";await f.Vm.PreviewTask;f.Vm.Rows[0].Russian="{broken}";await f.Vm.ApplySelectedAsync();
        Assert.Equal(1,calls);Assert.Equal(GameApplyState.ApplyFailed,f.Vm.ApplyState);Assert.Contains("Ошибка",f.Vm.ApplyStage);
        Assert.Equal("Повторить применение",f.Vm.MainActionLabel);Assert.Contains("Проверка перевода не пройдена",f.Vm.ApplyErrorSummary);
        await f.Vm.ShutdownAsync();
    });
    [Fact] public Task WriteFailurePersistsAndRetryOnlyAppliesWithoutTranslator() => OnDispatcher(async () => {
        var provider=new PartialProvider {Fail=false};var confirmations=0;
        using var f=new Fixture(provider:provider,summaryChoice:_=>{confirmations++;return new("Применить");});
        f.Vm.SelectedGame=f.First;await f.Vm.FindSelectedAsync();f.Vm.Rows[0].Russian="Начать игру";
        var path=Path.Combine(f.First.Path,"dialogue.txt");
        using(var blocked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            await f.Vm.ApplySelectedAsync();Assert.Equal(GameApplyState.ApplyFailed,f.Vm.ApplyState);
            Assert.Equal("Повторить применение",f.Vm.MainActionLabel);Assert.Contains("Ошибка",f.Vm.ApplyStage);
            await f.Vm.RefreshPreviewAsync();Assert.Equal(GameApplyState.ApplyFailed,f.Vm.ApplyState);Assert.True(f.Vm.HasApplyError);
        }
        Assert.Equal(GameApplyState.ApplyFailed,new ApplyStateStore(Path.Combine(f.Root,"ApplyStates")).Load(f.First.Path)!.State);
        await f.Vm.ApplySelectedAsync();Assert.Equal(GameApplyState.Applied,f.Vm.ApplyState);Assert.Equal(2,confirmations);
        Assert.Empty(provider.Inputs);Assert.Equal("Начать игру",File.ReadAllText(path));await f.Vm.ShutdownAsync();
    });
    [Fact] public Task PartialApplyRetriesOnlyFailedFile() => OnDispatcher(async () => {
        using var f=new Fixture(summaryChoice:_=>new("Применить"));
        File.WriteAllText(Path.Combine(f.First.Path,"dialogue-a.txt"),"Continue");File.WriteAllText(Path.Combine(f.First.Path,"dialogue-z.txt"),"Exit");
        File.WriteAllText(Path.Combine(f.First.Path,"dialogue.txt"),"");
        f.Vm.SelectedGame=f.First;await f.Vm.FindSelectedAsync();Assert.Equal(2,f.Vm.Rows.Count);foreach(var row in f.Vm.Rows){row.Selected=true;row.Russian="Русский "+row.Original;}
        var first=Path.Combine(f.First.Path,"dialogue-a.txt");var last=Path.Combine(f.First.Path,"dialogue-z.txt");
        using(var blocked=new FileStream(last,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            await f.Vm.ApplySelectedAsync();Assert.True(f.Vm.ApplyState==GameApplyState.ApplyPartiallyCompleted,new ApplyStateStore(Path.Combine(f.Root,"ApplyStates")).Load(f.First.Path)?.Details);
            Assert.Contains("Применено файлов: 1",f.Vm.ApplyFilesSummary);Assert.Contains("Частично",f.Vm.ApplyStage,StringComparison.OrdinalIgnoreCase);
        }
        var stamp=File.GetLastWriteTimeUtc(first);await f.Vm.ApplySelectedAsync();Assert.Equal(GameApplyState.Applied,f.Vm.ApplyState);
        Assert.Equal(stamp,File.GetLastWriteTimeUtc(first));await f.Vm.ShutdownAsync();
    });
    [Fact] public Task AppliedSurvivesSelectionAndRestartAndExternalChangeInvalidates() => OnDispatcher(async () => {
        using var f=new Fixture(persistent:true,summaryChoice:_=>new("Применить"));
        f.Vm.SelectedGame=f.First;await f.Vm.FindSelectedAsync();f.Vm.Rows[0].Russian="Начать игру";await f.Vm.ApplySelectedAsync();
        f.Vm.SelectedGame=f.Second;await f.Vm.AnalysisLoadTask;f.Vm.SelectedGame=f.First;await f.Vm.AnalysisLoadTask;
        Assert.Equal(GameApplyState.Applied,f.Vm.ApplyState);Assert.Equal("Перевод применён",f.Vm.MainActionLabel);await f.Vm.ShutdownAsync();
        using var restarted=new Fixture(persistent:true,existingRoot:f.Root,summaryChoice:_=>new("Отмена"));
        restarted.Vm.SelectedGame=restarted.First;await restarted.Vm.AnalysisLoadTask;Assert.Equal(GameApplyState.Applied,restarted.Vm.ApplyState);
        File.WriteAllText(Path.Combine(restarted.First.Path,"dialogue.txt"),"External edit");await restarted.Vm.CheckAppliedFilesAsync();
        Assert.Equal(GameApplyState.NeedsUpdate,restarted.Vm.ApplyState);Assert.Equal("Обновить перевод",restarted.Vm.MainActionLabel);
        Assert.Equal("Требуется обновление",restarted.First.Status);await restarted.Vm.ShutdownAsync();
    });
    [Fact] public Task ArtworkLoadLeavesWpfLibraryResponsive() => OnDispatcher(async () => {
        using var f=new Fixture();File.WriteAllText(Path.Combine(f.First.Path,"First.exe"),"fake");
        using var release=new ManualResetEventSlim();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service=new LibraryArtworkService(Path.Combine(f.Root,"artwork-test"),[],_=>{
            started.TrySetResult();release.Wait(TimeSpan.FromSeconds(10));
            var image=System.Windows.Media.Imaging.BitmapSource.Create(32,32,96,96,System.Windows.Media.PixelFormats.Bgra32,null,new byte[4096],128);image.Freeze();return image;
        });
        var pending=service.LoadAsync(f.First);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var tick=false;await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{f.Vm.LibrarySearch="First";tick=true;});
        Assert.True(tick);Assert.False(pending.IsCompleted);Assert.Single(f.Vm.LibraryGames.Cast<Game>());
        release.Set();var loaded=Assert.IsType<LibraryArtwork>(await pending);f.First.ArtworkIsIcon=loaded.IsIcon;f.First.Artwork=loaded.Image;
        Assert.True(f.First.ArtworkIsIcon);Assert.True(((System.Windows.Media.Imaging.BitmapSource)f.First.Artwork).IsFrozen);await f.Vm.ShutdownAsync();
    });
    private static async Task SaveFinalizationViewAsync(MainViewModel vm, string name)
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root,"src/GameLocalizer.UI/Views/MainWindow.xaml"));
        source = source.Replace("clr-namespace:GameLocalizer.UI.Views","clr-namespace:GameLocalizer.UI.Views;assembly=GameLocalizer").Replace("x:Class=\"GameLocalizer.UI.Views.MainWindow\"", "");
        source = System.Text.RegularExpressions.Regex.Replace(source," (Click|Checked|Unchecked|SelectionChanged|DragStarted|DragDelta|DragCompleted)=\"[^\"]*\"", "");
        var app = System.Xml.Linq.XDocument.Load(Path.Combine(root,"src/GameLocalizer.UI/App.xaml"));
        var localTargets = System.Xml.Linq.XDocument.Parse(source).Descendants().Where(e => e.Name.LocalName == "Style" && e.Attribute(System.Xml.Linq.XName.Get("Key","http://schemas.microsoft.com/winfx/2006/xaml")) == null).Select(e => (string?)e.Attribute("TargetType")).ToHashSet();
        var theme = app.Root!.Elements().Single().Elements().Where(e => e.Name.LocalName != "Style" || ((string?)e.Attribute("TargetType") != "Window" && !localTargets.Contains((string?)e.Attribute("TargetType"))));
        source = source.Replace("Style=\"{StaticResource {x:Type Window}}\"", "").Replace("<Window.Resources>","<Window.Resources>"+string.Concat(theme.Select(e => e.ToString())));
        var window = (System.Windows.Window)System.Windows.Markup.XamlReader.Parse(source); window.DataContext = vm;
        var content = (System.Windows.FrameworkElement)window.Content;
        ((System.Windows.Controls.Grid)content).Background = window.Background;
        foreach(var dock in new[]{"Bottom","Right"}) {
            vm.PanelLayout.SetDock(dock);
            for(var pass=0;pass<3;pass++) {
                GameLocalizer.UI.Views.MainWindow.ApplyDockPresentation(window,vm.PanelLayout,vm.HasGame);
                content.Measure(new System.Windows.Size(1350,728)); content.Arrange(new System.Windows.Rect(0,0,1350,728)); content.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            }
            var primary = (System.Windows.Controls.Button)window.FindName("PrimaryAction");
            Assert.Equal("Применить перевод",primary.Content); Assert.True(primary.IsEnabled);
            if(dock == "Right") { ((System.Windows.Controls.ScrollViewer)window.FindName("PanelContentScroll")).ScrollToEnd(); content.UpdateLayout(); }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1350,728,96,96,System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var output = Path.Combine(root,"artifacts/dev/translation-finalization"); Directory.CreateDirectory(output);
            using var stream = File.Create(Path.Combine(output,$"{name}-{dock}.png")); png.Save(stream);
        }
        vm.PanelLayout.Reset(); window.Close();
    }
    private sealed class Discovery(Game first, Game second) : IGameDiscoveryService
    {
        public async Task<IReadOnlyList<Game>> DiscoverAsync(CancellationToken ct)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            return [new(first.Id, first.Name, first.Path.ToUpperInvariant(), "Steam"), new(second.Id, second.Name, second.Path, "Steam")];
        }
    }
    private sealed class SlowDetector : IEngineDetector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public EngineDetection Detect(string directory, CancellationToken ct)
        {
            Started.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(10));
            // Deliberately ignores cancellation: the VM must also discard stale completion.
            return new(EngineType.Unity, .99, [directory]);
        }
    }
    [Fact]
    public Task RepeatedGameChangesClearPreviewAndCommandsUseNewGame() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); var vm = f.Vm;
        for (var i = 0; i < 6; i++)
        {
            var selected = i % 2 == 0 ? f.First : f.Second;
            vm.SelectedGame = selected;
            Assert.Same(selected, vm.SelectedGame); Assert.Empty(vm.Rows); Assert.Equal(selected.Engine, vm.Engine);
            Assert.True(vm.FindCommand.CanExecute(null)); Assert.False(vm.TranslateCommand.CanExecute(null));
            await vm.FindSelectedAsync();
            Assert.Equal(i % 2 == 0 ? "Start Game" : "Continue", Assert.Single(vm.Rows).Original);
            Assert.True(vm.TranslateCommand.CanExecute(null));
        }
        await vm.FlushEditsAsync();
    });
    [Fact]
    public Task RefreshPreservesSelectedInstanceAndPreview() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); var vm = f.Vm; vm.SelectedGame = f.Second;
        await vm.FindSelectedAsync(); await vm.RefreshGamesAsync();
        Assert.Same(f.Second, vm.SelectedGame); Assert.Equal(2, vm.Games.Count);
        Assert.Equal("Continue", Assert.Single(vm.Rows).Original);
    });
    [Fact]
    public Task SelectionDuringOperationCancelsAndDiscardsStaleEngineAndStatus() => OnDispatcher(async () =>
    {
        var detector = new SlowDetector(); using var f = new Fixture(detector); var vm = f.Vm;
        vm.SelectedGame = f.First; var analysis = vm.AnalyzeSelectedAsync();
        await detector.Started.Task; Assert.True(vm.Busy);
        vm.SelectedGame = f.Second; Assert.Same(f.Second, vm.SelectedGame);
        var status = vm.Status;
        detector.Release.Set(); await analysis;
        Assert.Equal(status, vm.Status); Assert.Equal("Unknown", vm.Engine); Assert.Equal("Unknown", f.Second.Engine);
        Assert.Empty(vm.Resources); Assert.False(vm.Busy); Assert.True(vm.AnalyzeCommand.CanExecute(null));
        detector.Release.Dispose();
    });
    [Fact]
    public Task CategoryFiltersAndBulkSelectionNeverSelectTechnicalRows() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); var vm = f.Vm;
        File.WriteAllText(Path.Combine(f.First.Path, "notes.txt"), "UnityEngine.AIModule\nLantern\nlantern");
        vm.SelectedGame = f.First; await vm.FindSelectedAsync();
        Assert.Equal(4, vm.TotalCount); Assert.Equal(1, vm.SelectedCount); Assert.Equal(2, vm.Rows.Count);
        Assert.Contains("Пользовательский текст: 1", vm.Counters); Assert.Contains("Технические: 1", vm.Counters);
        vm.Filter = "Технические"; await vm.PreviewTask;
        var technical = Assert.Single(vm.Rows); Assert.Equal(TextCategory.Technical, technical.Category);
        vm.SelectVisibleCommand.Execute(null); await vm.FlushEditsAsync();
        Assert.False(technical.Selected); Assert.False(technical.CanSelect); Assert.Equal(1, vm.SelectedCount);
        vm.Filter = "Сомнительные"; await vm.PreviewTask;
        Assert.Equal(2, vm.Rows.Count); Assert.All(vm.Rows, r => Assert.False(r.Selected));
        vm.Filter = "Высокая уверенность"; await vm.PreviewTask; Assert.Single(vm.Rows);
        vm.Filter = "Выбранные"; await vm.PreviewTask; Assert.Single(vm.Rows);
        await vm.ShutdownAsync();
    });
    [Fact]
    public Task ManualEditSurvivesFreshScanAndTranslation() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); var vm = f.Vm;
        vm.SelectedGame = f.First; await vm.FindSelectedAsync();
        Assert.Single(vm.Rows).Russian = "Начать приключение";
        await vm.FlushEditsAsync();
        await vm.FindSelectedAsync();
        Assert.Equal("Начать приключение", Assert.Single(vm.Rows).Russian);
        Assert.Equal("Manual", vm.Rows[0].Status);
        await vm.TranslateSelectedAsync();
        Assert.Equal("Начать приключение", Assert.Single(vm.Rows).Russian);
        Assert.Equal("Manual", vm.Rows[0].Status);
        await vm.ShutdownAsync();
    });
    [Fact]
    public Task ViewModelUsesOnlyOnePageAndPersistsOffPageEdits() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); var vm = f.Vm; ScanRegressionTests.Generate(f.First.Path, 100005);
        vm.SelectedGame = f.First; await vm.FindSelectedAsync();
        Assert.Equal(100006, vm.TotalCount); Assert.Equal(2000, vm.Rows.Count);
        await vm.ChangePageAsync(50); Assert.Equal(6, vm.Rows.Count);
        var last = vm.Rows[^1]; last.Russian = "Последняя строка"; last.Selected = false;
        await vm.ChangePageAsync(0);
        Assert.Equal(2000, vm.Rows.Count); Assert.Equal(100005, vm.SelectedCount);
        vm.Search = "ПОСЛЕДНЯЯ"; await vm.PreviewTask;
        Assert.Equal("Последняя строка", Assert.Single(vm.Rows).Russian); Assert.False(vm.Rows[0].Selected);
        vm.SelectedGame = f.Second; await vm.FindSelectedAsync();
        Assert.Equal(1, vm.TotalCount); Assert.Empty(vm.Rows); // Full-dataset filter still active.
        vm.Search = ""; await vm.PreviewTask; Assert.Equal("Continue", Assert.Single(vm.Rows).Original);
        await vm.EditSaveTask;
    }, timeoutSeconds: 300); // Full 100k-row scan competes with durable-write updater tests on hosted Windows runners.
    private static string WriteReport(Fixture f, Game game, string name)
    {
        var directory = Path.Combine(f.Root, "reports"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ApplyDiagnostic_" + name + ".txt");
        File.WriteAllText(path, "diagnostics"); File.WriteAllText(Path.ChangeExtension(path, ".json"), System.Text.Json.JsonSerializer.Serialize(new { Root = game.Path })); return path;
    }
    [Fact] public Task ReportDisabledWithoutReport() => OnDispatcher(() =>
    {
        using var f = new Fixture(); f.Vm.SelectedGame = f.First;
        Assert.False(f.Vm.OpenDiagnosticReportCommand.CanExecute(null)); Assert.Equal("Отчёт появится после применения перевода.", f.Vm.DiagnosticReportToolTip); return Task.CompletedTask;
    });
    [Fact] public Task ReportPersistsAcrossViewModelRestartAndSwitchesGames() => OnDispatcher(() =>
    {
        using var f = new Fixture(); var first = WriteReport(f, f.First, "first"); var second = WriteReport(f, f.Second, "second");
        f.Vm.SelectedGame = f.First; Assert.Equal(first, f.Vm.DiagnosticReportPath); Assert.True(f.Vm.OpenDiagnosticReportCommand.CanExecute(null));
        var restarted = new MainViewModel(new Discovery(f.First, f.Second), new EngineDetector(), new(ScanRegressionTests.Adapters()), f.Repository,
            ScanRegressionTests.Workspace(f.Repository, Path.Combine(f.Root, "restart-memory.db")), new(NullLogger<BackupService>.Instance), new SettingsService(f.Root), new UpdateService(), NullLogger<MainViewModel>.Instance,
            diagnosticReports: new ApplyDiagnosticReportLocator(Path.Combine(f.Root, "reports")));
        restarted.SelectedGame = f.First; Assert.Equal(first, restarted.DiagnosticReportPath);
        restarted.SelectedGame = f.Second; Assert.Equal(second, restarted.DiagnosticReportPath); return Task.CompletedTask;
    });
    [Fact] public Task ReportCreationRefreshesCommandAndDeletedFileIsGraceful() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); f.Vm.SelectedGame = f.First; Assert.False(f.Vm.OpenDiagnosticReportCommand.CanExecute(null));
        var backup = new BackupService(NullLogger<BackupService>.Instance, Path.Combine(f.Root, "backup-state"));
        var snapshot = await TextFiles.ReadAsync(Path.Combine(f.First.Path, "dialogue.txt"), default);
        await backup.ApplyAsync(f.First.Path, [new("dialogue.txt", snapshot.Hash, snapshot.Encode("Начать игру")) { AdapterType = "TXT" }], default);
        var directory = Path.Combine(f.Root, "reports"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileName(backup.LastDiagnosticReportPath!));
        File.Copy(backup.LastDiagnosticReportPath!, path); File.Copy(Path.ChangeExtension(backup.LastDiagnosticReportPath!, ".json"), Path.ChangeExtension(path, ".json"));
        f.Vm.RefreshDiagnosticReport(); Assert.True(f.Vm.OpenDiagnosticReportCommand.CanExecute(null));
        Assert.Equal("Открыть последний отчёт применения.", f.Vm.DiagnosticReportToolTip);
        File.Delete(path); f.Vm.OpenDiagnosticReportCommand.Execute(null); Assert.Equal("Диагностический отчёт не найден.", f.Vm.Status);
        Assert.False(f.Vm.OpenDiagnosticReportCommand.CanExecute(null));
    });
    [Theory] [InlineData(1280, 720)] [InlineData(1366, 768)] [InlineData(1600, 900)] [InlineData(1920, 1080)]
    public Task MainScreenLayoutGroupsAndPreviewFit(int width, int height) => OnDispatcher(async () =>
    {
        using var f = new Fixture(); f.Vm.SelectedGame = f.First;
        f.Vm.Rows.Add(new TranslationRow { Id = 1, File = "BepInEx\\Translation\\en\\Text\\dialogue.txt", Key = "ありがとう", Original = string.Join(" ", Enumerable.Repeat("Thank you.", 50)), Russian = string.Join(" ", Enumerable.Repeat("Спасибо.", 50)), LocalizationSlot = "en", Confidence = .99, State = GameLocalizer.Core.Models.TranslationStatus.Translated });
        foreach(var name in new[] { "Northern Realm", "Тихая долина", "Skyline", "Adventure", "Nightfall", "Pixel Valley" })
            f.Vm.Games.Add(new(Guid.NewGuid().ToString("N"), name, Path.Combine(f.Root,name), "Manual"));
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src/GameLocalizer.UI/Views/MainWindow.xaml"));
        source = source.Replace("clr-namespace:GameLocalizer.UI.Views", "clr-namespace:GameLocalizer.UI.Views;assembly=GameLocalizer");
        source = source.Replace("x:Class=\"GameLocalizer.UI.Views.MainWindow\"", "");
        source = System.Text.RegularExpressions.Regex.Replace(source, " (Click|Checked|Unchecked|SelectionChanged|DragStarted|DragDelta|DragCompleted)=\"[^\"]*\"", "");
        var app = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src/GameLocalizer.UI/App.xaml"));
        var localStyleTargets = System.Xml.Linq.XDocument.Parse(source).Descendants().Where(e => e.Name.LocalName == "Style" && e.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == null).Select(e => (string?)e.Attribute("TargetType")).ToHashSet();
        var theme = app.Root!.Elements().Single().Elements().Where(e => e.Name.LocalName != "Style" || ((string?)e.Attribute("TargetType") != "Window" && !localStyleTargets.Contains((string?)e.Attribute("TargetType"))));
        source = source.Replace("Style=\"{StaticResource {x:Type Window}}\"", "");
        source = source.Replace("<Window.Resources>", "<Window.Resources>" + string.Concat(theme.Select(e => e.ToString())));
        var window = (System.Windows.Window)System.Windows.Markup.XamlReader.Parse(source); window.DataContext = f.Vm;
        var content = (System.Windows.FrameworkElement)window.Content;
        ((System.Windows.Controls.Grid)content).Background = window.Background;
        content.Measure(new System.Windows.Size(width - 16, height - 40)); content.Arrange(new System.Windows.Rect(0, 0, width - 16, height - 40)); content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); content.InvalidateMeasure(); content.Measure(new System.Windows.Size(width - 16, height - 40)); content.Arrange(new System.Windows.Rect(0, 0, width - 16, height - 40)); content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); content.UpdateLayout();
        var library = (System.Windows.Controls.ListBox)window.FindName("LibraryGrid");
        Assert.Same(f.Vm.SelectedGame, library.SelectedItem);
        var host = (System.Windows.FrameworkElement)window.FindName("LibraryHost");
        var panel = (System.Windows.FrameworkElement)window.FindName("SelectedGamePanel");
        for(var pass=0;pass<3;pass++)
        {
            GameLocalizer.UI.Views.MainWindow.ApplyDockPresentation(window,f.Vm.PanelLayout,f.Vm.HasGame);
            GameLocalizer.UI.Views.MainWindow.ApplyLibraryPresentation(library,host,panel,false);
            content.InvalidateMeasure(); content.Measure(new System.Windows.Size(width - 16,height - 40));
            content.Arrange(new System.Windows.Rect(0,0,width - 16,height - 40)); content.UpdateLayout();
        }
        Assert.Equal(width < 1500 ? 3 : 4, GameLocalizer.UI.Views.MainWindow.CardColumns(host.ActualWidth - 12));
        Assert.True(library.ActualHeight >= 174);
        var positions = Enumerable.Range(0,library.Items.Count).Select(i => library.ItemContainerGenerator.ContainerFromIndex(i)).OfType<System.Windows.FrameworkElement>().Select(item => item.TranslatePoint(new System.Windows.Point(),library)).ToArray();
        var firstRow = positions.Count(point => Math.Abs(point.Y - positions.Min(p => p.Y)) < 1);
        Assert.Equal(width < 1500 ? 3 : 4, firstRow);
        Assert.IsType<System.Windows.Controls.Grid>(host); // Library owns its scroll viewport; no overlay/outer scrolling.
        Assert.True(library.ActualHeight > 120);
        var primary = (System.Windows.Controls.Button)window.FindName("PrimaryAction");
        Assert.Same(f.Vm.MainActionCommand,primary.Command); Assert.Equal(54,primary.ActualHeight);
        var advanced = (System.Windows.Controls.Expander)window.FindName("AdvancedActions"); Assert.False(advanced.IsExpanded);
        var panelPosition = panel.TranslatePoint(new System.Windows.Point(),content);
        Assert.True(panelPosition.X >= 252 && panelPosition.X + panel.ActualWidth <= content.ActualWidth + 1);
        Assert.True(panelPosition.Y + panel.ActualHeight <= content.ActualHeight + 1, $"Selected panel exceeds {width}x{height}: bottom {panelPosition.Y + panel.ActualHeight}, content {content.ActualHeight}");
        var libraryBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width - 16, height - 40, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); libraryBitmap.Render(content);
        var libraryEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); libraryEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(libraryBitmap));
        var libraryOutput = Path.Combine(root, "artifacts/dev/panel-dock"); Directory.CreateDirectory(libraryOutput);
        using(var libraryStream = File.Create(Path.Combine(libraryOutput, $"library-{width}x{height}.png"))) libraryEncoder.Save(libraryStream);
        f.Vm.IsTranslation = true;
        content.Measure(new System.Windows.Size(width - 16, height - 40)); content.Arrange(new System.Windows.Rect(0,0,width - 16,height - 40)); content.UpdateLayout();
        var grid = (System.Windows.Controls.DataGrid)window.FindName("PreviewGrid"); Assert.True(grid.ActualHeight >= 120);
        Assert.True(grid.Columns[1].Width.IsStar); Assert.True(grid.Columns[2].ActualWidth >= 110); Assert.True(grid.Columns[3].ActualWidth >= 120);
        var fileColumn = (System.Windows.Controls.DataGridTextColumn)grid.Columns[4]; Assert.Equal("FileName", fileColumn.Binding is System.Windows.Data.Binding binding ? binding.Path.Path : ""); Assert.Equal("FileToolTip", ((System.Windows.Data.Binding)((System.Windows.Setter)fileColumn.ElementStyle.Setters[0]).Value).Path.Path);
        Assert.Equal(50, grid.Columns[5].ActualWidth); Assert.Equal("dialogue.txt", f.Vm.Rows[0].FileName);
        var document = System.Xml.Linq.XDocument.Parse(File.ReadAllText(Path.Combine(root,"src/GameLocalizer.UI/Views/MainWindow.xaml")));
        var commands = document.Descendants().Select(e => (string?)e.Attribute("Command")).ToArray();
        foreach(var command in new[] { "InitializeCommand", "OpenGameFolderCommand", "AnalyzeCommand", "TranslateCommand", "ApplyCommand", "RestoreCommand", "CleanupCommand", "FindCommand", "OpenDiagnosticReportCommand", "OpenScanLogCommand", "InstallCollectorCommand", "RemoveCollectorCommand", "ImportCollectorCommand", "ExportCollectorCommand" })
            Assert.Contains("{Binding " + command + "}", commands);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width - 16, height - 40, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var output = Path.Combine(root, "artifacts/dev/panel-dock"); Directory.CreateDirectory(output); using var stream = File.Create(Path.Combine(output, $"main-{width}x{height}.png")); encoder.Save(stream);
        // Exercise the actual dock containers, not only preference arithmetic.
        void DockLayout()
        {
            for(var pass=0;pass<3;pass++)
            {
                GameLocalizer.UI.Views.MainWindow.ApplyDockPresentation(window,f.Vm.PanelLayout,f.Vm.HasGame);
                content.InvalidateMeasure();
                content.Measure(new System.Windows.Size(width-16,height-40));
                content.Arrange(new System.Windows.Rect(0,0,width-16,height-40)); content.UpdateLayout();
                GameLocalizer.UI.Views.MainWindow.ApplyLibraryPresentation(library,host,panel,false);
            }
        }
        f.Vm.IsLibrary = true; DockLayout();
        var layout = f.Vm.PanelLayout;
        var splitter = (System.Windows.Controls.GridSplitter)window.FindName("PanelSplitter");
        layout.Resize("Bottom",180,host.ActualHeight); DockLayout(); Assert.True(Math.Abs(panel.ActualHeight-180)<1, $"resize: pref={layout.BottomHeight}; panel={panel.ActualHeight}; desired={panel.DesiredSize.Height}; row={((System.Windows.Controls.Grid)window.FindName("LibrarySection")).RowDefinitions[2].Height}; rowActual={((System.Windows.Controls.Grid)window.FindName("LibrarySection")).RowDefinitions[2].ActualHeight}");
        layout.Resize("Bottom",9999,host.ActualHeight); DockLayout(); Assert.True(panel.ActualHeight <= host.ActualHeight*.6+1);
        layout.SetDock("Right"); layout.Resize("Right",320,host.ActualWidth); DockLayout();
        Assert.Equal(320,panel.ActualWidth,1); Assert.Equal(System.Windows.Controls.GridResizeDirection.Columns,splitter.ResizeDirection);
        Assert.Equal(1,((System.Windows.Controls.Primitives.UniformGrid)window.FindName("WorkflowSteps")).Columns);
        Assert.Equal(1,System.Windows.Controls.Grid.GetRow((System.Windows.FrameworkElement)window.FindName("PanelMetadata")));
        Assert.True(library.ActualWidth + panel.ActualWidth <= host.ActualWidth+1);
        var panelTop = panel.TranslatePoint(new System.Windows.Point(),host);
        Assert.True(panelTop.X >= library.ActualWidth);
        layout.Resize("Right",9999,host.ActualWidth); DockLayout(); Assert.True(panel.ActualWidth <= host.ActualWidth*.55+1);
        void SaveDock(string mode)
        {
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(width-16,height-40,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(content); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
            using var outputFile = File.Create(Path.Combine(libraryOutput,$"dock-{mode}-{width}x{height}.png")); png.Save(outputFile);
        }
        SaveDock("right");
        for(var index=0;index<16;index++) f.Vm.Games.Add(new(Guid.NewGuid().ToString("N"),$"Dock QA {index}",Path.Combine(f.Root,$"dock-{index}"),"Manual"));
        DockLayout();
        System.Windows.Controls.ScrollViewer? Scroll(System.Windows.DependencyObject item)
        {
            if(item is System.Windows.Controls.ScrollViewer scroll) return scroll;
            for(var index=0;index<System.Windows.Media.VisualTreeHelper.GetChildrenCount(item);index++)
                if(Scroll(System.Windows.Media.VisualTreeHelper.GetChild(item,index)) is {} found) return found;
            return null;
        }
        var viewport = Scroll(library)!; Assert.True(viewport.ScrollableHeight > 0);
        viewport.ScrollToEnd(); content.UpdateLayout(); Assert.True(viewport.VerticalOffset > 0);
        layout.SetCollapsed(true); DockLayout(); Assert.Equal(System.Windows.Visibility.Collapsed,splitter.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed,((System.Windows.Controls.ScrollViewer)window.FindName("PanelContentScroll")).Visibility);
        SaveDock("collapsed");
        var sizeBefore = (layout.BottomHeight,layout.RightWidth,layout.Dock,layout.Collapsed);
        f.Vm.SelectedGame = f.Second; DockLayout(); Assert.Equal(sizeBefore,(layout.BottomHeight,layout.RightWidth,layout.Dock,layout.Collapsed));
        f.Vm.IsSettings = true; f.Vm.IsLibrary = true; DockLayout(); Assert.Equal(sizeBefore,(layout.BottomHeight,layout.RightWidth,layout.Dock,layout.Collapsed));
        f.Vm.SelectedGame = null; DockLayout(); Assert.Equal(System.Windows.Visibility.Collapsed,panel.Visibility);
        Assert.Equal(host.ActualWidth,library.ActualWidth,1); SaveDock("no-selection");
        f.Vm.SelectedGame = f.First; DockLayout(); Assert.Equal(System.Windows.Visibility.Visible,panel.Visibility);
        layout.SetCollapsed(false); DockLayout(); Assert.Equal(System.Windows.Visibility.Visible,splitter.Visibility);
        var originalWidth = width; width = 1060;
        layout.SetDock("Right"); DockLayout();
        Assert.Equal("Right",layout.Dock); Assert.Equal(System.Windows.Controls.GridResizeDirection.Rows,splitter.ResizeDirection);
        Assert.True(library.ActualWidth > 600); Assert.True(panel.ActualHeight <= host.ActualHeight*.6+1);
        width = originalWidth;
        layout.Reset(); DockLayout(); Assert.Equal("Bottom",layout.Dock); Assert.False(layout.Collapsed);
        window.Close();
    });
    [Fact] public Task ShellNavigationPreservesGameFiltersAndManualTranslation() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); f.Vm.SelectedGame = f.First; await f.Vm.FindSelectedAsync();
        var row = Assert.Single(f.Vm.Rows); row.Russian = "Начать игру"; await f.Vm.FlushEditsAsync();
        f.Vm.LibrarySearch = "First"; f.Vm.LibrarySort = "Название";
        foreach(var section in new[] { "Перевод", "Проверка", "Библиотека" })
        {
            f.Vm.CurrentSection = section;
            Assert.Same(f.First,f.Vm.SelectedGame); Assert.Same(row,Assert.Single(f.Vm.Rows));
            Assert.Equal("Начать игру",row.Russian); Assert.Equal("First",f.Vm.LibrarySearch); Assert.Equal("Название",f.Vm.LibrarySort);
        }
        f.Vm.LibrarySearch = "Missing"; Assert.Empty(f.Vm.LibraryGames.Cast<Game>()); Assert.Same(f.First,f.Vm.SelectedGame);
        await f.Vm.ShutdownAsync();
    });
    [Fact] public Task ShellMainActionUsesWholeSelectedSetAndExistingCommands() => OnDispatcher(async () =>
    {
        using var f = new Fixture(); f.Vm.SelectedGame = f.First;
        Assert.Same(f.Vm.FindCommand,f.Vm.MainActionCommand);
        await f.Vm.FindSelectedAsync(); Assert.Same(f.Vm.TranslateCommand,f.Vm.MainActionCommand);
        f.Vm.Rows[0].Russian = "Начать игру"; await f.Vm.FlushEditsAsync();
        Assert.Same(f.Vm.ApplyCommand,f.Vm.MainActionCommand); Assert.Equal("✓ Готово",f.Vm.TranslationStage);
        f.Vm.Rows[0].Russian = ""; await f.Vm.FlushEditsAsync(); Assert.Same(f.Vm.TranslateCommand,f.Vm.MainActionCommand);
        await f.Vm.ShutdownAsync();
    });
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GameLocalizer.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new IOException("Repository root missing");
    }
    private static async Task SetupEmptyUi(Fixture f)
    {
        var path = Path.Combine(f.First.Path, "BepInEx/Translation/en/Text/ui.txt"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "おしゃべり=Chat\nはい=Yes\nいいえ=No");
        File.Delete(Path.Combine(f.First.Path, "dialogue.txt"));
        f.Vm.SelectedGame = f.First; await f.Vm.FindSelectedAsync();
        f.Vm.Rows.Single(r => r.Original == "Chat").Russian = "Чат";
        f.Vm.Rows.Single(r => r.Original == "No").Russian = "Нет"; await f.Vm.FlushEditsAsync();
        f.Vm.Rows.Single(r => r.Original == "No").Russian = " \t\u00A0"; await f.Vm.FlushEditsAsync();
    }
    [Fact] public Task EmptyApplyYesSkipsKeepsSelectionMemoryAndDiagnosticSummary() => OnDispatcher(async () =>
    {
        ApplySelectionSummary? shown = null; string? confirmed = null;
        using var f = new Fixture(emptyChoice: summary => { shown = summary; return "Да"; }, applyConfirmation: text => { confirmed = text; return true; });
        await SetupEmptyUi(f); await f.Vm.ApplySelectedAsync();
        Assert.NotNull(shown); Assert.Equal(3, shown.SelectedEntries); Assert.Equal(2, shown.SkippedEmptyTranslations); Assert.Contains("Пустых пропущено: 2", confirmed);
        var content = File.ReadAllText(Path.Combine(f.First.Path, "BepInEx/Translation/en/Text/ui.txt")); Assert.Equal("おしゃべり=Чат\nはい=Yes\nいいえ=No", content);
        Assert.All(f.Vm.Rows, r => Assert.True(r.Selected)); Assert.True(string.IsNullOrWhiteSpace(f.Vm.Rows.Single(r => r.Original == "No").Russian));
        Assert.Contains("Применено: 1 строк. Пропущено пустых: 2.", f.Vm.Status); Assert.True(f.Vm.ApplyCommand.CanExecute(null));
        var report = new ApplyDiagnosticReportLocator().FindLatest(f.First.Path); Assert.NotNull(report);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(report, ".json")));
        Assert.Equal(2, json.RootElement.GetProperty("SkippedEmptyTranslations").GetInt32()); Assert.Equal(3, json.RootElement.GetProperty("SelectedEntries").GetInt32());
        Assert.Equal("EmptyTranslation", json.RootElement.GetProperty("SkippedEmptyRows")[0].GetProperty("Reason").GetString());
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(f.Root, "memory.db")}"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM TranslationMemory WHERE TranslatedText=''"; Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT COUNT(*) FROM TranslationMemory WHERE TranslatedText='Нет'"; Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    });
    [Theory] [InlineData("Показать")][InlineData("Нет")]
    public Task EmptyApplyShowAndCancelNeverWrite(string choice) => OnDispatcher(async () =>
    {
        var confirmationCalled = false;
        using var f = new Fixture(emptyChoice: _ => choice, applyConfirmation: _ => { confirmationCalled = true; return true; });
        await SetupEmptyUi(f); var original = File.ReadAllBytes(Path.Combine(f.First.Path, "BepInEx/Translation/en/Text/ui.txt"));
        var filter = f.Vm.Filter; await f.Vm.ApplySelectedAsync(); Assert.False(confirmationCalled);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(f.First.Path, "BepInEx/Translation/en/Text/ui.txt"))); Assert.Equal(3, f.Vm.SelectedCount);
        if (choice == "Показать") { Assert.Equal("Пустые переводы", f.Vm.Filter); Assert.Equal(0, f.Vm.PageIndex); Assert.Equal(2, f.Vm.Rows.Count); Assert.All(f.Vm.Rows, r => Assert.True(string.IsNullOrWhiteSpace(r.Russian))); }
        else { Assert.Equal(filter, f.Vm.Filter); Assert.Equal(3, f.Vm.Rows.Count); }
    });
    [Fact] public Task NormalApplyBypassesEmptyDialogAndValidationStillBlocks() => OnDispatcher(async () =>
    {
        var emptyCalls = 0; using var f = new Fixture(emptyChoice: _ => { emptyCalls++; return "Да"; }, applyConfirmation: _ => true);
        f.Vm.SelectedGame = f.First; await f.Vm.FindSelectedAsync(); Assert.Single(f.Vm.Rows).Russian = "Начать игру";
        await f.Vm.ApplySelectedAsync(); Assert.Equal(0, emptyCalls); Assert.Equal("Начать игру", File.ReadAllText(Path.Combine(f.First.Path, "dialogue.txt")));
        await f.Vm.FindSelectedAsync(); f.Vm.Rows.Single().Russian = "{broken}";
        await f.Vm.ApplySelectedAsync(); Assert.Contains("Проверка перевода не пройдена", f.Vm.Status); Assert.Equal("Начать игру", File.ReadAllText(Path.Combine(f.First.Path, "dialogue.txt")));
        var failureReport = new ApplyDiagnosticReportLocator().FindLatest(f.First.Path); using var failureJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(failureReport!, ".json"))); Assert.Equal(1, failureJson.RootElement.GetProperty("ValidationErrorEntries").GetInt64());
    });
}

using System.Windows.Threading;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class ViewModelRegressionTests
{
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
        public Fixture(IEngineDetector? detector = null, Func<ApplySelectionSummary, string>? emptyChoice = null, Func<string, bool>? applyConfirmation = null, UiResourceDiscovery? uiDiscovery = null, bool persistent = false)
        {
            First = new("1", "First", Path.Combine(Root, "first"), "Steam"); Second = new("2", "Second", Path.Combine(Root, "second"), "Steam");
            Directory.CreateDirectory(First.Path); Directory.CreateDirectory(Second.Path);
            File.WriteAllText(Path.Combine(First.Path, "dialogue.txt"), "Start Game"); File.WriteAllText(Path.Combine(Second.Path, "dialogue.txt"), "Continue");
            var settings = new SettingsService(Root); settings.Save(new() { CheckUpdatesOnStartup = false });
            Repository = new(Path.Combine(Root, "scan.db"), persistent);
            Vm = new(new Discovery(First, Second), detector ?? new EngineDetector(), new(ScanRegressionTests.Adapters()), Repository,
                ScanRegressionTests.Workspace(Repository, Path.Combine(Root, "memory.db")), new(NullLogger<BackupService>.Instance), settings,
                new UpdateService(), NullLogger<MainViewModel>.Instance, diagnosticReports: new ApplyDiagnosticReportLocator(Path.Combine(Root, "reports")), emptyTranslationChoice: emptyChoice, confirmApply: applyConfirmation, uiDiscovery: uiDiscovery, scanDiagnostics: new ScanDiagnosticLog(Path.Combine(Root, "logs")));
            Vm.Games.Add(First); Vm.Games.Add(Second);
        }
        public void Dispose() { Repository.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
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
    [Theory] [InlineData(1366, 768)] [InlineData(1600, 900)] [InlineData(1920, 1080)]
    public Task MainScreenLayoutGroupsAndPreviewFit(int width, int height) => OnDispatcher(async () =>
    {
        using var f = new Fixture(); f.Vm.SelectedGame = f.First;
        f.Vm.Rows.Add(new TranslationRow { Id = 1, File = "BepInEx\\Translation\\en\\Text\\dialogue.txt", Key = "ありがとう", Original = string.Join(" ", Enumerable.Repeat("Thank you.", 50)), Russian = string.Join(" ", Enumerable.Repeat("Спасибо.", 50)), LocalizationSlot = "en", Confidence = .99, State = GameLocalizer.Core.Models.TranslationStatus.Translated });
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src/GameLocalizer.UI/Views/MainWindow.xaml"));
        source = source.Replace("x:Class=\"GameLocalizer.UI.Views.MainWindow\"", "");
        var app = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src/GameLocalizer.UI/App.xaml"));
        var theme = app.Root!.Elements().Single().Elements().Where(e => e.Name.LocalName != "Style" || (string?)e.Attribute("TargetType") != "Window");
        source = source.Replace("Style=\"{StaticResource {x:Type Window}}\"", "Background=\"#121B2A\" Foreground=\"#E5ECF5\" FontFamily=\"Segoe UI\" FontSize=\"13\"");
        source = source.Replace("<Window.Resources>", "<Window.Resources>" + string.Concat(theme.Select(e => e.ToString())));
        var window = (System.Windows.Window)System.Windows.Markup.XamlReader.Parse(source); window.DataContext = f.Vm;
        var content = (System.Windows.FrameworkElement)window.Content;
        ((System.Windows.Controls.Grid)content).Background = window.Background;
        content.Measure(new System.Windows.Size(width - 16, height - 40)); content.Arrange(new System.Windows.Rect(0, 0, width - 16, height - 40)); content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); content.InvalidateMeasure(); content.Measure(new System.Windows.Size(width - 16, height - 40)); content.Arrange(new System.Windows.Rect(0, 0, width - 16, height - 40)); content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); content.UpdateLayout();
        var apply = (System.Windows.Controls.StackPanel)window.FindName("ApplyActions");
        var buttons = apply.Children.OfType<System.Windows.Controls.Button>().ToArray();
        Assert.Equal(new[] { "Применить", "Восстановить", "Убрать" }, buttons.Select(b => b.Content.ToString()));
        Assert.Same(f.Vm.ApplyCommand, buttons[0].Command); Assert.Same(f.Vm.RestoreCommand, buttons[1].Command); Assert.Same(f.Vm.CleanupCommand, buttons[2].Command);
        var toolbar = (System.Windows.Controls.WrapPanel)window.FindName("ActionToolbar");
        foreach (System.Windows.FrameworkElement group in toolbar.Children)
        {
            var point = group.TranslatePoint(new System.Windows.Point(), content);
            Assert.True(point.X >= 0 && point.X + group.ActualWidth <= content.ActualWidth + 1);
        }
        var grid = (System.Windows.Controls.DataGrid)window.FindName("PreviewGrid"); Assert.True(grid.ActualHeight >= 120);
        Assert.True(grid.Columns[1].Width.IsStar); Assert.True(grid.Columns[2].ActualWidth >= 110); Assert.True(grid.Columns[3].ActualWidth >= 120);
        var fileColumn = (System.Windows.Controls.DataGridTextColumn)grid.Columns[4]; Assert.Equal("FileName", fileColumn.Binding is System.Windows.Data.Binding binding ? binding.Path.Path : ""); Assert.Equal("FileToolTip", ((System.Windows.Data.Binding)((System.Windows.Setter)fileColumn.ElementStyle.Setters[0]).Value).Path.Path);
        Assert.Equal(50, grid.Columns[5].ActualWidth); Assert.Equal("dialogue.txt", f.Vm.Rows[0].FileName);
        var report = ((System.Windows.Controls.StackPanel)window.FindName("DiagnosticActions")).Children.OfType<System.Windows.Controls.Button>().Single(b => b.Command == f.Vm.OpenDiagnosticReportCommand);
        Assert.True(System.Windows.Controls.ToolTipService.GetShowOnDisabled(report)); Assert.Same(f.Vm.OpenDiagnosticReportCommand, report.Command);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width - 16, height - 40, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var output = Path.Combine(root, "artifacts/dev/ui-diagnostics"); Directory.CreateDirectory(output); using var stream = File.Create(Path.Combine(output, $"main-{width}x{height}.png")); encoder.Save(stream);
        window.Close();
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
        await f.Vm.ApplySelectedAsync(); Assert.Contains("Validation Error", f.Vm.Status); Assert.Equal("Начать игру", File.ReadAllText(Path.Combine(f.First.Path, "dialogue.txt")));
        var failureReport = new ApplyDiagnosticReportLocator().FindLatest(f.First.Path); using var failureJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(failureReport!, ".json"))); Assert.Equal(1, failureJson.RootElement.GetProperty("ValidationErrorEntries").GetInt64());
    });
}

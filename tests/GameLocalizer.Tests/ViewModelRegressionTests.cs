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
    private static Task OnDispatcher(Func<Task> action)
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "GameLocalizerVmTests", Guid.NewGuid().ToString("N"));
        public MainViewModel Vm { get; }
        public ScanResultRepository Repository { get; }
        public Game First { get; }
        public Game Second { get; }
        public Fixture(IEngineDetector? detector = null)
        {
            First = new("1", "First", Path.Combine(Root, "first"), "Steam"); Second = new("2", "Second", Path.Combine(Root, "second"), "Steam");
            Directory.CreateDirectory(First.Path); Directory.CreateDirectory(Second.Path);
            File.WriteAllText(Path.Combine(First.Path, "dialogue.txt"), "Start Game"); File.WriteAllText(Path.Combine(Second.Path, "dialogue.txt"), "Continue");
            var settings = new SettingsService(Root); settings.Save(new() { CheckUpdatesOnStartup = false });
            Repository = new(Path.Combine(Root, "scan.db"));
            Vm = new(new Discovery(First, Second), detector ?? new EngineDetector(), new(ScanRegressionTests.Adapters()), Repository,
                ScanRegressionTests.Workspace(Repository, Path.Combine(Root, "memory.db")), new(NullLogger<BackupService>.Instance), settings,
                new UpdateService(), NullLogger<MainViewModel>.Instance);
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
    });
}

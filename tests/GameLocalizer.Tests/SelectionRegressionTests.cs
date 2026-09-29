using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using GameLocalizer.Core.Models;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class SelectionRegressionTests
{
    [Fact]
    public void GameIdentityDoesNotChangeWhenAnalysisOrSubscriptionsChange()
    {
        var game = new Game("1", "First", "C:/First", "Steam");
        var hash = game.GetHashCode();
        game.PropertyChanged += (_, _) => { };
        game.Engine = "Unity"; game.Status = "Строк: 20";
        Assert.Equal(hash, game.GetHashCode());
        Assert.NotEqual(game, new Game("1", "First", "C:/First", "Steam"));
    }

    [Fact]
    public async Task WpfSelectionAfterAnalysisUpdatesBindingRepeatedly()
    {
        await OnSta(() =>
        {
            var first = new Game("1", "First", "C:/First", "Steam");
            var second = new Game("2", "Second", "C:/Second", "Steam");
            var source = new SelectionSource();
            var list = new ListBox { ItemsSource = new ObservableCollection<Game> { first, second }, IsSynchronizedWithCurrentItem = false };
            BindingOperations.SetBinding(list, ListBox.SelectedItemProperty, new Binding(nameof(source.SelectedGame)) { Source = source, Mode = BindingMode.TwoWay });
            for (var i = 0; i < 10; i++)
            {
                list.SelectedItem = first;
                first.Engine = "Unity"; first.Status = "Analyzed " + i;
                list.SelectedItem = second;
                Assert.Same(second, source.SelectedGame);
                Assert.Same(second, list.SelectedItem);
                second.Engine = "Unreal"; second.Status = "Analyzed " + i;
            }
        });
    }

    internal static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private sealed class SelectionSource : INotifyPropertyChanged
    {
        private Game? selected;
        public Game? SelectedGame { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(SelectedGame))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

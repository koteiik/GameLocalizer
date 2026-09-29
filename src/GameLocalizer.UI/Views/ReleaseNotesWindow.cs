using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GameLocalizer.UI.ViewModels;

namespace GameLocalizer.UI.Views;

public sealed class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(UpdateViewModel updates)
    {
        Style = (Style)FindResource(typeof(Window)); DataContext = updates; Title = updates.NotesTitle;
        Width = 760; Height = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(20) }; Content = panel;
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var update = new Button { Content = "Обновить", Command = updates.UpdateCommand }; actions.Children.Add(update);
        var close = new Button { Content = "Закрыть" }; close.Click += (_, _) => Close(); actions.Children.Add(close);
        var reason = new TextBlock { TextWrapping = TextWrapping.Wrap }; reason.SetBinding(TextBlock.TextProperty, new Binding("BlockReason")); DockPanel.SetDock(reason, Dock.Bottom); panel.Children.Add(reason);
        // Plain text only: remote notes cannot execute markup, commands or navigation.
        panel.Children.Add(new TextBox { Text = updates.Notes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        System.ComponentModel.PropertyChangedEventHandler changed = (_, e) => { if (e.PropertyName == nameof(updates.Busy) && updates.Busy) Close(); };
        updates.PropertyChanged += changed; Closed += (_, _) => updates.PropertyChanged -= changed;
    }
}

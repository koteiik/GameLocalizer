using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;
namespace GameLocalizer.UI.Views;

public sealed class AboutWindow : Window
{
    public AboutWindow(string repository, UpdateViewModel updates)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "О программе"; Width = 440; Height = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(25) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "GameLocalizer\n" + ApplicationVersion.Label + "\nMIT License", FontSize = 24, Margin = new Thickness(0, 0, 0, 15) });
        DataContext = updates;
        System.ComponentModel.PropertyChangedEventHandler changed = (_, e) => { if (e.PropertyName == nameof(updates.Busy) && updates.Busy) Close(); };
        updates.PropertyChanged += changed; Closed += (_, _) => updates.PropertyChanged -= changed;
        var latest = new TextBlock(); latest.SetBinding(TextBlock.TextProperty, new Binding("LatestVersion") { StringFormat = "Последняя версия: {0}" }); panel.Children.Add(latest);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; status.SetBinding(TextBlock.TextProperty, new Binding("Status")); panel.Children.Add(status);
        panel.Children.Add(new Button { Content = "Проверить обновления", Command = updates.CheckCommand });
        panel.Children.Add(new Button { Content = "Обновить", Command = updates.UpdateCommand });
        panel.Children.Add(new Button { Content = "Что нового", Command = updates.NotesCommand });
        var blocked = new TextBlock { TextWrapping = TextWrapping.Wrap }; blocked.SetBinding(TextBlock.TextProperty, new Binding("BlockReason")); panel.Children.Add(blocked);
        foreach (var (label, suffix) in new[] { ("GitHub", ""), ("Releases", "/releases"), ("License", "/blob/main/LICENSE") })
        {
            var button = new Button { Content = label, IsEnabled = UpdateService.ValidRepository(repository) };
            button.Click += (_, _) => MainViewModel.Open("https://github.com/" + repository + suffix); panel.Children.Add(button);
        }
    }
}

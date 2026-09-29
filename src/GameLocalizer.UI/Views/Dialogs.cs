using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;
namespace GameLocalizer.UI.Views;

public sealed class AboutWindow : Window
{
    public AboutWindow(string repository)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "О программе"; Width = 440; Height = 330; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(25) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "GameLocalizer\n" + ApplicationVersion.Label + "\nMIT License", FontSize = 24, Margin = new Thickness(0, 0, 0, 15) });
        foreach (var (label, suffix) in new[] { ("GitHub", ""), ("Releases", "/releases"), ("License", "/blob/main/LICENSE") })
        {
            var button = new Button { Content = label, IsEnabled = UpdateService.ValidRepository(repository) };
            button.Click += (_, _) => MainViewModel.Open("https://github.com/" + repository + suffix); panel.Children.Add(button);
        }
    }
}
public sealed class UpdateWindow : Window
{
    public UpdateWindow(string version)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "Обновление"; Width = 460; Height = 190; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Доступна новая версия GameLocalizer " + version, TextWrapping = TextWrapping.Wrap });
        var download = new Button { Content = "Скачать" }; download.Click += (_, _) => { DialogResult = true; }; panel.Children.Add(download);
        var later = new Button { Content = "Позже" }; later.Click += (_, _) => { DialogResult = false; }; panel.Children.Add(later);
    }
}

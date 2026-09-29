using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.TranslationProviders;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;
namespace GameLocalizer.UI.Views;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, OfflineSettingsViewModel? offline = null, GlossaryService? glossary = null)
    {
        Style = (Style)FindResource(typeof(Window)); Title = "Настройки → Offline Translation → Models"; Width = 760; Height = 780; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Переводчик · EN → RU", FontSize = 22 });
        var providers = new ComboBox { ItemsSource = new[] { "Offline", "Mock" }, SelectedItem = settings.TranslationProvider, Margin = new Thickness(4) }; panel.Children.Add(providers);
        if (offline != null)
        {
            panel.Children.Add(new TextBlock { Text = offline.ModelInfo, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            var modelPanel = new StackPanel { DataContext = offline }; panel.Children.Add(modelPanel);
            var state = new TextBlock { FontWeight = FontWeights.Bold }; state.SetBinding(TextBlock.TextProperty, new Binding("ReadyDescription")); modelPanel.Children.Add(state);
            var hardware = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }; hardware.SetBinding(TextBlock.TextProperty, new Binding("HardwareText")); modelPanel.Children.Add(hardware);
            var options = new WrapPanel(); modelPanel.Children.Add(options);
            options.Children.Add(new TextBlock { Text = "Device", VerticalAlignment = VerticalAlignment.Center });
            var devices = new ComboBox { ItemsSource = offline.Devices, Width = 120 }; devices.SetBinding(ComboBox.SelectedItemProperty, new Binding("Settings.Device") { Mode = BindingMode.TwoWay }); options.Children.Add(devices);
            options.Children.Add(new TextBlock { Text = "Batch size", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) });
            var batch = new ComboBox { ItemsSource = new[] { 1, 2, 4, 8, 16 }, Width = 90 }; batch.SetBinding(ComboBox.SelectedItemProperty, new Binding("Settings.BatchSize") { Mode = BindingMode.TwoWay }); options.Children.Add(batch);
            var keep = new CheckBox { Content = "Memory cache: оставлять модель загруженной", Margin = new Thickness(4, 12, 0, 12) }; keep.SetBinding(CheckBox.IsCheckedProperty, new Binding("Settings.KeepModelLoaded") { Mode = BindingMode.TwoWay }); modelPanel.Children.Add(keep);
            var actions = new WrapPanel(); modelPanel.Children.Add(actions);
            foreach (var (label, command) in new[] { ("Скачать модель", "DownloadCommand"), ("Проверить SHA256", "VerifyCommand"), ("Удалить модель", "DeleteCommand"), ("Остановить загрузку", "CancelCommand") })
            { var button = new Button { Content = label }; button.SetBinding(Button.CommandProperty, new Binding(command)); actions.Children.Add(button); }
            var progress = new ProgressBar { Height = 7, Maximum = 100, Margin = new Thickness(4, 8, 4, 8) }; progress.SetBinding(ProgressBar.ValueProperty, new Binding("Progress") { Mode = BindingMode.OneWay }); modelPanel.Children.Add(progress);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; status.SetBinding(TextBlock.TextProperty, new Binding("Status")); modelPanel.Children.Add(status);
            providers.SelectionChanged += (_, _) =>
            {
                settings.TranslationProvider = providers.SelectedItem?.ToString() ?? "Offline";
                if (settings.TranslationProvider == "Offline" && !offline.IsInstalled)
                {
                    var choice = new ChoiceWindow("Локальная модель EN → RU", "Для офлайн-перевода требуется локальная модель EN → RU.\n\n" + offline.ModelInfo, "Скачать модель", "Отмена") { Owner = this };
                    if (choice.ShowDialog() == true) offline.DownloadCommand.Execute(null);
                }
            };
            Closing += (_, e) => { if (offline.Busy) { offline.CancelCommand.Execute(null); e.Cancel = true; } };
        }
        else providers.SelectionChanged += (_, _) => settings.TranslationProvider = providers.SelectedItem?.ToString() ?? "Mock";
        panel.Children.Add(new TextBlock { Text = "Модель загружается только при переводе. После Apply игра читает сохранённые файлы; GameLocalizer можно закрыть. Mock — только демонстрация.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 15) });
        if (glossary != null) { var button = new Button { Content = "Glossary / Names · импорт и экспорт CSV / JSON" }; button.Click += (_, _) => new GlossaryWindow(glossary) { Owner = this }.ShowDialog(); panel.Children.Add(button); }
        var updates = new CheckBox { Content = "Проверять обновления при запуске", IsChecked = settings.CheckUpdatesOnStartup }; updates.Click += (_, _) => settings.CheckUpdatesOnStartup = updates.IsChecked == true; panel.Children.Add(updates);
        panel.Children.Add(new TextBlock { Text = "GitHub repository:" }); var repository = new TextBox { Text = settings.GitHubRepository }; panel.Children.Add(repository);
        var save = new Button { Content = "Сохранить и закрыть" }; panel.Children.Add(save);
        save.Click += (_, _) => { if (!UpdateService.ValidRepository(repository.Text)) { MessageBox.Show("Введите owner/repository"); return; } settings.GitHubRepository = repository.Text; Close(); };
    }
}

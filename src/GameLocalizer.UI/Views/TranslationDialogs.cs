using System.Windows;
using System.Windows.Controls;
using GameLocalizer.Core.Models;
namespace GameLocalizer.UI.Views;

public sealed class ChoiceWindow : Window
{
    public string Choice { get; private set; } = "Отмена";
    public ChoiceWindow(string title, string text, params string[] choices)
    {
        Style = (Style)FindResource(typeof(Window)); Title = title; Width = 660; SizeToContent = SizeToContent.Height; MaxHeight = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice, IsDefault = choice == choices[0], IsCancel = choice == "Отмена" };
            button.Click += (_, _) => { Choice = choice; DialogResult = choice != "Отмена"; }; buttons.Children.Add(button);
        }
    }
}
public sealed class TranslationTestWindow : Window
{
    public TranslationTestWindow(IReadOnlyList<ScanRow> rows)
    {
        Style = (Style)FindResource(typeof(Window)); Title = "Тест 20 строк · игровые файлы не изменены"; Width = 1000; Height = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var table = new DataGrid { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = false, Margin = new Thickness(20), CanUserAddRows = false };
        foreach (var column in new[] { ("Key", "DisplayKey"), ("Original", "Original"), ("Russian", "Translation"), ("Category", "Category"), ("Status", "Status") })
            table.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new System.Windows.Data.Binding(column.Item2), Width = new DataGridLength(column.Item1 is "Original" or "Russian" ? 3 : 1, DataGridLengthUnitType.Star) });
        Content = table;
    }
}

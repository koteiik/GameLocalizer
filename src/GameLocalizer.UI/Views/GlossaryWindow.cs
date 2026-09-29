using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.TranslationProviders;
using Microsoft.Win32;
namespace GameLocalizer.UI.Views;

public sealed class GlossaryWindow : Window
{
    public sealed class Row
    {
        public string Original { get; set; } = "";
        public string Russian { get; set; } = "";
        public bool CaseSensitive { get; set; } = true;
        public string Category { get; set; } = "";
    }
    public GlossaryWindow(GlossaryService service)
    {
        Style = (Style)FindResource(typeof(Window)); Title = "Glossary / Names"; Width = 860; Height = 570; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rows = new ObservableCollection<Row>();
        void Reload() { rows.Clear(); foreach (var e in service.Entries) rows.Add(new() { Original = e.Original, Russian = e.Russian, CaseSensitive = e.CaseSensitive, Category = e.Category ?? "" }); }
        Reload();
        var panel = new DockPanel { Margin = new Thickness(20) }; Content = panel;
        var help = new TextBlock { Text = "Безопасный режим: точное совпадение целой строки. Names: Alice → Алиса. Слова внутри предложений не заменяются — это может нарушить падежи. Пустая Category означает все категории.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15) }; DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var table = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true }; panel.Children.Add(table);
        table.Columns.Add(new DataGridTextColumn { Header = "Original", Binding = new Binding("Original"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn { Header = "Russian", Binding = new Binding("Russian"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridCheckBoxColumn { Header = "CaseSensitive", Binding = new Binding("CaseSensitive") });
        table.Columns.Add(new DataGridComboBoxColumn { Header = "Category", ItemsSource = new[] { "", "Names", "UI", "Dialogue", "Subtitle", "Localization", "Possible" }, SelectedItemBinding = new Binding("Category"), Width = 130 });
        void Save() { table.CommitEdit(); table.CommitEdit(DataGridEditingUnit.Row, true); service.Save(rows.Select(r => new GlossaryEntry(r.Original, r.Russian, r.CaseSensitive, r.Category.Length == 0 ? null : r.Category)).ToArray()); }
        void Action(string name, Action action) { var button = new Button { Content = name }; button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(e.Message, "Glossary"); } }; buttons.Children.Add(button); }
        Action("Сохранить", Save);
        Action("Импорт CSV / JSON", () => { var dialog = new OpenFileDialog { Filter = "Glossary|*.json;*.csv" }; if (dialog.ShowDialog(this) == true) { service.Import(dialog.FileName); Reload(); } });
        Action("Экспорт", () => { Save(); var dialog = new SaveFileDialog { Filter = "JSON|*.json|CSV|*.csv", FileName = "glossary" }; if (dialog.ShowDialog(this) == true) service.Export(dialog.FileName); });
        Action("Закрыть", Close);
    }
}

using System.Windows;
namespace GameLocalizer.UI.Views;
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InitializePresentation();
        DataContextChanged += (_, e) =>
        {
            if(e.OldValue is ViewModels.MainViewModel old) { old.PropertyChanged -= ShellChanged; old.PanelLayout.PropertyChanged -= PanelLayoutChanged; }
            if(e.NewValue is ViewModels.MainViewModel vm) { vm.PropertyChanged += ShellChanged; vm.PanelLayout.PropertyChanged += PanelLayoutChanged;
                foreach(var column in PreviewGrid.Columns.OfType<System.Windows.Controls.DataGridTextColumn>().Where(c => c.Header?.ToString() is "Тип ошибки" or "Причина ошибки" or "Повтор"))
                    System.Windows.Data.BindingOperations.SetBinding(column, System.Windows.Controls.DataGridColumn.VisibilityProperty, new System.Windows.Data.Binding(nameof(ViewModels.MainViewModel.ShowingTranslationErrors)) { Source = vm, Converter = new System.Windows.Controls.BooleanToVisibilityConverter() });
                UpdateCards(); }
        };
        LibraryGrid.SizeChanged += (_,_) => UpdateCards();
    }
    private void ShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if(e.PropertyName is nameof(ViewModels.MainViewModel.LibraryList) or nameof(ViewModels.MainViewModel.LibrarySearch) or nameof(ViewModels.MainViewModel.CurrentSection) or nameof(ViewModels.MainViewModel.SelectedGame)) UpdateCards(); }
    private void UpdateCards() => QueueLibraryPresentation();
    private void GameSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if(DataContext is ViewModels.MainViewModel vm && e.AddedItems.Count > 0 && e.AddedItems[0] is GameLocalizer.Core.Models.Game g) vm.SelectedGame = g; }
    private void AdvancedClick(object sender, RoutedEventArgs e)
    { if(sender is System.Windows.Controls.Button b && b.ContextMenu != null) { b.ContextMenu.DataContext = DataContext; b.ContextMenu.PlacementTarget = b; b.ContextMenu.IsOpen = true; } }
    private void OpenRuntimeClick(object sender,RoutedEventArgs e)
    { if(DataContext is ViewModels.MainViewModel vm) { vm.IsTranslation = true; vm.PreviewTabIndex = 4; } }
    private void RuntimeGlossaryClick(object sender,RoutedEventArgs e) {if(DataContext is ViewModels.MainViewModel vm)vm.EditRuntimeGlossary();}
    private async void RuntimeReprocessClick(object sender,RoutedEventArgs e) {if(DataContext is ViewModels.MainViewModel vm)await vm.ReprocessRuntimeAsync();}
    private async void RuntimeRetranslateClick(object sender,RoutedEventArgs e)
    {if(DataContext is ViewModels.MainViewModel vm)await vm.RetranslateRuntimeSelectedAsync(RuntimeGrid.SelectedItems.Cast<GameLocalizer.Infrastructure.Runtime.RuntimeUiEntry>().ToArray());}
    private void RuntimeFilterChanged(object sender,RoutedEventArgs e)
    {
        if(DataContext is not ViewModels.MainViewModel vm||RuntimeGrid==null)return;
        vm.SuspiciousRuntimeOnly=(sender as System.Windows.Controls.CheckBox)?.IsChecked==true;
        var view=System.Windows.Data.CollectionViewSource.GetDefaultView(RuntimeGrid.ItemsSource);if(view!=null){view.Filter=vm.RuntimeFilter;view.Refresh();}
    }
}

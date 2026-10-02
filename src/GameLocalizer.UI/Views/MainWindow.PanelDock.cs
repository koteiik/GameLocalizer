using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GameLocalizer.UI.ViewModels;

namespace GameLocalizer.UI.Views;

public partial class MainWindow
{
    private bool panelResizing;
    private void PanelLayoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateCards();
    private void PanelPositionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private void PanelDockClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is MenuItem item) vm.PanelLayout.SetDock(item.Tag?.ToString() ?? "Bottom");
    }
    private void PanelCollapseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.PanelLayout.SetCollapsed(!vm.PanelLayout.Collapsed);
    }
    private void PanelResizeStarted(object sender, DragStartedEventArgs e) => panelResizing = true;
    private void PanelResizeDelta(object sender, DragDeltaEventArgs e) => UpdateCards();
    private void PanelResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        panelResizing = false;
        if (!e.Canceled && DataContext is MainViewModel vm)
        {
            var geometry = vm.PanelLayout.Geometry(LibraryHost.ActualWidth, LibraryHost.ActualHeight, vm.HasGame);
            vm.PanelLayout.Resize(geometry.Dock, geometry.Dock == "Right" ? LibrarySection.ColumnDefinitions[2].ActualWidth : LibrarySection.RowDefinitions[2].ActualHeight,
                geometry.Dock == "Right" ? LibraryHost.ActualWidth : LibraryHost.ActualHeight);
        }
        UpdateCards();
    }
    public static PanelGeometry ApplyDockPresentation(Window window, SelectedPanelLayout layout, bool hasGame)
    {
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var host = Find<Grid>("LibraryHost");
        var grid = Find<Grid>("LibrarySection");
        var panel = Find<Border>("SelectedGamePanel");
        var splitter = Find<GridSplitter>("PanelSplitter");
        var geometry = layout.Geometry(host.ActualWidth, host.ActualHeight, hasGame);
        var right = geometry.Dock == "Right";
        panel.Visibility = hasGame ? Visibility.Visible : Visibility.Collapsed;
        splitter.Visibility = geometry.Resizable ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < 3; i++)
        {
            var row = grid.RowDefinitions[i]; var column = grid.ColumnDefinitions[i];
            row.MinHeight = 0; row.MaxHeight = double.PositiveInfinity;
            column.MinWidth = 0; column.MaxWidth = double.PositiveInfinity;
            row.Height = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(right ? 0 : i == 1 ? geometry.Resizable ? 8 : 0 : geometry.Size);
            column.Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(!right ? 0 : i == 1 ? geometry.Resizable ? 8 : 0 : geometry.Size);
        }
        if (geometry.Resizable)
        {
            if (right) { grid.ColumnDefinitions[2].MinWidth = geometry.Minimum; grid.ColumnDefinitions[2].MaxWidth = geometry.Maximum; }
            else { grid.RowDefinitions[2].MinHeight = geometry.Minimum; grid.RowDefinitions[2].MaxHeight = geometry.Maximum; }
        }
        Place(panel, right ? 0 : 2, right ? 2 : 0, right ? 3 : 1, right ? 1 : 3);
        Place(splitter, right ? 0 : 1, right ? 1 : 0, right ? 3 : 1, right ? 1 : 3);
        splitter.ResizeDirection = right ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        splitter.Cursor = right ? Cursors.SizeWE : Cursors.SizeNS;
        Find<ScrollViewer>("PanelContentScroll").Visibility = layout.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        Find<Button>("PanelCollapseButton").Content = layout.Collapsed ? "Развернуть" : "Свернуть";
        var header = Find<DockPanel>("PanelHeader");
        DockPanel.SetDock(header.Children[0], right ? Dock.Top : Dock.Right);
        header.Children[0].SetValue(FrameworkElement.MarginProperty, right ? new Thickness(0,0,0,8) : new Thickness(10,0,0,0));
        var body = Find<Grid>("PanelBody");
        body.ColumnDefinitions[0].Width = right ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        body.ColumnDefinitions[1].Width = right ? new GridLength(0) : GridLength.Auto;
        body.ColumnDefinitions[2].Width = right ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        var poster = Find<Border>("PanelPoster");
        var metadata = Find<StackPanel>("PanelMetadata");
        var workflow = Find<Grid>("PanelWorkflow");
        Place(poster, 0, 0, 1, right ? 3 : 1);
        Place(metadata, right ? 1 : 0, right ? 0 : 1, 1, right ? 3 : 1);
        Place(workflow, right ? 2 : 0, right ? 0 : 2, 1, right ? 3 : 1);
        var compact = host.ActualWidth < 1100 || host.ActualHeight < 680;
        poster.Width = right ? double.NaN : compact ? 96 : 132;
        poster.Height = right ? 130 : compact ? 176 : 224;
        poster.Margin = right ? new Thickness(0,0,0,14) : new Thickness(0,0,16,0);
        metadata.Width = right ? double.NaN : compact ? 194 : 244;
        metadata.Margin = right ? new Thickness(0,0,0,16) : new Thickness(0,0,20,0);
        Find<UniformGrid>("WorkflowSteps").Columns = right ? 1 : 3;
        var actions = Find<Grid>("PanelActions");
        actions.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        actions.ColumnDefinitions[1].Width = new GridLength(0);
        Find<Button>("PrimaryAction").Margin = new Thickness(0);
        return geometry;
    }
    private static void Place(FrameworkElement element, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(element,row); Grid.SetColumn(element,column); Grid.SetRowSpan(element,rowSpan); Grid.SetColumnSpan(element,columnSpan);
    }
}

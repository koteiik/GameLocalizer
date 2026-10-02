using System.Windows;
using System.Windows.Controls;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.UI.Views;

namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    public async Task ReprocessRuntimeAsync()
    {
        if(!Idle||game==null||runtimeDictionary==null)return;
        if(MessageBox.Show("Будут повторно обработаны все автоматические Runtime UI переводы.\nРучные переводы не изменятся.","Пересчитать Runtime переводы",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
        var selected=game;Busy=true;updatingRuntimeRows=true;using var cts=new CancellationTokenSource();cancellation=cts;
        try
        {
            await RuntimeEditSaveTask;
            var rows=runtimeDictionary.LoadExisting(selected.Path,RuntimeUi.ToArray());
            var preview=await runtimeDictionary.PreviewReprocessAsync(selected,rows,cts.Token);
            var window=new Window{Title="Пересчитать Runtime переводы — только изменения",Width=1100,Height=550,Owner=Application.Current?.MainWindow};
            var panel=new DockPanel();var buttons=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);
            var grid=new DataGrid{ItemsSource=preview.Changes,AutoGenerateColumns=false,IsReadOnly=true};
            foreach(var (header,path) in new[]{("Original","Row.Text"),("Old Russian","OldRussian"),("New Russian","NewRussian"),("Context","Context"),("Old Source","OldSource"),("New Source","NewSource")})grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new System.Windows.Data.Binding(path),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
            panel.Children.Add(grid);window.Content=panel;bool apply=false;
            var accept=new Button{Content="Применить все",Margin=new Thickness(8)};accept.Click+=(_,_)=>{apply=true;window.Close();};buttons.Children.Add(accept);
            var cancel=new Button{Content="Отмена",Margin=new Thickness(8)};cancel.Click+=(_,_)=>window.Close();buttons.Children.Add(cancel);window.ShowDialog();
            if(!apply)return;
            await runtimeDictionary.ApplyReprocessAsync(selected,preview,cts.Token);
            var deployed=await runtimeDictionary.RebuildAndDeployAsync(selected,rows,cts.Token);
            RuntimeUi.Clear();foreach(var row in rows){AttachRuntimeRow(row,selected);RuntimeUi.Add(row);}
            RuntimeCollectorStatus=$"Пересчитано {rows.Count}; изменений {preview.Changes.Count}; ручных сохранено {preview.ManualPreserved}. Установлено {deployed.EntryCount} строк. SHA256 совпадают. Перезапустите игру.";
        }
        catch(OperationCanceledException){RuntimeCollectorStatus="Пересчёт отменён.";}
        catch(Exception e){RuntimeCollectorStatus="Ошибка пересчёта: "+e.Message;}
        finally{Busy=false;updatingRuntimeRows=false;cancellation=null;}
    }
    public void EditRuntimeGlossary()
    {
        if(runtimeDictionary==null||!Idle)return;
        new RuntimeGlossaryWindow(runtimeDictionary.Glossary){Owner=Application.Current?.MainWindow}.ShowDialog();
    }
    public bool SuspiciousRuntimeOnly {get;set;}
    public bool RuntimeFilter(object value) => !SuspiciousRuntimeOnly || value is RuntimeUiEntry row && row.Text.Length<=20&&row.SeenCount>=3&&row.TranslationSource is "Offline" or "OfflineModel"&&runtimeDictionary?.Glossary.Match(row)==null;
    public async Task RetranslateRuntimeSelectedAsync(IReadOnlyList<RuntimeUiEntry> selected)
    {
        if(!Idle||game==null||runtimeDictionary==null||selected.Count==0)return;
        var current=game;Busy=true;updatingRuntimeRows=true;
        using var cts=new CancellationTokenSource();cancellation=cts;
        try
        {
            await RuntimeEditSaveTask;
            var preview=await runtimeDictionary.PreviewAsync(current,selected,cts.Token);
            if(preview.Count==0){RuntimeCollectorStatus="Нет изменений; ручные переводы сохранены.";return;}
            var window=new Window{Title="Перевести заново с UI-контекстом",Width=1000,Height=550,Owner=Application.Current?.MainWindow};
            var panel=new DockPanel();var buttons=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);
            var grid=new DataGrid{ItemsSource=preview,IsReadOnly=true,AutoGenerateColumns=false,SelectionMode=DataGridSelectionMode.Extended};
            foreach(var (header,path) in new[]{("Original","Row.Text"),("Old Russian","OldRussian"),("New Russian","NewRussian"),("Context","Context"),("Source","Source")})grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new System.Windows.Data.Binding(path),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
            panel.Children.Add(grid);window.Content=panel;grid.SelectAll();bool apply=false;
            var accept=new Button{Content="Применить выбранные",Margin=new Thickness(8)};accept.Click+=(_,_)=>{apply=true;window.Close();};buttons.Children.Add(accept);
            var keep=new Button{Content="Оставить старый",Margin=new Thickness(8)};keep.Click+=(_,_)=>window.Close();buttons.Children.Add(keep);
            window.ShowDialog();
            if(apply){await runtimeDictionary.ApplyAsync(current,RuntimeUi.ToArray(),grid.SelectedItems.Cast<RuntimeDictionaryService.Retranslation>().ToArray(),cts.Token);RuntimeCollectorStatus="Исправления сохранены. Нажмите «Обновить Runtime словарь».";}
        }
        catch(OperationCanceledException){RuntimeCollectorStatus="Перевод отменён.";}
        catch(Exception e){RuntimeCollectorStatus="Ошибка: "+e.Message;}
        finally{Busy=false;updatingRuntimeRows=false;cancellation=null;}
    }
}

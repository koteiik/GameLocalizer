using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GameLocalizer.Infrastructure.Runtime;
using Microsoft.Win32;

namespace GameLocalizer.UI.Views;
public sealed class RuntimeGlossaryWindow : Window
{
    public sealed class EditableEntry
    {
        public string Original {get;set;}="";
        public string Russian {get;set;}="";
        public string Context {get;set;}="";
        public bool Enabled {get;set;}=true;
    }
    public RuntimeGlossaryWindow(RuntimeUiGlossary glossary)
    {
        Title="Runtime/UI glossary";Width=850;Height=600;
        var rows=new ObservableCollection<EditableEntry>();
        void Load(){rows.Clear();foreach(var e in glossary.Load())rows.Add(new(){Original=e.Original,Russian=e.Russian,Context=e.Context??"",Enabled=e.Enabled});}
        Load();var grid=new DataGrid{ItemsSource=rows,AutoGenerateColumns=true,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Single};
        var panel=new DockPanel();var buttons=new WrapPanel();DockPanel.SetDock(buttons,Dock.Top);panel.Children.Add(buttons);
        var note=new TextBlock{Text="Контекст необязателен: MainMenu, Settings, SaveLoad, Inventory, Map, Character, DialogueChoice, Help, Crafting, Shop, Interaction, System, Unknown, Technical. Для изменения редактируйте ячейки. Импорт заменяет пользовательский glossary.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)};
        DockPanel.SetDock(note,Dock.Bottom);panel.Children.Add(note);panel.Children.Add(grid);Content=panel;
        void Save(){grid.CommitEdit();grid.CommitEdit();glossary.Save(rows.Select(r=>new UiGlossaryEntry(r.Original,r.Russian,r.Context,r.Enabled)));}
        void Button(string label,Action action){var button=new Button{Content=label,Margin=new Thickness(4),Padding=new Thickness(8)};button.Click+=(_,_)=>{try{action();}catch(Exception e){MessageBox.Show(this,e.Message,"Glossary");}};buttons.Children.Add(button);}
        Button("Добавить",()=>{var row=new EditableEntry();rows.Add(row);grid.SelectedItem=row;grid.ScrollIntoView(row);});
        Button("Изменить",()=>{if(grid.SelectedItem!=null){grid.CurrentCell=new DataGridCellInfo(grid.SelectedItem,grid.Columns[1]);grid.BeginEdit();}});
        Button("Удалить",()=>{if(grid.SelectedItem is EditableEntry row)rows.Remove(row);});
        Button("Сохранить",Save);
        Button("Экспорт словаря",()=>{Save();var dialog=new SaveFileDialog{Filter="JSON UTF-8|*.json",FileName="runtime-ui-glossary.json"};if(dialog.ShowDialog(this)==true)glossary.Export(dialog.FileName);});
        Button("Импорт словаря",()=>{var dialog=new OpenFileDialog{Filter="JSON UTF-8|*.json"};if(dialog.ShowDialog(this)==true){glossary.Import(dialog.FileName);Load();}});
        Button("Встроенный glossary",()=>{var built=new Window{Title="Встроенный glossary — изменения задаются пользовательскими строками",Width=650,Height=600,Owner=this,Content=new DataGrid{ItemsSource=RuntimeUiGlossary.BuiltIn,IsReadOnly=true,AutoGenerateColumns=true}};built.ShowDialog();});
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.UI.Commands;
using GameLocalizer.UI.Views;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    private readonly RuntimeCollectorService collector=new();
    public ObservableCollection<RuntimeUiEntry> RuntimeUi {get;}=[];
    private string runtimeStatus="Не установлен";
    public string RuntimeCollectorStatus {get=>runtimeStatus;private set=>Set(ref runtimeStatus,value);}
    public ICommand InstallCollectorCommand {get;private set;}=null!;
    public ICommand RemoveCollectorCommand {get;private set;}=null!;
    public ICommand ImportCollectorCommand {get;private set;}=null!;
    private void InitializeCollectorCommands()
    {
        InitializeRuntimeExportCommands();
        InitializeDialogueTraceCommands();
        InitializeRuntimeDictionaryCommands();
        InstallCollectorCommand=new AsyncCommand(()=>Run(async(op,ct)=>
        {
            if(op.Game==null)return;
            var warning=new ChoiceWindow("Runtime UI Collector","Runtime UI Collector временно добавит диагностический BepInEx-плагин в папку игры.\n\nСборщик наблюдает UI-текст. Если установлен Runtime словарь, отдельный модуль мгновенно подставляет готовый локальный русский перевод. Модель и сеть во время игры не используются.\n\nПосле сбора плагин можно полностью удалить.\n\nЗакройте игру перед установкой.","Установить","Отмена"){Owner=Application.Current?.MainWindow};
            warning.ShowDialog();if(warning.Choice!="Установить")return;
            var path=await Task.Run(()=>collector.Install(op.Game.Path,Path.Combine(AppContext.BaseDirectory,"RuntimeCollector","GameLocalizer.RuntimeCollector.dll")),ct);
            if(IsCurrent(op)){RuntimeCollectorStatus="Установлен";Status=path;RefreshDialogueTrace();}
        }),()=>Idle&&HasGame);
        RemoveCollectorCommand=new AsyncCommand(()=>Run(async(op,ct)=>
        {
            if(op.Game==null)return;
            if(MessageBox.Show("Закройте игру. Удалить только принадлежащие сборщику файлы?","Runtime UI Collector",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            await Task.Run(()=>collector.Uninstall(op.Game.Path),ct);if(IsCurrent(op)){RuntimeCollectorStatus=collector.Status(op.Game.Path);RefreshDialogueTrace();}
        }),()=>Idle&&HasGame);
        ImportCollectorCommand=new AsyncCommand(()=>Run(async(op,ct)=>
        {
            if(op.Game==null)return;
            var rows=await Task.Run(()=>RuntimeCollectorService.ImportDirectory(RuntimeCollectorService.DataDirectory(op.Game.Path)),ct);
            await repository.MatchRuntimeAsync(op.Session,op.Game.Path,rows,ct);
            await PrepareRuntimeRowsAsync(op.Game,rows,ct);
            var unsupported=await repository.LoadUiDiscoveryAsync(op.Game.Path,ct);
            var lookup=unsupported.GroupBy(r=>r.Text,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First().SourceFile,StringComparer.Ordinal);
            foreach(var row in rows)if(row.MatchStatus=="RuntimeOnly"&&lookup.TryGetValue(row.Text,out var source)){row.MatchStatus="MatchedUnsupported";row.Source=source;}
            if(!IsCurrent(op))return;
            RuntimeUi.Clear();foreach(var row in rows){AttachRuntimeRow(row,op.Game);RuntimeUi.Add(row);}
            RuntimeCollectorStatus=$"Импортировано {rows.Count} строк. Найдено новых Runtime UI строк: {rows.Count(r=>string.IsNullOrWhiteSpace(r.Russian)&&!r.DictionaryConflict)}";PreviewTabIndex=4;
        }),()=>Idle&&HasGame);
    }
    private void RefreshCollector()
    {
        RuntimeUi.Clear();try{RuntimeCollectorStatus=game==null?"Не установлен":collector.Status(game.Path);RefreshDialogueTrace();}catch(Exception e){RuntimeCollectorStatus=e.Message;}
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.UI.Commands;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    private bool dialogueTraceEnabled;
    public bool DialogueTraceEnabled
    {
        get=>dialogueTraceEnabled;
        set
        {
            if(value==dialogueTraceEnabled || game==null)return;
            try
            {
                if(!Idle || !collector.IsInstalled(game.Path))return;
                collector.SetDialogueTraceEnabled(game.Path,value);
                Set(ref dialogueTraceEnabled,value);
                RuntimeCollectorStatus="Диагностика диалогов "+(value?"включена":"выключена")+". Применится при следующем запуске игры.";
            }
            catch(Exception e){RuntimeCollectorStatus="Не удалось изменить диагностику диалогов: "+e.Message;}
            finally{Changed(nameof(DialogueTraceEnabled));}
        }
    }
    public bool CanEditDialogueTrace=>Idle&&game!=null&&collector.IsInstalled(game.Path);
    public string DialogueTracePath
    {
        get
        {
            if(game==null)return "Выберите игру";
            var directory=RuntimeCollectorService.DataDirectory(game.Path);
            return Directory.Exists(directory)?Directory.EnumerateFiles(directory,"dialogue-trace-*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()??Path.Combine(directory,"dialogue-trace-*.jsonl"):Path.Combine(directory,"dialogue-trace-*.jsonl");
        }
    }
    public ICommand OpenDialogueTraceCommand {get;private set;}=null!;
    private void InitializeDialogueTraceCommands()
    {
        PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(Idle))Changed(nameof(CanEditDialogueTrace));};
        OpenDialogueTraceCommand=new RelayCommand(()=>
        {
            try
            {
                var path=DialogueTracePath;Changed(nameof(DialogueTracePath));
                if(!File.Exists(path)){RuntimeCollectorStatus="Лог диалогов ещё не создан. Включите диагностику и запустите игру.";return;}
                Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
            }
            catch(Exception e){RuntimeCollectorStatus="Не удалось открыть лог диалогов: "+e.Message;}
        },()=>Idle&&HasGame);
    }
    private void RefreshDialogueTrace()
    {
        dialogueTraceEnabled=game!=null&&collector.ReadDialogueTraceEnabled(game.Path);
        Changed(nameof(DialogueTraceEnabled));Changed(nameof(CanEditDialogueTrace));Changed(nameof(DialogueTracePath));
    }
}

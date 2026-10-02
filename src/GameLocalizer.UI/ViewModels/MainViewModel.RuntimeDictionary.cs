using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.UI.Commands;

namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    private readonly RuntimeDictionaryService? runtimeDictionary;
    private bool updatingRuntimeRows;
    public Task RuntimeEditSaveTask { get; private set; } = Task.CompletedTask;
    public ICommand TranslateRuntimeNewCommand { get; private set; } = null!;
    public ICommand UpdateRuntimeDictionaryCommand { get; private set; } = null!;
    public ICommand OpenRuntimeDictionaryCommand { get; private set; } = null!;
    public string RuntimeDictionaryNotice => "Словарь подставляет заранее подготовленный русский текст. После обновления перезапустите игру. Для новых строк модель запускается только в GameLocalizer.";
    private void InitializeRuntimeDictionaryCommands()
    {
        TranslateRuntimeNewCommand = new AsyncCommand(TranslateRuntimeNewInteractiveAsync, () => Idle && HasGame && runtimeDictionary != null && RuntimeUi.Any(r => string.IsNullOrWhiteSpace(r.Russian) && !r.DictionaryConflict));
        UpdateRuntimeDictionaryCommand = new AsyncCommand(UpdateRuntimeDictionaryAsync, () => Idle && HasGame && runtimeDictionary != null && RuntimeUi.Count > 0);
        OpenRuntimeDictionaryCommand = new RelayCommand(() =>
        {
            try { var path = runtimeDictionary!.DictionaryPath(game!.Path);if(!File.Exists(path)){RuntimeCollectorStatus="Runtime словарь ещё не создан. Нажмите «Обновить Runtime словарь».";return;}Process.Start(new ProcessStartInfo(path){UseShellExecute=true}); }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception){RuntimeCollectorStatus="Не удалось открыть Runtime словарь: "+e.Message;}
        }, () => Idle && HasGame && runtimeDictionary != null);
    }
    private async Task PrepareRuntimeRowsAsync(Game selected,IReadOnlyList<RuntimeUiEntry> rows,CancellationToken ct)
    {
        await RuntimeEditSaveTask;
        if(runtimeDictionary!=null)await runtimeDictionary.HydrateAsync(selected,rows,ct);
    }
    private void AttachRuntimeRow(RuntimeUiEntry row,Game selected)
    {
        row.PropertyChanged += (_,e) =>
        {
            if(e.PropertyName!=nameof(RuntimeUiEntry.Russian)){CommandManager.InvalidateRequerySuggested();return;}
            if(updatingRuntimeRows || row.TranslationSource!="Manual" || runtimeDictionary==null || !ReferenceEquals(game,selected))return;
            // Exact lookup has one manual value per original. Editing one context explicitly resolves the other contexts.
            updatingRuntimeRows=true;
            try{foreach(var duplicate in RuntimeUi.Where(r=>r.Text==row.Text&&!ReferenceEquals(r,row))){duplicate.SetTranslation(row.Russian,"Manual",row.UpdatedAt);duplicate.SetDictionaryState(false,false);}}
            finally{updatingRuntimeRows=false;}
            RuntimeEditSaveTask=PersistRuntimeManualAsync(RuntimeEditSaveTask,selected,RuntimeUi.ToArray(),row);
            CommandManager.InvalidateRequerySuggested();
        };
    }
    private async Task PersistRuntimeManualAsync(Task previous,Game selected,IReadOnlyList<RuntimeUiEntry> rows,RuntimeUiEntry changed)
    {
        await previous;
        try{await runtimeDictionary!.SaveManualAsync(selected,rows,changed,CancellationToken.None);}
        catch(Exception e){if(ReferenceEquals(game,selected))RuntimeCollectorStatus="Ошибка сохранения runtime-перевода: "+e.Message;}
    }
    public async Task<int> TranslateRuntimeNewAsync()
    {
        if(!Idle || game==null || runtimeDictionary==null)return 0;
        var selected=game;var version=selectionVersion;var rows=RuntimeUi.ToArray();
        using var source=new CancellationTokenSource();cancellation=source;Busy=true;updatingRuntimeRows=true;
        try
        {
            await PrepareRuntimeRowsAsync(selected,rows,source.Token);
            var count=await runtimeDictionary.TranslateNewAsync(selected,rows,source.Token);
            if(ReferenceEquals(game,selected)&&version==selectionVersion){RuntimeCollectorStatus=$"Переведено новых строк: {count}";Status=RuntimeCollectorStatus;}
            return count;
        }
        catch(OperationCanceledException){RuntimeCollectorStatus="Runtime перевод отменён; готовые переводы сохранены.";return 0;}
        catch(Exception e){RuntimeCollectorStatus="Ошибка Runtime перевода: "+e.Message;return 0;}
        finally{updatingRuntimeRows=false;cancellation=null;Busy=false;}
    }
    private async Task TranslateRuntimeNewInteractiveAsync()
    {
        if(await TranslateRuntimeNewAsync()>0 && MessageBox.Show("Обновить Runtime словарь?","Runtime Translation Dictionary",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            await UpdateRuntimeDictionaryAsync();
    }
    public async Task UpdateRuntimeDictionaryAsync()
    {
        if(!Idle || game==null || runtimeDictionary==null)return;
        var selected=game;var version=selectionVersion;var rows=RuntimeUi.ToArray();Busy=true;updatingRuntimeRows=true;
        try
        {
            await RuntimeEditSaveTask;
            var build=await runtimeDictionary.GenerateAsync(selected,rows,CancellationToken.None);
            // The explicitly requested update installs only the owned ready copy. No hidden dictionary update after translation.
            var pluginFolder=Path.Combine(selected.Path,RuntimeCollectorService.PluginDirectory);
            string? installed=null;
            if(File.Exists(Path.Combine(pluginFolder,"collector-ownership.json")))installed=collector.InstallDictionary(selected.Path,runtimeDictionary.DictionaryPath(selected.Path));
            if(ReferenceEquals(game,selected)&&version==selectionVersion)
            {
                RuntimeCollectorStatus=$"Runtime словарь: {build.Dictionary.EntryCount} строк. DictionaryConflict: {build.Conflicts.Count}.\n{runtimeDictionary.DictionaryPath(selected.Path)}\n"+(installed==null?"Сборщик не установлен; сохранён пользовательский master.":"Установлена копия: "+installed+"\nПерезапустите игру.");Status=RuntimeCollectorStatus;
            }
        }
        catch(Exception e){if(ReferenceEquals(game,selected))RuntimeCollectorStatus="Ошибка Runtime словаря: "+e.Message;}
        finally{updatingRuntimeRows=false;Busy=false;}
    }
}

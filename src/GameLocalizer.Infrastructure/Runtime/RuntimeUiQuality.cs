using System.Text.Json;
using System.Text.RegularExpressions;
using GameLocalizer.Core.Validation;

namespace GameLocalizer.Infrastructure.Runtime;

public enum UiContext { MainMenu, Settings, SaveLoad, Inventory, Map, Character, DialogueChoice, Help, Crafting, Shop, Interaction, System, Unknown }
public sealed record UiClassification(UiContext Context, string Confidence, string Domain);
public static class RuntimeUiClassifier
{
    private static readonly (UiContext Context, string Pattern)[] Rules =
    [
        (UiContext.Help,"help|tutorial|guide"), (UiContext.SaveLoad,"save|load|loader"),
        (UiContext.Settings,"settings|options|config"), (UiContext.Inventory,"inventory|equipment|items"),
        (UiContext.Crafting,"craft|crafting"), (UiContext.Shop,"shop|barter"),
        (UiContext.DialogueChoice,"dialogue|dialog|choice"), (UiContext.Character,"character|girl|chara"),
        (UiContext.Interaction,"interaction|action|command list|motion"), (UiContext.Map,"map"),
        (UiContext.MainMenu,"title|mainmenu|main menu"), (UiContext.System,"system|debug|technical|software")
    ];
    public static UiClassification Classify(RuntimeUiEntry row)
    {
        var metadata = string.Join(" ", row.Scene,row.Object,row.Hierarchy,row.Component,row.NeighborLabels,row.ExistingCategory);
        // Split common Unity CamelCase names, then use tokens rather than arbitrary substrings.
        metadata = Regex.Replace(metadata,"([a-z])([A-Z])","$1 $2").ToLowerInvariant();
        var domain = Regex.IsMatch(metadata,@"\b(debug|technical|software|console)\b") ? "Technical" : "Game";
        var matches = Rules.Where(r=>Regex.IsMatch(metadata,@"(?<![a-z])("+r.Pattern+@")(?![a-z])")).ToArray();
        if(matches.Length>0) return new(matches[0].Context, matches.Select(r=>r.Context).Distinct().Count()==1 ? "High" : "Medium",domain);
        if(row.Text.Length<=40)
        {
            var label=row.Text.ToLowerInvariant();
            var labelContext=label switch { "options" or "settings"=>UiContext.Settings,"save" or "load"=>UiContext.SaveLoad,"inventory" or "equipment"=>UiContext.Inventory,"map"=>UiContext.Map,"help"=>UiContext.Help,"crafting"=>UiContext.Crafting,"shop" or "barter"=>UiContext.Shop,"new game" or "game start" or "quit"=>UiContext.MainMenu,_=>UiContext.Unknown };
            if(labelContext!=UiContext.Unknown)return new(labelContext,"Medium",domain);
        }
        return new(UiContext.Unknown,"Low",domain);
    }
}

public sealed record UiGlossaryEntry(string Original,string Russian,string? Context = null,bool Enabled = true);
public sealed class RuntimeUiGlossary
{
    public string Path { get; }
    public RuntimeUiGlossary(string? data=null) => Path=System.IO.Path.Combine(data??GameLocalizer.Core.Models.ApplicationPaths.UserData,"Glossary","runtime-ui.json");
    public static IReadOnlyList<UiGlossaryEntry> BuiltIn { get; } = CreateBuiltIn();
    private static IReadOnlyList<UiGlossaryEntry> CreateBuiltIn()
    {
        var pairs = "Options|Настройки;Settings|Настройки;Save|Сохранить;Load|Загрузить;Loader|Загрузка;Quit|Выход;Exit|Выход;Back|Назад;Cancel|Отмена;Confirm|Подтвердить;OK|ОК;Yes|Да;No|Нет;Map|Карта;Status|Состояние;Help|Помощь;Camera|Камера;Journal|Журнал;Inventory|Инвентарь;Items|Предметы;Item|Предмет;Equipment|Снаряжение;Craft|Создать;Crafting|Создание;Shop|Магазин;Barter|Обмен;Friendship|Дружба;Category|Категория;Sort|Сортировка;Rarity|Редкость;Gender|Пол;Play Time|Время игры;New Game|Новая игра;Game Start|Начать игру;Continue|Продолжить;Auto Save|Автосохранение;Take Photo|Сделать фото;Change Girl|Сменить девушку;Transfer All|Переместить всё;Item Management|Управление предметами;Chat|Общение;Give Advice|Дать совет;Give Item|Дать предмет";
        var result=pairs.Split(';').Select(p=>p.Split('|')).Select(p=>new UiGlossaryEntry(p[0],p[1])).ToList();
        result.AddRange([new("Loader","Загрузить игру","SaveLoad"),new("Log","Журнал","DialogueChoice"),new("Log","Журнал","System"),new("Log","Лог","Technical"),new("Release","Отпустить","Character"),new("Release","Отпустить","Interaction"),new("Release","Релиз","Technical"),new("Change","Сменить","Character"),new("Change","Изменить","Settings")]);
        return result;
    }
    public IReadOnlyList<UiGlossaryEntry> Load() => File.Exists(Path) ? Read(File.ReadAllText(Path)) : [];
    public static IReadOnlyList<UiGlossaryEntry> Read(string json)
    {
        var rows=JsonSerializer.Deserialize<UiGlossaryEntry[]>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Пустой JSON glossary");
        foreach(var row in rows)
        {
            if(string.IsNullOrWhiteSpace(row.Original)||!new TranslationValidator().Validate(row.Original,row.Russian,out _) || (row.Context is {Length:>0} && row.Context!="Technical" && !Enum.TryParse<UiContext>(row.Context,true,out _)))
                throw new InvalidDataException("Некорректная строка glossary: "+row.Original);
        }
        if(rows.GroupBy(r=>(r.Original.ToUpperInvariant(),(r.Context??"").ToUpperInvariant())).Any(g=>g.Count()>1))throw new InvalidDataException("Повтор Original + Context в glossary");
        return rows;
    }
    public void Save(IEnumerable<UiGlossaryEntry> rows)
    {
        var json=JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        Read(json);RuntimeDictionaryService.AtomicSave(Path,json);
    }
    public void Import(string file) => Save(Read(File.ReadAllText(file)));
    public void Export(string file) => RuntimeDictionaryService.AtomicSave(file,JsonSerializer.Serialize(Load(),new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
    public (string Russian,string Source)? Match(RuntimeUiEntry row)
    {
        var context=RuntimeUiClassifier.Classify(row);
        foreach(var (entries,source) in new[]{(Load(),"UserGlossary"),(BuiltIn,"BuiltInGlossary")})
        {
            var exact=entries.Where(e=>e.Enabled&&string.Equals(e.Original,row.Text,StringComparison.OrdinalIgnoreCase)).ToArray();
            var contextual=context.Confidence!="Low" ? exact.FirstOrDefault(e=>string.Equals(e.Context,context.Domain=="Technical"?"Technical":context.Context.ToString(),StringComparison.OrdinalIgnoreCase)) : null;
            var match=contextual??exact.FirstOrDefault(e=>string.IsNullOrEmpty(e.Context));
            if(match!=null)return(match.Russian,source);
        }
        return null;
    }
}

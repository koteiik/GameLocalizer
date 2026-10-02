using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.TranslationProviders;

namespace GameLocalizer.Infrastructure.Runtime;

public sealed record RuntimeTranslationState(string Identity,string OriginalText,string RussianText,string TranslationSource,DateTimeOffset UpdatedAt,bool Approved=false);
public sealed record RuntimeDictionaryValue(string OriginalText,string RussianText,string Scene,string Hierarchy,string ComponentType,string Source,DateTimeOffset UpdatedAt,string TranslationSource);
public sealed record RuntimeDictionaryFile(int SchemaVersion,string GameId,DateTimeOffset GeneratedAt,string Version,int EntryCount,IReadOnlyList<RuntimeDictionaryValue> Entries);
public sealed record RuntimeDictionaryBuild(RuntimeDictionaryFile Dictionary,IReadOnlySet<string> Conflicts);

public sealed partial class RuntimeDictionaryService(TranslationService offlineTranslator,TranslationMemoryService memory,string? userData = null)
{
    private readonly string data=userData??ApplicationPaths.UserData;
    public RuntimeUiGlossary Glossary { get; } = new(userData);
    private readonly SemaphoreSlim gate=new(1,1);
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public string DirectoryFor(string root)=>Path.Combine(data,"RuntimeDictionary",RuntimeCollectorService.GameId(root));
    public string DictionaryPath(string root)=>Path.Combine(DirectoryFor(root),"runtime-dictionary.json");
    private string StatePath(string root)=>Path.Combine(DirectoryFor(root),"runtime-translations.json");
    public IReadOnlyList<RuntimeTranslationState> LoadState(string root)
    {
        var path=StatePath(root);if(!File.Exists(path))return [];
        return JsonSerializer.Deserialize<RuntimeTranslationState[]>(File.ReadAllText(path))??throw new InvalidDataException("Повреждён runtime-translations.json; файл сохранён для восстановления.");
    }
    public async Task HydrateAsync(Game game,IReadOnlyList<RuntimeUiEntry> rows,CancellationToken ct)
    {
        var stored=LoadState(game.Path).ToDictionary(r=>r.Identity,StringComparer.Ordinal);
        var candidates=(await memory.ReadRuntimeCandidatesAsync(game.Id,ct)).GroupBy(r=>r.Text,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
        RuntimeDictionaryFile? dictionary=null;var path=DictionaryPath(game.Path);
        if(File.Exists(path))try{dictionary=JsonSerializer.Deserialize<RuntimeDictionaryFile>(File.ReadAllText(path));}catch(JsonException){ }
        var included=dictionary?.SchemaVersion==1 && dictionary.GameId==RuntimeCollectorService.GameId(game.Path)?dictionary.Entries.ToDictionary(r=>r.OriginalText,r=>r.RussianText,StringComparer.Ordinal):new Dictionary<string,string>();
        foreach(var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            stored.TryGetValue(row.Identity,out var previous);
            row.Approved=previous?.Approved??row.Approved;
            var options=new List<(string Text,string Source,DateTimeOffset Updated,int Priority)>();
            if(row.TranslationSource=="Manual" && new TranslationValidator().Validate(row.Text,row.Russian,out _))options.Add((row.Russian,"Manual",row.UpdatedAt,70));
            if(previous!=null && previous.TranslationSource is not ("UserGlossary" or "BuiltInGlossary") && new TranslationValidator().Validate(row.Text,previous.RussianText,out _))options.Add((previous.RussianText,previous.TranslationSource,previous.UpdatedAt,previous.TranslationSource=="Manual"?70:Rank(previous.TranslationSource)));
            var term=Glossary.Match(row);
            if(term is {} match) options.Add((match.Russian,match.Source,DateTimeOffset.UtcNow,Rank(match.Source)));
            if(candidates.TryGetValue(row.Text,out var values))foreach(var value in values.Where(v=>v.Manual || previous?.Approved!=true || v.UpdatedAt>previous.UpdatedAt))options.Add((value.Russian,value.Manual?"Manual":"TranslationMemory",value.UpdatedAt,value.Manual?60:30));
            if(options.Count>0)
            {
                var highest=options.Max(o=>o.Priority);var best=options.Where(o=>o.Priority==highest).ToArray();
                if(best.Select(o=>o.Text).Distinct(StringComparer.Ordinal).Count()==1){var selected=best.OrderByDescending(o=>o.Updated).First();row.SetTranslation(selected.Text,selected.Source,selected.Updated);}
                else {row.SetTranslation(previous?.RussianText??"","DictionaryConflict",DateTimeOffset.UtcNow);row.SetDictionaryState(false,true);continue;}
            }
            row.SetDictionaryState(included.TryGetValue(row.Text,out var translated)&&translated==row.Russian,false);
            row.IsSuspiciousShort=row.Text.Length<=20 && row.SeenCount>=3 && row.TranslationSource is "Offline" or "OfflineModel" && term==null;
        }
    }
    public Task SaveStateAsync(Game game,IReadOnlyList<RuntimeUiEntry> rows,CancellationToken ct)
    {
        var state=rows.Select(r=>new RuntimeTranslationState(r.Identity,r.Text,r.Russian,r.TranslationSource,r.UpdatedAt,r.Approved)).ToArray();
        return SaveStateCore(game.Path,state,ct);
    }
    private async Task SaveStateCore(string root,IReadOnlyList<RuntimeTranslationState> state,CancellationToken ct)
    {
        await gate.WaitAsync(ct);try
        {
            var old=LoadState(root).ToDictionary(r=>r.Identity,StringComparer.Ordinal);foreach(var row in state)old[row.Identity]=row;
            AtomicSave(StatePath(root),JsonSerializer.Serialize(old.Values.ToArray(),Json));
        }finally{gate.Release();}
    }
    public async Task SaveManualAsync(Game game,IReadOnlyList<RuntimeUiEntry> rows,RuntimeUiEntry changed,CancellationToken ct)
    {
        var text=changed.Text;var russian=changed.Russian;
        await SaveStateAsync(game,rows,ct);
        if(new TranslationValidator().Validate(text,russian,out _))
            await offlineTranslator.SaveManualAsync(game,"RuntimeCollector",new(text,text,"Runtime UI","UI",text),russian,ct);
    }
    public async Task<int> TranslateNewAsync(Game game,IReadOnlyList<RuntimeUiEntry> rows,CancellationToken ct,bool persist=true,bool ignoreMemory=false)
    {
        if(offlineTranslator.Provider!="Offline")throw new InvalidOperationException("Runtime pretranslation requires the offline provider.");
        var newRows=rows.Where(r=>string.IsNullOrWhiteSpace(r.Russian)&&!r.DictionaryConflict).ToArray();
        if(!ignoreMemory)await HydrateAsync(game,newRows,ct);
        var pending=rows.Where(r=>string.IsNullOrWhiteSpace(r.Russian)&&!r.DictionaryConflict).GroupBy(r=>(r.Text,r.Context)).ToArray();
        var count=newRows.Where(r=>r.Russian.Length>0).Select(r=>(r.Text,r.Context)).Distinct().Count();
        try
        {
            // Bounded batches persist every completed response through the ordinary translation pipeline.
            foreach(var batch in pending.Chunk(16))
            {
                ct.ThrowIfCancellationRequested();
                foreach(var group in batch)
                    if(Glossary.Match(group.First()) is {} match)
                        foreach(var row in group)row.SetTranslation(match.Russian,match.Source,DateTimeOffset.UtcNow);
                var modelBatch=batch.Where(g=>string.IsNullOrWhiteSpace(g.First().Russian)).ToArray();
                count+=batch.Length-modelBatch.Length;
                var items=modelBatch.Select((g,i)=>new TranslationItem(i.ToString(),g.Key.Text,string.Join("; ",g.First().Context,"Scene="+g.First().Scene,"Object="+g.First().Object,"Hierarchy="+g.First().Hierarchy,"Component="+g.First().Component,"Neighbors="+g.First().NeighborLabels),g.First().Category,g.Key.Text)).ToArray();
                var outcomes=await TranslateLayoutAsync(game,items,ignoreMemory,ct,persist);
                foreach(var outcome in outcomes)
                {
                    if(!new TranslationValidator().Validate(items[int.Parse(outcome.Id)].Text,outcome.Translation,out _))continue;
                    var group=modelBatch[int.Parse(outcome.Id)];var source=outcome.Status==TranslationStatus.Manual?"Manual":outcome.Status==TranslationStatus.FromMemory?"TranslationMemory":"OfflineModel";
                    foreach(var row in group)row.SetTranslation(outcome.Translation,source,DateTimeOffset.UtcNow);count++;
                }
                if(persist)await SaveStateAsync(game,rows,CancellationToken.None);
            }
            return count;
        }
        finally{try{if(persist)await SaveStateAsync(game,rows,CancellationToken.None);}finally{offlineTranslator.EndJob(ct.IsCancellationRequested);}}
    }
    private static int Rank(string source)=>source switch { "Manual"=>60,"UserGlossary"=>50,"BuiltInGlossary"=>40,"TranslationMemory"=>30,"Offline" or "OfflineModel"=>10,_=>20 };

    public sealed record Retranslation(RuntimeUiEntry Row,string OldRussian,string NewRussian,string Context,string Source);
    public async Task<IReadOnlyList<Retranslation>> PreviewAsync(Game game,IReadOnlyList<RuntimeUiEntry> selected,CancellationToken ct)
    {
        var copies=selected.Where(r=>r.TranslationSource!="Manual").Select(r=>(Original:r,Copy:new RuntimeUiEntry{Text=r.Text,Scene=r.Scene,Object=r.Object,Hierarchy=r.Hierarchy,Component=r.Component,NeighborLabels=r.NeighborLabels,ExistingCategory=r.ExistingCategory})).ToArray();
        // Reuse manual/memory and current glossary, but explicitly refresh previous model output.
        await HydrateAsync(game,copies.Select(r=>r.Copy).ToArray(),ct);
        foreach(var pair in copies)if(pair.Copy.TranslationSource is not ("Manual" or "UserGlossary" or "BuiltInGlossary"))pair.Copy.SetTranslation("","",DateTimeOffset.UtcNow);
        await TranslateNewAsync(game,copies.Select(r=>r.Copy).ToArray(),ct,false,true);
        return copies.Where(p=>p.Copy.Russian.Length>0&&p.Copy.Russian!=p.Original.Russian).Select(p=>new Retranslation(p.Original,p.Original.Russian,p.Copy.Russian,p.Copy.Context,p.Copy.TranslationSource)).ToArray();
    }
    public async Task ApplyAsync(Game game,IReadOnlyList<RuntimeUiEntry> rows,IReadOnlyList<Retranslation> preview,CancellationToken ct)
    {
        foreach(var change in preview)if(change.Row.TranslationSource!="Manual" && change.Row.Russian==change.OldRussian)
        {change.Row.SetTranslation(change.NewRussian,change.Source,DateTimeOffset.UtcNow);change.Row.Approved=true;change.Row.SetDictionaryState(false,false);}
        await SaveStateAsync(game,rows,ct);
    }
    private async Task<IReadOnlyList<TranslationOutcome>> TranslateLayoutAsync(Game game,TranslationItem[] items,bool ignoreMemory,CancellationToken ct,bool persist)
    {
        var result=new List<TranslationOutcome>();
        foreach(var item in items)
        {
            var parts=Regex.Split(item.Text,"(\\r\\n|\\n|\\r)");
            var segments=new List<TranslationItem>();var prefixes=new Dictionary<int,string>();var suffixes=new Dictionary<int,string>();
            for(var i=0;i<parts.Length;i+=2)
            {
                var match=Regex.Match(parts[i],@"^(\s*(?:[•●▪\-*]|\d+[.)])?\s*)(.*?)(\s*)$");
                if(match.Groups[2].Value.Length==0)continue;
                prefixes[i]=match.Groups[1].Value;suffixes[i]=match.Groups[3].Value;
                segments.Add(new(i.ToString(),match.Groups[2].Value,item.Context,item.Category));
            }
            var outcomes=await offlineTranslator.TranslateDetailedAsync(game,"RuntimeCollector",segments,ignoreMemory,ct,persist);
            if(outcomes.Any(o=>o.Status is TranslationStatus.Failed or TranslationStatus.ValidationError or TranslationStatus.Cancelled))continue;
            foreach(var outcome in outcomes){var i=int.Parse(outcome.Id);parts[i]=prefixes[i]+outcome.Translation+suffixes[i];}
            var status=outcomes.Count>0&&outcomes.All(o=>o.Status==TranslationStatus.Manual)?TranslationStatus.Manual:outcomes.Count>0&&outcomes.All(o=>o.Status==TranslationStatus.FromMemory)?TranslationStatus.FromMemory:TranslationStatus.Translated;
            result.Add(new(item.Id,string.Concat(parts),status));
        }
        return result;
    }
    public static RuntimeDictionaryBuild Build(string root,IReadOnlyList<RuntimeUiEntry> rows)
    {
        var entries=new List<RuntimeDictionaryValue>();var conflicts=new HashSet<string>(StringComparer.Ordinal);var validator=new TranslationValidator();
        foreach(var group in rows.GroupBy(r=>r.Text,StringComparer.Ordinal))
        {
            if(group.Any(r=>r.DictionaryConflict)){conflicts.Add(group.Key);continue;}
            var valid=group.Where(r=>r.Russian!=r.Text && r.Russian.Any(c=>c is >= '\u0400' and <= '\u04ff') && validator.Validate(r.Text,r.Russian,out _)).ToArray();
            if(valid.Length==0)continue;
            var rank=valid.Max(r=>Rank(r.TranslationSource));var best=valid.Where(r=>Rank(r.TranslationSource)==rank).ToArray();
            if(best.Select(r=>r.Russian).Distinct(StringComparer.Ordinal).Count()!=1){conflicts.Add(group.Key);continue;}
            var row=best.OrderByDescending(r=>r.UpdatedAt).First();
            entries.Add(new(row.Text,row.Russian,row.Scene,row.Hierarchy,row.Component,row.Source,row.UpdatedAt,row.TranslationSource));
        }
        entries.Sort((a,b)=>StringComparer.Ordinal.Compare(a.OriginalText,b.OriginalText));
        var now=DateTimeOffset.UtcNow;return new(new(1,RuntimeCollectorService.GameId(root),now,now.ToString("yyyyMMddHHmmssfffffff"),entries.Count,entries),conflicts);
    }
    public async Task<RuntimeDictionaryBuild> GenerateAsync(Game game,IReadOnlyList<RuntimeUiEntry> rows,CancellationToken ct)
    {
                await SaveStateAsync(game,rows,ct);
        var all=rows.ToList();var path=DictionaryPath(game.Path);
        if(File.Exists(path))
        {
            RuntimeDictionaryFile? previous=null;try{previous=JsonSerializer.Deserialize<RuntimeDictionaryFile>(File.ReadAllText(path));}catch(JsonException){ }
            if(previous?.SchemaVersion==1 && previous.GameId==RuntimeCollectorService.GameId(game.Path))
                foreach(var template in previous.Entries.Where(e=>e.ComponentType=="ExplicitPlaceholderTemplate" && !rows.Any(r=>r.Text==e.OriginalText)))
                {
                    var row=new RuntimeUiEntry{Text=template.OriginalText,Scene=template.Scene,Hierarchy=template.Hierarchy,Component=template.ComponentType,Source=template.Source};
                    row.SetTranslation(template.RussianText,template.TranslationSource,template.UpdatedAt);all.Add(row);
                }
        }
        var result=Build(game.Path,all);
        await gate.WaitAsync(ct);try{AtomicSave(DictionaryPath(game.Path),JsonSerializer.Serialize(result.Dictionary,Json));}finally{gate.Release();}
        var lookup=result.Dictionary.Entries.ToDictionary(r=>r.OriginalText,r=>r.RussianText,StringComparer.Ordinal);
        foreach(var row in rows)row.SetDictionaryState(lookup.TryGetValue(row.Text,out var russian)&&russian==row.Russian,result.Conflicts.Contains(row.Text));
        return result;
    }
    public static void AtomicSave(string path,string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var bytes=Encoding.UTF8.GetBytes(content);stream.Write(bytes);stream.Flush(true);}File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;

namespace GameLocalizer.Infrastructure.Runtime;
public sealed partial class RuntimeDictionaryService
{
    public sealed record ReprocessChange(RuntimeUiEntry Row,string OldRussian,string NewRussian,string Context,string OldSource,string NewSource);
    public sealed record ReprocessPreview(IReadOnlyList<RuntimeUiEntry> Rows,IReadOnlyList<ReprocessChange> Changes,int ManualPreserved,int UserGlossaryPreserved);
    public sealed record DictionaryDeployment(string Master,string Installed,string MasterSha256,string InstalledSha256,int EntryCount);
    public IReadOnlyList<RuntimeUiEntry> LoadExisting(string root,IReadOnlyList<RuntimeUiEntry>? current=null)
    {
        var rows=RuntimeCollectorService.ImportDirectory(RuntimeCollectorService.DataDirectory(root)).ToDictionary(r=>r.Identity,StringComparer.Ordinal);
        foreach(var state in LoadState(root))
        {
            if(!rows.TryGetValue(state.Identity,out var row))
            {
                var identity=JsonSerializer.Deserialize<string[]>(state.Identity)??throw new InvalidDataException("Invalid runtime identity");
                if(identity.Length!=4||identity[0]!=state.OriginalText)throw new InvalidDataException("Invalid runtime identity");
                row=new(){Text=identity[0],Scene=identity[1],Hierarchy=identity[2],Component=identity[3]};rows.Add(row.Identity,row);
            }
            row.SetTranslation(state.RussianText,state.TranslationSource,state.UpdatedAt);row.Approved=state.Approved;
        }
        var path=DictionaryPath(root);
        if(File.Exists(path))
        {
            var dictionary=JsonSerializer.Deserialize<RuntimeDictionaryFile>(File.ReadAllText(path))??throw new InvalidDataException("Invalid runtime dictionary");
            foreach(var entry in dictionary.Entries)
            {
                var row=new RuntimeUiEntry{Text=entry.OriginalText,Scene=entry.Scene,Hierarchy=entry.Hierarchy,Component=entry.ComponentType,Source=entry.Source};
                // Dictionary-only ordinary rows are stale output, not authoritative entries.
                if(!rows.TryGetValue(row.Identity,out var existing)&&entry.ComponentType!="ExplicitPlaceholderTemplate")continue;
                if(existing==null){existing=row;rows.Add(row.Identity,row);}
                if(string.IsNullOrEmpty(existing.Russian))existing.SetTranslation(entry.RussianText,entry.TranslationSource,entry.UpdatedAt);
            }
        }
        foreach(var row in current??[])if(row.TranslationSource is "Manual" or "UserGlossary" || !rows.ContainsKey(row.Identity))rows[row.Identity]=row;
        return rows.Values.OrderBy(r=>r.Text,StringComparer.Ordinal).ToArray();
    }
    public async Task<ReprocessPreview> PreviewReprocessAsync(Game game,IReadOnlyList<RuntimeUiEntry> rows,CancellationToken ct)
    {
        var candidates=await memory.ReadRuntimeCandidatesAsync(game.Id,ct);var changes=new List<ReprocessChange>();
        var copies=new List<(RuntimeUiEntry Original,RuntimeUiEntry Copy)>();
        foreach(var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if(row.TranslationSource is "Manual" or "UserGlossary")continue;
            var copy=new RuntimeUiEntry{Text=row.Text,Scene=row.Scene,Object=row.Object,Hierarchy=row.Hierarchy,Component=row.Component,NeighborLabels=row.NeighborLabels,ExistingCategory=row.ExistingCategory};
            var manual=candidates.Where(c=>c.Text==row.Text&&c.Manual).OrderByDescending(c=>c.UpdatedAt).FirstOrDefault();
            if(manual!=null)copy.SetTranslation(manual.Russian,"Manual",manual.UpdatedAt);
            else if(Glossary.Match(copy) is {} term)copy.SetTranslation(term.Russian,term.Source,DateTimeOffset.UtcNow);
            else
            {
                var remembered=candidates.Where(c=>c.Text==row.Text&&!c.Manual&&Usable(row.Text,c.Russian)).OrderByDescending(c=>c.UpdatedAt).FirstOrDefault();
                if(remembered!=null&&(!row.Approved||remembered.UpdatedAt>row.UpdatedAt))copy.SetTranslation(remembered.Russian,remembered.TranslationSource,remembered.UpdatedAt);
                else if(Usable(row.Text,row.Russian))copy.SetTranslation(row.Russian,NormalizeSource(row.TranslationSource),row.UpdatedAt);
                else if(!row.Text.Any(char.IsLetter))copy.SetTranslation(row.Text,NormalizeSource(row.TranslationSource),row.UpdatedAt);
            }
            copies.Add((row,copy));
        }
        // No memory or state writes before acceptance. Model fallback is only for missing/invalid entries.
        await TranslateNewAsync(game,copies.Select(p=>p.Copy).ToArray(),ct,false,true);
        foreach(var (original,copy) in copies)
            if(copy.Russian.Length>0&&(copy.Russian!=original.Russian||copy.TranslationSource!=original.TranslationSource))
                changes.Add(new(original,original.Russian,copy.Russian,copy.Context,original.TranslationSource,copy.TranslationSource));
        return new(rows,changes,rows.Count(r=>r.TranslationSource=="Manual"),rows.Count(r=>r.TranslationSource=="UserGlossary"));
    }
    private static string NormalizeSource(string source)=>source switch {"Offline"=>"OfflineModel","" or "Imported" or "DictionaryConflict"=>"Legacy",_=>source};
    private static bool Usable(string original,string translated)=>new TranslationValidator().Validate(original,translated,out _)&&translated.Any(c=>c is >= '\u0400' and <= '\u04ff');
    public async Task ApplyReprocessAsync(Game game,ReprocessPreview preview,CancellationToken ct)
    {
        foreach(var change in preview.Changes)
        {
            ct.ThrowIfCancellationRequested();
            if(change.Row.TranslationSource is "Manual" or "UserGlossary" || change.Row.Russian!=change.OldRussian||change.Row.TranslationSource!=change.OldSource)continue;
            change.Row.SetTranslation(change.NewRussian,change.NewSource,DateTimeOffset.UtcNow);change.Row.Approved=true;change.Row.SetDictionaryState(false,false);
        }
        await SaveStateAsync(game,preview.Rows,ct);
        // Update automatic TM only when exact dictionary export has one unambiguous final value.
        foreach(var entry in Build(game.Path,preview.Rows).Dictionary.Entries.Where(e=>e.TranslationSource!="Manual"))
            await memory.UpdateRuntimeAutomaticAsync(game.Id,entry.OriginalText,entry.RussianText,entry.TranslationSource,ct);
    }
    public async Task<DictionaryDeployment> RebuildAndDeployAsync(Game game,IReadOnlyList<RuntimeUiEntry> rows,CancellationToken ct)
    {
        await SaveStateAsync(game,rows,ct);
        var build=Build(game.Path,rows);var master=DictionaryPath(game.Path);
        await gate.WaitAsync(ct);try{AtomicSave(master,JsonSerializer.Serialize(build.Dictionary,Json));}finally{gate.Release();}
        VerifyDictionary(master,build.Dictionary);
        var installed=new RuntimeCollectorService().InstallDictionary(game.Path,master);
        VerifyDictionary(installed,build.Dictionary);
        var masterHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(master)));var installedHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installed)));
        if(masterHash!=installedHash)throw new IOException("Runtime dictionary SHA256 mismatch");
        var lookup=build.Dictionary.Entries.ToDictionary(e=>e.OriginalText,e=>e.RussianText,StringComparer.Ordinal);
        foreach(var row in rows)row.SetDictionaryState(lookup.TryGetValue(row.Text,out var value)&&value==row.Russian,build.Conflicts.Contains(row.Text));
        return new(master,installed,masterHash,installedHash,build.Dictionary.EntryCount);
    }
    private static void VerifyDictionary(string path,RuntimeDictionaryFile expected)
    {
        var actual=JsonSerializer.Deserialize<RuntimeDictionaryFile>(File.ReadAllText(path));
        if(actual==null||actual.GameId!=expected.GameId||actual.SchemaVersion!=expected.SchemaVersion||actual.EntryCount!=expected.EntryCount||actual.EntryCount!=actual.Entries.Count||!actual.Entries.SequenceEqual(expected.Entries))throw new IOException("Runtime dictionary disk verification failed: "+path);
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.Infrastructure.TranslationProviders;
using GameLocalizer.RuntimeCollector;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;
[Collection("Runtime plugin")]
public sealed class RuntimeDictionaryTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"GLDictionary-"+Guid.NewGuid().ToString("N"));
    private readonly Game game;
    private readonly TranslationMemoryService memory;
    private readonly Provider provider=new();
    private readonly RuntimeDictionaryService service;
    public RuntimeDictionaryTests()
    {
        Directory.CreateDirectory(root);game=new("runtime-test","Test",Path.Combine(root,"game"),"Manual");Directory.CreateDirectory(game.Path);
        memory=new(Path.Combine(root,"memory.db"));service=new(new TranslationService(provider,memory,NullLogger<TranslationService>.Instance),memory,Path.Combine(root,"data"));
    }
    private sealed class Provider:ITranslationProvider
    {
        public int Calls;public string Name=>"Offline";public string ModelName=>"test";public string ModelVersion=>"1";
        public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken ct)
        {Calls++;return Task.FromResult(new TranslationResult(request.Batch.Items.ToDictionary(i=>i.Id,i=>"Перевод "+i.Text)));}
    }
    private RuntimeUiEntry Row(string text,string russian="",string source="Imported",string hierarchy="H")
    {var row=new RuntimeUiEntry{Text=text,Scene="Map",Hierarchy=hierarchy,Component="UnityEngine.UI.Text",SeenCount=4};row.SetTranslation(russian,source,DateTimeOffset.UtcNow);return row;}
    [Fact]public async Task GenerationSchemaAtomicSaveEmptyInvalidAndConflictExcluded()
    {
        var rows=new[]{Row("Chat","Чат"),Row("Give Advice","Дать совет"),Row("Empty"),Row("Broken {{A}}","Сломано"),Row("Duplicate","Первый"),Row("Duplicate","Второй",hierarchy:"Other")};
        var build=await service.GenerateAsync(game,rows,default);Assert.Equal(2,build.Dictionary.EntryCount);Assert.Contains("Duplicate",build.Conflicts);Assert.Equal("Conflict",rows[4].RuntimeStatus);
        using var document=JsonDocument.Parse(File.ReadAllText(service.DictionaryPath(game.Path)));Assert.Equal(1,document.RootElement.GetProperty("SchemaVersion").GetInt32());Assert.Equal(RuntimeCollectorService.GameId(game.Path),document.RootElement.GetProperty("GameId").GetString());Assert.Equal(2,document.RootElement.GetProperty("EntryCount").GetInt32());Assert.True(document.RootElement.TryGetProperty("GeneratedAt",out _));
        Assert.Empty(Directory.EnumerateFiles(service.DirectoryFor(game.Path),"*.tmp"));
        var lookup=RuntimeDictionaryLookup.Load(service.DictionaryPath(game.Path),RuntimeCollectorService.GameId(game.Path),out var error);Assert.Null(error);Assert.True(lookup.TryTranslate("Chat",out var text));Assert.Equal("Чат",text);
    }
    [Fact]public void ManualWinsOverImportedAndOfflineAtGeneration()
    {
        var build=RuntimeDictionaryService.Build(game.Path,[Row("Chat","Модель","Offline"),Row("Chat","Память","TranslationMemory"),Row("Chat","Импорт","Imported"),Row("Chat","Ручной","Manual")]);
        Assert.Equal("Ручной",Assert.Single(build.Dictionary.Entries).RussianText);Assert.Empty(build.Conflicts);
    }
    [Fact]public async Task MemoryReuseAcrossContextsNeverStartsModelAndManualHasPriority()
    {
        var translator=new TranslationService(provider,memory,NullLogger<TranslationService>.Instance);
        await translator.SaveManualAsync(game,"ui.txt",new("x","Chat","different context","ShortUI"),"Чат",default);
        var rows=new[]{Row("Chat")};await service.HydrateAsync(game,rows,default);Assert.Equal("Чат",rows[0].Russian);Assert.Equal(0,await service.TranslateNewAsync(game,rows,default));Assert.Equal(0,provider.Calls);
        rows[0].Russian="Общение";await service.SaveManualAsync(game,rows,rows[0],default);var reopened=new[]{Row("Chat")};await service.HydrateAsync(game,reopened,default);Assert.Equal("Общение",reopened[0].Russian);
    }
    [Fact]public async Task NewOfflineTranslationsAreSavedAndNeverRepeated()
    {
        var rows=new[]{Row("Unlisted action"),Row("Unlisted item"),Row("Unlisted action",hierarchy:"Other")};Assert.Equal(2,await service.TranslateNewAsync(game,rows,default));Assert.True(provider.Calls>0);
        var calls=provider.Calls;Assert.Equal(0,await service.TranslateNewAsync(game,rows,default));Assert.Equal(calls,provider.Calls);
        var reopened=new[]{Row("Unlisted item",hierarchy:"New")};await service.HydrateAsync(game,reopened,default);Assert.Equal(rows[1].Russian,reopened[0].Russian);Assert.Equal(calls,provider.Calls);
    }
    [Theory][InlineData("Chat","Чат")][InlineData("Give Advice","Дать совет")][InlineData("Give Item","Дать предмет")][InlineData("日本語","Японский")][InlineData("Русский","Родной")][InlineData("OK","ОК")]
    public void ExactUnicodeAndShortLookup(string original,string russian)
    {
        var lookup=RuntimeDictionaryLookup.FromEntries([new(){OriginalText=original,RussianText=russian}]);Assert.True(lookup.TryTranslate(original,out var translated));Assert.Equal(russian,translated);Assert.False(lookup.TryTranslate(original+" ",out _));
    }
    [Theory][InlineData("missing")][InlineData("corrupt")][InlineData("schema")][InlineData("game")]
    public void InvalidDictionarySafelyDisablesTranslation(string mode)
    {
        var path=Path.Combine(root,"dictionary.json");if(mode=="corrupt")File.WriteAllText(path,"bad");
        if(mode=="schema" || mode=="game")File.WriteAllText(path,JsonSerializer.Serialize(new RuntimeDictionaryFile(mode=="schema"?9:1,"wrong",DateTimeOffset.UtcNow,"1",0,[])));
        var lookup=RuntimeDictionaryLookup.Load(path,"correct",out var error);Assert.NotNull(error);Assert.Equal(0,lookup.Count);Assert.False(lookup.TryTranslate("Chat",out _));
    }
    [Fact]public void ExplicitDynamicTokenWorksButUnsafeAndAmbiguousPatternsNeverGuess()
    {
        var lookup=RuntimeDictionaryLookup.FromEntries([new(){OriginalText="Talk with {{A}}",RussianText="Поговорить с {{A}}"}]);
        Assert.True(lookup.TryTranslate("Talk with Tsubomi",out var translated));Assert.Equal("Поговорить с Tsubomi",translated);
        Assert.False(lookup.TryTranslate("Talk with <tag>",out _));Assert.False(lookup.TryTranslate("Talk with ",out _));
        var unsafeLookup=RuntimeDictionaryLookup.FromEntries([new(){OriginalText="{{A}}",RussianText="Русский {{A}}"},new(){OriginalText="Talk with {name}",RussianText="Поговорить с {name}"}]);Assert.False(unsafeLookup.TryTranslate("Talk with Tsubomi",out _));
        var ambiguous=RuntimeDictionaryLookup.FromEntries([new(){OriginalText="Talk with {{A}}",RussianText="С {{A}}"},new(){OriginalText="Talk with T{{A}}",RussianText="Т {{A}}"}]);Assert.False(ambiguous.TryTranslate("Talk with Tsubomi",out _));
    }
    [Theory][InlineData(true,false)][InlineData(false,false)][InlineData(false,true)]public void RuntimePluginLoadsOnceAndPrefixReplacesKnownLeavesUnknownUnchanged(bool legacy,bool enabled)
    {
        var id=RuntimeCollectorService.GameId(game.Path);File.WriteAllText(Path.Combine(root,"collector.config"),legacy?id+"\n":new CollectorConfiguration(id,enabled).Serialize());
        File.WriteAllText(Path.Combine(root,"runtime-dictionary.json"),JsonSerializer.Serialize(new RuntimeDictionaryFile(1,id,DateTimeOffset.UtcNow,"1",1,[new("Chat","Чат","","","","",DateTimeOffset.UtcNow,"Manual")])));
        var plugin=new RuntimeDictionaryPlugin();plugin.Info.Location=Path.Combine(root,"plugin.dll");Invoke(plugin,"Awake");Assert.Equal(enabled,plugin.DialogueTraceEnabled);Assert.Equal(enabled,typeof(RuntimeDictionaryPlugin).GetField("dialogueTrace",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(plugin)!=null);File.WriteAllText(Path.Combine(root,"runtime-dictionary.json"),"corrupted after startup");
        var known="Chat";RuntimeDictionaryPlugin.BeforeText(new UnityEngine.UI.Text(),ref known);Assert.Equal("Чат",known);
        var unknown="Never seen";RuntimeDictionaryPlugin.BeforeText(new UnityEngine.UI.Text(),ref unknown);Assert.Equal("Never seen",unknown);Invoke(plugin,"OnDestroy");
    }
    [Fact]public void UnknownTextStillCollectedAndKnownCaptureKeepsOriginal()
    {
        var collector=new CollectorPlugin();Set(collector,"buffer",new CaptureBuffer(Path.Combine(root,"runtime-ui-a.jsonl")));Set(collector,"mainThread",Environment.CurrentManagedThreadId);SetStatic(typeof(CollectorPlugin),"current",collector);
        var translator=new RuntimeDictionaryPlugin();Set(translator,"lookup",RuntimeDictionaryLookup.FromEntries([new(){OriginalText="Chat",RussianText="Чат"}]));Set(translator,"mainThread",Environment.CurrentManagedThreadId);SetStatic(typeof(RuntimeDictionaryPlugin),"current",translator);
        var known=new UnityEngine.UI.Text();var value="Chat";RuntimeDictionaryPlugin.BeforeText(known,ref value);known.text=value;CollectorPlugin.Observe(known);
        var unknown=new UnityEngine.UI.Text();value="Give Advice";RuntimeDictionaryPlugin.BeforeText(unknown,ref value);unknown.text=value;CollectorPlugin.Observe(unknown);
        Invoke(collector,"LateUpdate");Invoke(collector,"Flush");var rows=RuntimeCollectorService.ImportDirectory(root);Assert.Contains(rows,r=>r.Text=="Give Advice");Assert.Contains(rows,r=>r.Text=="Chat");Assert.DoesNotContain(rows,r=>r.Text=="Чат");
        Assert.Empty((System.Collections.IDictionary)typeof(CollectorPlugin).GetField("originals",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(collector)!);
        Invoke(translator,"OnDestroy");SetStatic(typeof(CollectorPlugin),"current",null);
    }
    [Fact]public async Task DictionaryCopyIsOwnedSafeUninstallRetainsMasterAndMemory()
    {
        Directory.CreateDirectory(Path.Combine(game.Path,"BepInEx/core"));File.WriteAllText(Path.Combine(game.Path,"BepInEx/core/BepInEx.dll"),"");Directory.CreateDirectory(Path.Combine(game.Path,"Game_Data/Managed"));File.WriteAllText(Path.Combine(game.Path,"Game_Data/Managed/UnityEngine.dll"),"");
        var package=Path.Combine(root,"plugin.dll");File.WriteAllBytes(package,[77,90,1]);var installer=new RuntimeCollectorService();var plugin=installer.Install(game.Path,package);
        await service.GenerateAsync(game,[Row("Chat","Чат")],default);var copy=installer.InstallDictionary(game.Path,service.DictionaryPath(game.Path));Assert.True(File.Exists(copy));
        installer.Install(game.Path,package);Assert.True(File.Exists(copy));installer.Uninstall(game.Path);Assert.False(File.Exists(copy));Assert.False(File.Exists(plugin));Assert.True(File.Exists(service.DictionaryPath(game.Path)));Assert.True(File.Exists(service.DictionaryPath(game.Path)));
    }
    [Fact]public void TmpFormattedReplacementRegeneratesInSameCallbackAndKeepsOriginalForCollector()
    {
        var collector=new CollectorPlugin();Set(collector,"buffer",new CaptureBuffer(Path.Combine(root,"runtime-ui-tmp.jsonl")));Set(collector,"mainThread",Environment.CurrentManagedThreadId);SetStatic(typeof(CollectorPlugin),"current",collector);
        var translator=new RuntimeDictionaryPlugin();Set(translator,"lookup",RuntimeDictionaryLookup.FromEntries([new(){OriginalText="Talk with {{A}}",RussianText="Поговорить с {{A}}"}]));Set(translator,"mainThread",Environment.CurrentManagedThreadId);SetStatic(typeof(RuntimeDictionaryPlugin),"current",translator);
        var tmp=new TMPro.TextMeshProUGUI{text="Talk with {0}",ParsedText="Talk with Tsubomi"};
        RuntimeDictionaryPlugin.AfterGenerate(tmp);Assert.Equal("Поговорить с Tsubomi",tmp.GetParsedText());
        CollectorPlugin.ObserveRendered(tmp);Invoke(collector,"LateUpdate");Invoke(collector,"Flush");Assert.Equal("Talk with Tsubomi",Assert.Single(RuntimeCollectorService.ImportDirectory(root)).Text);
    }
    [Fact]public void LastUnknownAssignmentInSameFrameIsCollectedInsteadOfEarlierKnownText()
    {
        var collector=new CollectorPlugin();Set(collector,"buffer",new CaptureBuffer(Path.Combine(root,"runtime-ui-last.jsonl")));Set(collector,"mainThread",Environment.CurrentManagedThreadId);SetStatic(typeof(CollectorPlugin),"current",collector);
        var translator=new RuntimeDictionaryPlugin();Set(translator,"lookup",RuntimeDictionaryLookup.FromEntries([new(){OriginalText="Chat",RussianText="Чат"}]));Set(translator,"mainThread",Environment.CurrentManagedThreadId);SetStatic(typeof(RuntimeDictionaryPlugin),"current",translator);
        var component=new UnityEngine.UI.Text();var value="Chat";RuntimeDictionaryPlugin.BeforeText(component,ref value);component.text=value;CollectorPlugin.Observe(component);
        value="Unknown";RuntimeDictionaryPlugin.BeforeText(component,ref value);component.text=value;CollectorPlugin.Observe(component);Invoke(collector,"LateUpdate");Invoke(collector,"Flush");Assert.Equal("Unknown",Assert.Single(RuntimeCollectorService.ImportDirectory(root)).Text);
    }
    [Fact]public void PackagedPluginHasNoNetworkProcessModelHostOrOnnxDependencies()
    {
        using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"RuntimeCollector","GameLocalizer.RuntimeCollector.dll"));using var pe=new System.Reflection.PortableExecutable.PEReader(stream);
        var reader=System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var references=reader.TypeReferences.Select(h=>reader.GetTypeReference(h)).Select(r=>reader.GetString(r.Namespace)+"."+reader.GetString(r.Name)).ToArray();
        Assert.DoesNotContain(references,r=>r.StartsWith("System.Net",StringComparison.Ordinal)||r.StartsWith("Microsoft.ML",StringComparison.Ordinal)||r.Contains("ModelHost",StringComparison.Ordinal)||r=="System.Diagnostics.Process"||r.StartsWith("GameLocalizer.Infrastructure",StringComparison.Ordinal));
    }
    [Fact]public async Task DictionaryUninstallPreservesActualTranslationMemory()
    {
        await new TranslationService(provider,memory,NullLogger<TranslationService>.Instance).SaveManualAsync(game,"RuntimeCollector",new("x","Chat","Runtime UI","UI"),"Чат",default);
        Directory.CreateDirectory(Path.Combine(game.Path,"BepInEx/core"));File.WriteAllText(Path.Combine(game.Path,"BepInEx/core/BepInEx.dll"),"");Directory.CreateDirectory(Path.Combine(game.Path,"Game_Data/Managed"));File.WriteAllText(Path.Combine(game.Path,"Game_Data/Managed/UnityEngine.dll"),"");
        var package=Path.Combine(root,"plugin.dll");File.WriteAllBytes(package,[77,90,1]);var installer=new RuntimeCollectorService();installer.Install(game.Path,package);await service.GenerateAsync(game,[Row("Chat","Чат")],default);installer.InstallDictionary(game.Path,service.DictionaryPath(game.Path));installer.Uninstall(game.Path);
        Assert.Equal("Чат",Assert.Single(await memory.ReadRuntimeCandidatesAsync(game.Id,default)).Russian);
    }
    [Fact]public async Task ExplicitAuthoredTemplateSurvivesDictionaryRefreshFromRawCaptures()
    {
        var template=Row("Talk with {{A}}","Поговорить с {{A}}");template.Component="ExplicitPlaceholderTemplate";
        await service.GenerateAsync(game,[Row("Chat","Чат"),template],default);
        var refreshed=await service.GenerateAsync(game,[Row("Chat","Общение","Manual")],default);
        Assert.Equal(2,refreshed.Dictionary.EntryCount);
        var lookup=RuntimeDictionaryLookup.Load(service.DictionaryPath(game.Path),RuntimeCollectorService.GameId(game.Path),out var error);Assert.Null(error);Assert.True(lookup.TryTranslate("Talk with Tsubomi",out var russian));Assert.Equal("Поговорить с Tsubomi",russian);
    }
    [Fact]public void LaterTmpRenderKeepsOriginalAndNeverRecapturesOurRussianReplacement()
    {
        var collector=new CollectorPlugin();Set(collector,"buffer",new CaptureBuffer(Path.Combine(root,"runtime-ui-next-frame.jsonl")));Set(collector,"mainThread",Environment.CurrentManagedThreadId);SetStatic(typeof(CollectorPlugin),"current",collector);
        var translator=new RuntimeDictionaryPlugin();Set(translator,"lookup",RuntimeDictionaryLookup.FromEntries([new(){OriginalText="Chat",RussianText="Чат"}]));Set(translator,"mainThread",Environment.CurrentManagedThreadId);SetStatic(typeof(RuntimeDictionaryPlugin),"current",translator);
        var tmp=new TMPro.TextMeshProUGUI();var value="Chat";RuntimeDictionaryPlugin.BeforeText(tmp,ref value);tmp.text=value;tmp.ParsedText=value;Invoke(collector,"LateUpdate");
        RuntimeDictionaryPlugin.AfterGenerate(tmp);CollectorPlugin.ObserveRendered(tmp);Invoke(collector,"LateUpdate");Invoke(collector,"Flush");
        var row=Assert.Single(RuntimeCollectorService.ImportDirectory(root));Assert.Equal("Chat",row.Text);Assert.Equal(2,row.SeenCount);
    }
    [Fact]public void TenThousandEntriesAndHundredThousandRandomLookupsAllocateNothing()
    {
        var entries=Enumerable.Range(0,10000).Select(i=>new RuntimeDictionaryEntry{OriginalText="Key "+i,RussianText="Строка "+i}).ToArray();var lookup=RuntimeDictionaryLookup.FromEntries(entries);var random=new Random(42);var keys=Enumerable.Range(0,100000).Select(_=>entries[random.Next(entries.Length)].OriginalText).ToArray();
        foreach(var key in keys.Take(1000))lookup.TryTranslate(key,out _);
        var bytes=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();foreach(var key in keys)if(!lookup.TryTranslate(key,out _))throw new Exception("missing");watch.Stop();var allocated=GC.GetAllocatedBytesForCurrentThread()-bytes;
        Assert.Equal(10000,lookup.Count);Assert.True(allocated<1024,$"Allocated: {allocated}");Assert.True(watch.Elapsed.TotalSeconds<5);
    }
    private static void Invoke(object target,string method)=>target.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,null);
    private static void Set(object target,string field,object value)=>target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    private static void SetStatic(Type type,string field,object? value)=>type.GetField(field,BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,value);
    public void Dispose(){SetStatic(typeof(RuntimeDictionaryPlugin),"current",null);SetStatic(typeof(CollectorPlugin),"current",null);using(var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"memory.db")}.ToString()))Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);Directory.Delete(root,true);}
}

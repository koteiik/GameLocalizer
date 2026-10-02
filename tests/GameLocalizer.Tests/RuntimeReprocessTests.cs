using System.Text.Json;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.Infrastructure.TranslationProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace GameLocalizer.Tests;
public sealed class RuntimeReprocessTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"GLReprocess-"+Guid.NewGuid().ToString("N"));
    private readonly Game game;private readonly TranslationMemoryService memory;private readonly TranslationService translator;private readonly RuntimeDictionaryService service;
    private sealed class Model:ITranslationProvider
    {
        public string Name=>"Offline";public string ModelName=>"test";public string ModelVersion=>"1";
        public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken ct)=>Task.FromResult(new TranslationResult(request.Batch.Items.ToDictionary(i=>i.Id,i=>"Перевод "+i.Text)));
    }
    public RuntimeReprocessTests(){Directory.CreateDirectory(root);game=new("reprocess","Test",root,"Manual");memory=new(Path.Combine(root,"memory.db"));translator=new(new Model(),memory,NullLogger<TranslationService>.Instance);service=new(translator,memory,root);}
    private static RuntimeUiEntry Row(string text,string russian,string source,string scene="") {var row=new RuntimeUiEntry{Text=text,Scene=scene};row.SetTranslation(russian,source,DateTimeOffset.UtcNow);return row;}
    [Theory][InlineData("Legacy")][InlineData("Unknown")][InlineData("Offline")][InlineData("OfflineModel")][InlineData("TranslationMemory")]
    public async Task ExistingBadLoaderIsReprocessedDespiteOldMemory(string source)
    {
        await translator.TranslateDetailedAsync(game,"ui",[new("1","Loader","old","UI")],false,default);await memory.UpdateRuntimeAutomaticAsync(game.Id,"Loader","Погрузчик","OfflineModel",default);
        var row=Row("Loader","Погрузчик",source,"Title_Load");await service.SaveStateAsync(game,[row],default);
        var rows=service.LoadExisting(game.Path);var preview=await service.PreviewReprocessAsync(game,rows,default);var change=Assert.Single(preview.Changes);Assert.Equal("Загрузить игру",change.NewRussian);Assert.Equal("BuiltInGlossary",change.NewSource);Assert.Equal("Погрузчик",rows[0].Russian);
        await service.ApplyReprocessAsync(game,preview,default);Assert.Equal("Загрузить игру",rows[0].Russian);var candidate=Assert.Single(await memory.ReadRuntimeCandidatesAsync(game.Id,default));Assert.Equal("Загрузить игру",candidate.Russian);Assert.Equal("BuiltInGlossary",candidate.TranslationSource);
    }
    [Fact]public async Task ManualAndStoredUserGlossaryPreservedEvenWithoutCurrentMatch()
    {
        var rows=new[]{Row("Loader","Мой ручной","Manual","Title_Load"),Row("Options","Мой glossary","UserGlossary")};await service.SaveStateAsync(game,rows,default);
        var preview=await service.PreviewReprocessAsync(game,service.LoadExisting(game.Path),default);Assert.Empty(preview.Changes);Assert.Equal(1,preview.ManualPreserved);Assert.Equal(1,preview.UserGlossaryPreserved);
        await service.ApplyReprocessAsync(game,preview,default);Assert.Equal("Мой ручной",preview.Rows.Single(r=>r.Text=="Loader").Russian);Assert.Equal("Мой glossary",preview.Rows.Single(r=>r.Text=="Options").Russian);
    }
    [Fact]public async Task FullRebuildRemovesStaleValueAndVerifiesRealInstalledDiskAndHashes()
    {
        var row=Row("Loader","Погрузчик","Legacy","Title_Load");var old=await service.GenerateAsync(game,[row],default);
        var stale=new RuntimeDictionaryValue("Stale","Устаревшее","","","","",DateTimeOffset.UtcNow,"OfflineModel");
        File.WriteAllText(service.DictionaryPath(root),JsonSerializer.Serialize(old.Dictionary with {EntryCount=2,Entries=old.Dictionary.Entries.Concat([stale]).ToArray()}));
        var rows=service.LoadExisting(root);Assert.Single(rows);var preview=await service.PreviewReprocessAsync(game,rows,default);await service.ApplyReprocessAsync(game,preview,default);
        Directory.CreateDirectory(Path.Combine(root,"BepInEx/core"));File.WriteAllText(Path.Combine(root,"BepInEx/core/BepInEx.dll"),"");Directory.CreateDirectory(Path.Combine(root,"Game_Data/Managed"));File.WriteAllText(Path.Combine(root,"Game_Data/Managed/UnityEngine.dll"),"");
        var package=Path.Combine(root,"package.dll");File.WriteAllBytes(package,[77,90,1]);new RuntimeCollectorService().Install(root,package);
        var deployment=await service.RebuildAndDeployAsync(game,rows,default);Assert.Equal(deployment.MasterSha256,deployment.InstalledSha256);Assert.Equal(1,deployment.EntryCount);
        var disk=JsonSerializer.Deserialize<RuntimeDictionaryFile>(File.ReadAllText(deployment.Installed))!;Assert.Equal(disk.EntryCount,disk.Entries.Count);var entry=Assert.Single(disk.Entries);Assert.Equal("Загрузить игру",entry.RussianText);Assert.DoesNotContain(disk.Entries,e=>e.OriginalText=="Stale");Assert.Equal(File.ReadAllBytes(deployment.Master),File.ReadAllBytes(deployment.Installed));
    }
    [Fact]public async Task DefaultLoaderRegressionAndUntranslatedFallback()
    {
        var rows=new[]{Row("Loader","Погрузчик","Legacy"),Row("Long untranslated sentence for help","Long untranslated sentence for help","Unknown","Help")};var preview=await service.PreviewReprocessAsync(game,rows,default);Assert.Equal("Загрузка",preview.Changes.Single(c=>c.Row.Text=="Loader").NewRussian);Assert.Equal("OfflineModel",preview.Changes.Single(c=>c.Row.Text.StartsWith("Long")).NewSource);
    }
    public void Dispose(){using(var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"memory.db")}.ToString()))Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);Directory.Delete(root,true);}
}

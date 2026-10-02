using System.Text.Json;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.Infrastructure.TranslationProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;
public sealed class RuntimeUiQualityTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"GLQuality-"+Guid.NewGuid().ToString("N"));
    private readonly TranslationMemoryService memory;
    private readonly Model model=new();
    private readonly TranslationService translator;
    private readonly RuntimeDictionaryService service;
    private readonly Game game;
    private sealed class Model : ITranslationProvider
    {
        public int Calls; public string Prefix="Перевод "; public string Name=>"Offline";public string ModelName=>"quality-test";public string ModelVersion=>"1";
        public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken ct)
        {Calls++;return Task.FromResult(new TranslationResult(request.Batch.Items.ToDictionary(i=>i.Id,i=>Prefix+i.Text)));}
    }
    public RuntimeUiQualityTests()
    {
        Directory.CreateDirectory(root);game=new("quality-test","Quality",root,"Manual");memory=new(Path.Combine(root,"memory.db"));
        translator=new(model,memory,NullLogger<TranslationService>.Instance);service=new(translator,memory,root);
    }
    private static RuntimeUiEntry Row(string text,string scene="")=>new(){Text=text,Scene=scene,SeenCount=10};
    [Theory]
    [InlineData("Loader","Title_Load","Загрузить игру")][InlineData("Loader","","Загрузка")]
    [InlineData("Options","","Настройки")][InlineData("Save","","Сохранить")][InlineData("Map","","Карта")]
    [InlineData("Status","","Состояние")][InlineData("Help","","Помощь")][InlineData("Camera","","Камера")]
    [InlineData("Journal","","Журнал")][InlineData("Chat","","Общение")][InlineData("Give Advice","","Дать совет")]
    [InlineData("Give Item","","Дать предмет")][InlineData("Game Start","","Начать игру")][InlineData("Quit","","Выход")]
    public async Task RealUiTermsAvoidModel(string text,string scene,string expected)
    {var row=Row(text,scene);await service.TranslateNewAsync(game,[row],default);Assert.Equal(expected,row.Russian);Assert.NotEqual("Погрузчик",row.Russian);Assert.Equal("BuiltInGlossary",row.TranslationSource);Assert.Equal(0,model.Calls);}
    [Fact]public async Task ManualUserBuiltInMemoryAndApprovedPriority()
    {
        await translator.SaveManualAsync(game,"runtime",new("1","Options","","UI"),"Вручную",default);
        service.Glossary.Save([new("Options","Параметры")]);var row=Row("Options");await service.HydrateAsync(game,[row],default);Assert.Equal("Вручную",row.Russian);
        row=Row("Loader");service.Glossary.Save([new("Loader","Загрузка сохранения")]);await service.HydrateAsync(game,[row],default);Assert.Equal("UserGlossary",row.TranslationSource);Assert.Equal("Загрузка сохранения",row.Russian);
        row.Russian="Мой перевод";await service.SaveManualAsync(game,[row],row,default);var reopened=Row("Loader");await service.HydrateAsync(game,[reopened],default);Assert.Equal("Мой перевод",reopened.Russian);
        var modelRow=Row("Save");modelRow.SetTranslation("Плохой","OfflineModel",DateTimeOffset.UtcNow);await service.SaveStateAsync(game,[modelRow],default);var fixedRow=Row("Save");await service.HydrateAsync(game,[fixedRow],default);Assert.Equal("Сохранить",fixedRow.Russian);
    }
    [Theory][InlineData("Log","DebugConsole","Лог")][InlineData("Log","System","Журнал")][InlineData("Change","Character","Сменить")][InlineData("Change","Settings","Изменить")][InlineData("Release","Character","Отпустить")][InlineData("Release","Software","Релиз")]
    public void ContextSpecificGlossary(string text,string scene,string expected)=>Assert.Equal(expected,service.Glossary.Match(Row(text,scene))?.Russian);
    [Fact]public async Task LowConfidenceAndNoSubstringFallback()
    {
        var row=Row("Save this plant");Assert.Equal("Low",row.ContextConfidence);Assert.Null(service.Glossary.Match(row));await service.TranslateNewAsync(game,[row],default);Assert.Equal("OfflineModel",row.TranslationSource);Assert.True(model.Calls>0);
        Assert.Null(service.Glossary.Match(Row("Release")));Assert.Null(service.Glossary.Match(Row("Autosaver")));
    }
    [Fact]public async Task MemoryReuseWithoutModel()
    {
        var first=Row("Unlisted action");await service.TranslateNewAsync(game,[first],default);var calls=model.Calls;
        var second=Row("Unlisted action","NewScene");await service.HydrateAsync(game,[second],default);Assert.Equal(first.Russian,second.Russian);Assert.Equal("TranslationMemory",second.TranslationSource);Assert.Equal(calls,model.Calls);
    }
    [Fact]public async Task HelpPreservesRichTextPlaceholdersBreaksBulletsAndIndentation()
    {
        var row=Row("  • <b>Press {0}</b> to move.\r\n\r\n - Use {{key}} for help.\n1. Keep %s nearby.  ","Tutorial");
        Assert.Equal("Runtime Help / Tutorial",row.Category);await service.TranslateNewAsync(game,[row],default);
        Assert.Contains("  • ",row.Russian);Assert.Contains("\r\n\r\n - ",row.Russian);Assert.Contains("\n1. ",row.Russian);Assert.EndsWith("  ",row.Russian);
        Assert.True(new GameLocalizer.Core.Validation.TranslationValidator().Validate(row.Text,row.Russian,out _));Assert.Contains("<b>",row.Russian);Assert.Contains("{0}",row.Russian);Assert.Contains("{{key}}",row.Russian);Assert.Contains("%s",row.Russian);
    }
    [Fact]public async Task PreviewDoesNotOverwriteAndApplyRegeneratesCorrectedDictionary()
    {
        var row=Row("Loader","Title_Load");row.SetTranslation("Погрузчик","OfflineModel",DateTimeOffset.UtcNow);await service.SaveStateAsync(game,[row],default);await service.GenerateAsync(game,[row],default);
        var preview=await service.PreviewAsync(game,[row],default);var proposal=Assert.Single(preview);Assert.Equal("Загрузить игру",proposal.NewRussian);Assert.Equal("Погрузчик",row.Russian);Assert.Equal("Погрузчик",Assert.Single(service.LoadState(game.Path)).RussianText);
        await service.ApplyAsync(game,[row],preview,default);var build=await service.GenerateAsync(game,[row],default);Assert.Equal("Загрузить игру",Assert.Single(build.Dictionary.Entries).RussianText);
        row.Russian="Ручной";Assert.Empty(await service.PreviewAsync(game,[row],default));Assert.Equal("Ручной",row.Russian);
    }
    [Fact]public void UserGlossaryPersistenceImportExportAndDisabledEntries()
    {
        service.Glossary.Save([new("Loader","Загрузить сохранение","SaveLoad"),new("Options","Параметры",null,false)]);
        var file=Path.Combine(root,"export.json");service.Glossary.Export(file);var other=new RuntimeUiGlossary(Path.Combine(root,"other"));other.Import(file);Assert.Equal(service.Glossary.Load(),other.Load());Assert.Equal("Настройки",other.Match(Row("Options"))?.Russian);
        Assert.Equal("Загрузить сохранение",other.Match(Row("Loader","Title_Load"))?.Russian);
        Assert.Throws<InvalidDataException>(()=>other.Save([new("A {0}","Неверно")]));Assert.Equal(2,other.Load().Count);
    }
    [Fact]public async Task ConflictingContextsExcludedFromExactDictionary()
    {
        var rows=new[]{Row("Change","Character"),Row("Change","Settings")};await service.TranslateNewAsync(game,rows,default);var build=await service.GenerateAsync(game,rows,default);Assert.Contains("Change",build.Conflicts);Assert.Empty(build.Dictionary.Entries);
    }
    [Fact]public async Task RejectedModelPreviewDoesNotChangeMemoryOrStoredTranslation()
    {
        var row=Row("Unknown UI instruction");row.SetTranslation("Старый перевод","OfflineModel",DateTimeOffset.UtcNow);await service.SaveStateAsync(game,[row],default);
        var before=await memory.ReadRuntimeCandidatesAsync(game.Id,default);var preview=await service.PreviewAsync(game,[row],default);Assert.Single(preview);Assert.True(model.Calls>0);
        Assert.Equal(before,await memory.ReadRuntimeCandidatesAsync(game.Id,default));Assert.Equal("Старый перевод",Assert.Single(service.LoadState(game.Path)).RussianText);
    }
    [Fact]public async Task AppliedModelPreviewSurvivesOlderMemoryAndDoesNotRetranslateAutomatically()
    {
        var row=Row("Unknown action");await service.TranslateNewAsync(game,[row],default);model.Prefix="Новый перевод ";
        var preview=await service.PreviewAsync(game,[row],default);Assert.Single(preview);await service.ApplyAsync(game,[row],preview,default);
        var reopened=Row(row.Text);await service.HydrateAsync(game,[reopened],default);Assert.Equal(row.Russian,reopened.Russian);Assert.True(reopened.Approved);
        var calls=model.Calls;Assert.Equal(0,await service.TranslateNewAsync(game,[reopened],default));Assert.Equal(calls,model.Calls);
    }
    [Fact]public async Task BuiltInGlossaryBeatsExistingAutomaticMemory()
    {
        await translator.TranslateDetailedAsync(game,"ui",[new("x","Options","OldContext","UI")],false,default);
        var row=Row("Options");await service.HydrateAsync(game,[row],default);Assert.Equal("Настройки",row.Russian);Assert.Equal("BuiltInGlossary",row.TranslationSource);
    }
    [Theory][InlineData("Title",UiContext.MainMenu)][InlineData("Settings",UiContext.Settings)][InlineData("Title_Load",UiContext.SaveLoad)][InlineData("Inventory",UiContext.Inventory)][InlineData("Map",UiContext.Map)][InlineData("Character",UiContext.Character)][InlineData("DialogueChoice",UiContext.DialogueChoice)][InlineData("Tutorial",UiContext.Help)][InlineData("Crafting",UiContext.Crafting)][InlineData("Shop",UiContext.Shop)][InlineData("Interaction",UiContext.Interaction)][InlineData("System",UiContext.System)][InlineData("unclassified",UiContext.Unknown)]
    public void ClassifierCoversTaxonomy(string scene,UiContext context)=>Assert.Equal(context,RuntimeUiClassifier.Classify(Row("A",scene)).Context);
    public void Dispose(){using(var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"memory.db")}.ToString()))Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);Directory.Delete(root,true);}
}

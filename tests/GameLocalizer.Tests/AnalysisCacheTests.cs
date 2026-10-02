using System.Text;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Data.Sqlite;
using Xunit;
namespace GameLocalizer.Tests;
public sealed class AnalysisCacheTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"AnalysisCacheTests",Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(root,"analysis.db");
    private string Memory => Path.Combine(root,"memory.db");
    private Game Game => new("game","Game",Path.Combine(root,"game"),"Manual");
    public AnalysisCacheTests(){Directory.CreateDirectory(Game.Path);Write("ui.txt","おしゃべり=Chat\n助言=Give Advice");}
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    private string Write(string name,string text){var p=Path.Combine(Game.Path,"BepInEx","Translation","en","Text",name);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,text,new UTF8Encoding(false));return p;}
    private Task<ScanProgress> Refresh(ScanWorkspaceService workspace,string session,bool full=false,CancellationToken ct=default) => workspace.RefreshAnalysisAsync(Game,session,"Unity",.99,full,null,ct);
    [Fact] public async Task SnapshotRestoresAllRowMetadataTranslationsSelectionAndCategoriesAfterRestart()
    {
        ScanRow before;
        using(var repo=new ScanResultRepository(Db,true))
        {
            var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"first");before=(await repo.QueryAsync("first",new(),0,default)).Rows[0];
            await repo.SaveEditsAsync("first",[new(before.Id,"Чат",false,TranslationStatus.Manual)],default);
            await ws.SaveManualEditsAsync(Game,"first",[new(before.Id,"Чат",false,TranslationStatus.Manual)],default);
        }
        Assert.True(File.Exists(Db));
        using(var repo=new ScanResultRepository(Db,true))
        {
            var snapshot=await repo.LoadSnapshotAsync(Game.Path,default);Assert.NotNull(snapshot);Assert.Equal("Unity",snapshot.Engine);Assert.Equal(.99,snapshot.EngineConfidence);
            var row=(await repo.QueryAsync(snapshot.Session,new(),0,default)).Rows.Single(r=>r.Key==before.Key);
            Assert.Equal("Чат",row.Translation);Assert.True(row.ManualEdit);Assert.Equal("Manual",row.TranslationSource);Assert.False(row.Selected);
            Assert.Equal(before.StableRowId,row.StableRowId);Assert.Equal(before.Original,row.Original);Assert.Equal(before.Category,row.Category);Assert.Equal(before.Confidence,row.Confidence);
            Assert.Equal(before.PhysicalSourceFile,row.PhysicalSourceFile);Assert.Equal("en",row.LocalizationSlot);Assert.Equal(before.AdapterType,row.AdapterType);
        }
    }
    [Fact] public async Task UnchangedGameReusesFilesAndTimestampOnlyChangeRechecksHash()
    {
        using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");
        var f=Assert.Single(await repo.AnalysisFilesAsync("a",default));File.SetLastWriteTimeUtc(f.PhysicalPath,DateTime.UtcNow.AddMinutes(2));
        Assert.Equal(0,await ws.ValidateAnalysisAsync((await repo.LoadSnapshotAsync(Game.Path,default))!,default));
        await Refresh(ws,"b");Assert.Equal(0,ws.LastFilesRescanned);Assert.Equal(1,ws.LastFilesReused);Assert.Equal(2,(await repo.QueryAsync("b",new(),0,default)).Total);
    }
    [Theory][InlineData("changed")][InlineData("new")][InlineData("deleted")]
    public async Task IncrementalChangesAffectOnlyNecessaryFile(string change)
    {
        Write("second.txt","保存=Save Game");using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");
        if(change=="changed")Write("second.txt","保存=Load Game");else if(change=="new")Write("third.txt","終了=Exit Game");else File.Delete(Path.Combine(Game.Path,"BepInEx/Translation/en/Text/second.txt"));
        await Refresh(ws,"b");Assert.Equal(change=="deleted"?0:1,ws.LastFilesRescanned);Assert.Equal(change=="new"?2:1,ws.LastFilesReused);
        var rows=(await repo.QueryAsync("b",new(),0,default)).Rows;
        if(change=="changed")Assert.Contains(rows,r=>r.Original=="Load Game");else if(change=="new")Assert.Contains(rows,r=>r.Original=="Exit Game");else Assert.DoesNotContain(rows,r=>r.Original=="Save Game");
    }
    [Fact] public async Task SourceChangedKeepsSuggestionUnselectedAndOldTranslationInMemory()
    {
        using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");var row=(await repo.QueryAsync("a",new(),0,default)).Rows[0];
        await repo.SaveEditsAsync("a",[new(row.Id,"Чат",true,TranslationStatus.Manual)],default);await ws.SaveManualEditsAsync(Game,"a",[new(row.Id,"Чат",true,TranslationStatus.Manual)],default);
        Write("ui.txt","おしゃべり=Talk to friend\n助言=Give Advice");await Refresh(ws,"b");var changed=(await repo.QueryAsync("b",new(),0,default)).Rows.Single(r=>r.DisplayKey=="おしゃべり");
        Assert.Equal("SourceChanged",changed.Status);Assert.Equal("Чат",changed.Translation);Assert.False(changed.Selected);Assert.True(File.Exists(Memory));
        Write("ui.txt","おしゃべり=Chat\n助言=Give Advice");await Refresh(ws,"c",true);Assert.Equal("Чат",(await repo.QueryAsync("c",new(),0,default)).Rows.Single(r=>r.Original=="Chat").Translation);
    }
    [Fact] public async Task DuplicateIdentitySurvivesInsertedLineAndFullRescan()
    {
        Write("ui.txt","おしゃべり=Chat\nおしゃべり=Give Advice");using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");var old=(await repo.QueryAsync("a",new(),0,default)).Rows;
        Assert.Equal(2,old.Select(r=>r.StableRowId).Distinct().Count());await repo.SaveEditsAsync("a",[new(old[0].Id,"Чат",true,TranslationStatus.Manual),new(old[1].Id,"Совет",false,TranslationStatus.Manual)],default);
        Write("ui.txt","# comment inserted\nおしゃべり=Chat\nおしゃべり=Give Advice");await Refresh(ws,"b",true);var current=(await repo.QueryAsync("b",new(),0,default)).Rows;
        Assert.Equal(old.Select(r=>r.StableRowId),current.Select(r=>r.StableRowId));Assert.Equal(new[]{"Чат","Совет"},current.Select(r=>r.Translation));Assert.False(current[1].Selected);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task AppliedRussianRestoresEnglishOriginalWithSnapshotOrBackup(bool forgetFirst)
    {
        using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");var rows=(await repo.QueryAsync("a",new(),0,default)).Rows;
        await repo.SaveEditsAsync("a",rows.Select(r=>new ScanEdit(r.Id,r.Original=="Chat"?"Чат":"Дать совет",true,TranslationStatus.Manual)).ToArray(),default);
        await ws.ApplyAsync(Game,"a",default);if(forgetFirst)await repo.ForgetSnapshotAsync(Game.Path,default);
        await Refresh(ws,"b",true);var row=(await repo.QueryAsync("b",new(),0,default)).Rows.Single(r=>r.DisplayKey=="おしゃべり");Assert.Equal("Chat",row.Original);Assert.Equal("Чат",row.Translation);Assert.True(row.Applied);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RestoreAndCleanupKeepCacheAndTranslations(bool cleanup)
    {
        using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");var rows=(await repo.QueryAsync("a",new(),0,default)).Rows;await repo.SaveEditsAsync("a",rows.Select(r=>new ScanEdit(r.Id,"Перевод",true,TranslationStatus.Manual)).ToArray(),default);await ws.ApplyAsync(Game,"a",default);
        var backup=new BackupService(Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance);
        if(cleanup)await backup.CleanupAsync(Game.Path,null,default);else await backup.RestoreAsync(Game.Path,default);
        await ws.RefreshAppliedStampsAsync(Game,"a",false,default);
        Assert.NotNull(await repo.LoadSnapshotAsync(Game.Path,default));Assert.All((await repo.QueryAsync("a",new(),0,default)).Rows,r=>{Assert.False(r.Applied);Assert.Equal("Перевод",r.Translation);});
    }
    [Fact] public async Task ForgetOnlySelectedGameSnapshotKeepsMemoryAndOtherGame()
    {
        using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");var other=new Game("other","Other",Path.Combine(root,"other"),"Manual");Directory.CreateDirectory(other.Path);File.WriteAllText(Path.Combine(other.Path,"ui.txt"),"Start Game");await ws.RefreshAnalysisAsync(other,"other","Unknown",0,false,null,default);
        File.WriteAllText(Path.Combine(root,"settings.json"),"settings");await repo.ForgetSnapshotAsync(Game.Path,default);Assert.Null(await repo.LoadSnapshotAsync(Game.Path,default));Assert.NotNull(await repo.LoadSnapshotAsync(other.Path,default));Assert.True(File.Exists(Memory));Assert.Equal("settings",File.ReadAllText(Path.Combine(root,"settings.json")));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task InvalidOrUnsupportedDatabaseRetainedAndRecreatedSafely(bool unsupported)
    {
        if(unsupported){using(var repo=new ScanResultRepository(Db,true))await ScanRegressionTests.Workspace(repo,Memory).RefreshAnalysisAsync(Game,"a","Unity",1,false,null,default);using var db=new SqliteConnection("Data Source="+Db+";Pooling=False");db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE AnalysisSchema SET Version=999";cmd.ExecuteNonQuery();}
        else File.WriteAllBytes(Db,Encoding.UTF8.GetBytes("corrupt database"));
        using var fresh=new ScanResultRepository(Db,true);Assert.Null(await fresh.LoadSnapshotAsync(Game.Path,default));Assert.NotEmpty(Directory.GetFiles(root,"analysis.db.unreadable-*"));await Refresh(ScanRegressionTests.Workspace(fresh,Memory),"fresh");Assert.NotNull(await fresh.LoadSnapshotAsync(Game.Path,default));
    }
    [Fact] public async Task CancelledSnapshotPublicationKeepsPreviousActiveSnapshot()
    {
        using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");using var cts=new CancellationTokenSource();cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Refresh(ws,"b",true,cts.Token));Assert.Equal("a",(await repo.LoadSnapshotAsync(Game.Path,default))!.Session);
    }
    [Fact] public async Task ModelTranslationRestoresAndKnownRowsAreNotSentAgain()
    {
        string translation;
        using(var repo=new ScanResultRepository(Db,true))
        {
            var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");await ws.RunTranslationJobAsync(Game,"a",false,false,null,default);
            translation=(await repo.QueryAsync("a",new(),0,default)).Rows[0].Translation;Assert.NotEmpty(translation);
        }
        using(var repo=new ScanResultRepository(Db,true))
        {
            var ws=ScanRegressionTests.Workspace(repo,Memory);var snapshot=(await repo.LoadSnapshotAsync(Game.Path,default))!;var row=(await repo.QueryAsync(snapshot.Session,new(),0,default)).Rows[0];Assert.Equal(translation,row.Translation);Assert.Equal("Model",row.TranslationSource);
            var job=await ws.RunTranslationJobAsync(Game,snapshot.Session,false,false,null,default);Assert.Equal(0,job.TranslatedStrings);Assert.True(job.CachedStrings>=2);
            await Refresh(ws,"b",true);Assert.Equal(translation,(await repo.QueryAsync("b",new(),0,default)).Rows[0].Translation);Assert.True(File.Exists(Memory));
        }
    }
    [Fact] public async Task OldSchemaMigratesWithoutDiscardingRows()
    {
        using(var db=new SqliteConnection("Data Source="+Db+";Pooling=False"))
        {
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TABLE ScanRows(Id INTEGER PRIMARY KEY,Session TEXT,GameId TEXT,FilePath TEXT,EntryKey TEXT,Original TEXT,Translation TEXT,Context TEXT,Confidence REAL,Selected INTEGER,Status TEXT,Category TEXT); INSERT INTO ScanRows VALUES(1,'old','game','ui.txt','1','Start Game','Начать игру','',.99,1,'Manual','UI');";cmd.ExecuteNonQuery();
        }
        using var repo=new ScanResultRepository(Db,true);var row=Assert.Single((await repo.QueryAsync("old",new(),0,default)).Rows);Assert.Equal("Начать игру",row.Translation);Assert.Equal("Start Game",row.Original);Assert.Empty(Directory.GetFiles(root,"analysis.db.unreadable-*"));
    }
    [Fact] public async Task NewFileIsDetectedByDirectoryMetadataWithoutTextRescan()
    {
        using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");
        Write("new.txt","保存=Save Game");Assert.True(await ws.ValidateAnalysisAsync((await repo.LoadSnapshotAsync(Game.Path,default))!,default)>0);Assert.Equal(1,ws.LastFilesRescanned);
    }
    [Fact] public async Task InvalidTextFailureIsCachedAndDoesNotCauseRepeatScan()
    {
        var path=Write("invalid.txt","bad");File.WriteAllBytes(path,[0xFF,0xFE,0xFF]);using var repo=new ScanResultRepository(Db,true);var ws=ScanRegressionTests.Workspace(repo,Memory);await Refresh(ws,"a");Assert.True(ws.ScanErrorCount>0);
        await Refresh(ws,"b");Assert.Equal(0,ws.LastFilesRescanned);Assert.Equal(2,ws.LastFilesReused);Assert.True(ws.ScanErrorCount>0);Assert.Equal(2,(await repo.QueryAsync("b",new(),0,default)).Total);
    }
}

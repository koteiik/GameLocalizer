using GameLocalizer.Core.Models;
using Xunit;
namespace GameLocalizer.Tests;
public sealed class TranslationJobFinalizationTests
{
    [Theory]
    [InlineData(5725,5712,13,0,false,false,TranslationJobStatus.PartiallyCompleted)]
    [InlineData(20,20,0,0,false,false,TranslationJobStatus.Completed)]
    [InlineData(20,0,20,0,false,false,TranslationJobStatus.Failed)]
    [InlineData(20,3,0,0,true,false,TranslationJobStatus.Cancelled)]
    [InlineData(20,3,0,0,false,true,TranslationJobStatus.Failed)]
    [InlineData(20,3,0,17,false,false,TranslationJobStatus.PartiallyCompleted)]
    public void TerminalProcessingIncludesEveryOutcome(long total,long success,long failed,long skipped,bool cancelled,bool critical,TranslationJobStatus expected)
    {
        var job = new TranslationJob { TotalStrings=total,TranslatedStrings=success,FailedStrings=failed,SkippedStrings=skipped,Status=TranslationJobStatus.Running };
        Assert.True(job.IsRunning); job.FinalizeJob(cancelled,critical);
        Assert.Equal(expected,job.Status); Assert.False(job.IsRunning); Assert.Equal(total,job.ProcessedCount);
        Assert.Equal(100,job.ProgressPercent); Assert.Equal(success,job.Successful); Assert.NotNull(job.FinishedAt);
    }
    [Fact] public void ActiveProgressIncludesFailuresWithoutInflatingSuccessfulCount() {
        var job = new TranslationJob { TotalStrings=100,TranslatedStrings=70,CachedStrings=10,FailedStrings=5,Status=TranslationJobStatus.Running };
        Assert.Equal(80,job.Successful); Assert.Equal(85,job.ProcessedCount); Assert.Equal(85,job.ProgressPercent);
        Assert.True(job.IsRunning);
    }
    [Fact] public async Task ErrorFilterIncludesLowConfidenceSelectedFailuresAndLegacyPartialIsTerminal() {
        var root=Path.Combine(Path.GetTempPath(),"GameLocalizerJobTests",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            using var repository=new GameLocalizer.Infrastructure.Database.ScanResultRepository(Path.Combine(root,"scan.db"));
            await repository.AppendAsync("s","game",new(new("a.csv","CSV",true,"",ResourceKind.LocalizationCandidate),"hash",[new("a.csv","key","Original text","",.2,true,TextCategory.Dialogue)],new(1,1,1,0,1)),default);
            var row=Assert.Single(await repository.ReadSelectedAsync("s",0,false,default));
            await repository.SaveEditsAsync("s",[new(row.Id,"",true,TranslationStatus.Failed)],default);
            Assert.Empty((await repository.QueryAsync("s",new(),0,default)).Rows);
            Assert.Single((await repository.QueryAsync("s",new(Status:"Ошибки перевода"),0,default)).Rows);
            var store=new GameLocalizer.Infrastructure.Database.TranslationJobStore(Path.Combine(root,"jobs"));
            var job=new TranslationJob {GameId="game",TotalStrings=5725,TranslatedStrings=5712,FailedStrings=13,Status=TranslationJobStatus.PartiallyCompleted,StartedAt=DateTimeOffset.UtcNow};
            await store.SaveAsync(job,default); Assert.Null(store.FindIncomplete("game"));
            var restored=Assert.IsType<TranslationJob>(store.FindLatest("game")); Assert.False(restored.IsRunning); Assert.Equal(100,restored.ProgressPercent);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
}

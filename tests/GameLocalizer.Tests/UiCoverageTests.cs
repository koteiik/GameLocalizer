using System.Text;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace GameLocalizer.Tests;
public sealed class UiCoverageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "UiCoverage", Guid.NewGuid().ToString("N"));
    public UiCoverageTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    private string Write(string file, string text) { var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    [Theory] [InlineData("Chat")][InlineData("Yes")][InlineData("No")][InlineData("Give Advice")][InlineData("Give Item")][InlineData("OK")][InlineData("SAVE")]
    public async Task ShortLabelsClassifiedAndSelected(string value)
    {
        Write("BepInEx/Translation/en/Text/Main.txt", "おしゃべり=" + value);
        var adapters = ScanRegressionTests.Adapters(); var pipeline = new ScanPipeline(new(adapters), adapters, NullLogger<ScanPipeline>.Instance);
        var entries = new List<ScanEntry>(); await foreach(var batch in pipeline.ScanAsync(root, default)) entries.AddRange(batch.Entries);
        var entry = Assert.Single(entries); Assert.Equal(TextCategory.ShortUI, entry.Category); Assert.True(entry.Selected); Assert.True(entry.Confidence >= .85);
    }
    [Theory] [InlineData("C:\\Game\\file.txt")][InlineData("f4bd39ec-f363-4ffc-ab9d-797278e550e0")][InlineData("UnityEngine.GameObject")][InlineData("SomeClass")][InlineData("internal_id")][InlineData("{name}")][InlineData("123")][InlineData("FFAA00FF")]
    public void TechnicalProtectionWinsOverUiContext(string value) => Assert.True(new TextCandidateDetector().Score(value, source: ResourceKind.UIResource, path: "Localization/Menu.json") < .35);
    [Fact] public void ContextIncreasesNaturalShortScore()
    {
        var detector = new TextCandidateDetector(); var plain = detector.Score("Chat");
        Assert.True(detector.Score("Chat", path: "Main.json") > plain);
        Assert.True(detector.Score("Chat", source: ResourceKind.LocalizationCandidate, path: "BepInEx/Translation/en/Text/labels.txt") > plain);
    }
    [Theory] [InlineData(false)][InlineData(true)]
    public async Task UnityCandidatesAndSearchAreReadOnly(bool utf16)
    {
        var file = Path.Combine(root, "Game_Data/sharedassets0.assets"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var payload = (utf16 ? Encoding.Unicode : Encoding.UTF8).GetBytes("\0MonoBehaviour\0Give Advice\0Give Item\0UnityEngine.GameObject\0"); await File.WriteAllBytesAsync(file, payload);
        var discovery = new UiResourceDiscovery(ScanRegressionTests.Adapters()); var candidates = await discovery.DiscoverAsync(root, default);
        Assert.Contains(candidates, c => c.Text == "Give Advice"); Assert.Contains(candidates, c => c.Text == "Give Item"); Assert.All(candidates, c => Assert.False(c.Writable));
        var exact = Assert.Single(await discovery.SearchAsync(root, "Give Advice", default)); Assert.Equal("Exact", exact.MatchKind); Assert.False(exact.Writable); Assert.Equal(Path.GetFullPath(file), exact.SourceFile);
        var insensitive = Assert.Single(await discovery.SearchAsync(root, "give advice", default)); Assert.Equal("Case-insensitive", insensitive.MatchKind);
        Assert.Empty(await discovery.SearchAsync(root, "Unknown Missing UI", default)); Assert.Equal(payload, await File.ReadAllBytesAsync(file));
    }
    [Fact] public async Task SupportedTextSearchIsWritableAndCaseSensitiveEvidence()
    {
        var file = Write("BepInEx/Translation/en/Text/ui.txt", "助言=Give Advice\n渡す=Give Item");
        var discovery = new UiResourceDiscovery(ScanRegressionTests.Adapters()); var hit = Assert.Single(await discovery.SearchAsync(root, "Give Item", default));
        Assert.True(hit.Writable); Assert.Equal(Path.GetFullPath(file), hit.SourceFile); Assert.Equal("Exact", hit.MatchKind);
    }
    [Fact] public async Task UnsupportedCannotBeSelectedOrAppliedEvenByForgedEdits()
    {
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db"));
        await repository.AppendAsync("s", "g", new(new("fake.assets", "Unity", false, "unsupported"), "hash", [new("fake.assets", "1", "Give Advice", "", .99, true, TextCategory.UnsupportedUI)], new(1,1,1,0)), default);
        var page = await repository.QueryAsync("s", new(Filter: "Unsupported UI"), 0, default); var row = Assert.Single(page.Rows); Assert.False(row.Selected);
        await repository.SaveEditsAsync("s", [new(row.Id, "Дать совет", true)], default);
        Assert.Empty(await repository.SelectedFilesAsync("s", default)); Assert.Empty(await repository.ReadSelectedAsync("s", 0, false, default));
    }
    [Fact] public async Task MissedUiAndEmptyFiltersIgnoreSliderButStayScoped()
    {
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db"));
        await repository.AppendAsync("s", "g", new(new("ui.txt", "TXT", true, ""), "hash", [new("ui.txt", "1", "Chat", "", .92, false, TextCategory.ShortUI), new("ui.txt", "2", "Yes", "", .91, true, TextCategory.ShortUI)], new(1,2,2,0)), default);
        var missed = await repository.QueryAsync("s", new(Filter:"Пропущенные UI", MinimumConfidence:1), 0, default); Assert.Equal(1, missed.MissedUi); Assert.Equal("Chat", Assert.Single(missed.Rows).Original);
        var empty = await repository.QueryAsync("s", new(Filter:"Пустые переводы", MinimumConfidence:1), 0, default); Assert.Equal("Yes", Assert.Single(empty.Rows).Original);
    }
    [Fact] public async Task AllEmptyWhitespaceAndNullAreSkippedWithoutChangingSelection()
    {
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db"));
        await repository.AppendAsync("s", "g", new(new("ui.txt", "TXT", true, ""), "hash", [new("ui.txt", "1", "Chat", "", .92, true, TextCategory.ShortUI), new("ui.txt", "2", "Yes", "", .91, true, TextCategory.ShortUI)], new(1,2,2,0)), default);
        var rows = (await repository.QueryAsync("s", new(), 0, default)).Rows;
        await repository.SaveEditsAsync("s", [new(rows[0].Id, null!, true), new(rows[1].Id, " \t\u00A0", true)], default);
        var summary = await repository.ApplySelectionAsync("s", default); Assert.Equal(2, summary.SkippedEmptyTranslations); Assert.Equal(0, summary.AppliedEntries);
        var workspace = ScanRegressionTests.Workspace(repository, Path.Combine(root, "memory.db"));
        Assert.Equal(0, await workspace.ApplyAsync(new("g","Game",root,"Manual"), "s", LocalizationApplyMode.CompatibleReplacement, true, default));
        Assert.False(Directory.Exists(Path.Combine(root,"GameLocalizer_Backup"))); Assert.Equal(2, (await repository.QueryAsync("s",new(),0,default)).Selected);
        Assert.Contains("Пропущено пустых: 2", workspace.DiagnosticSummary);
    }
    [Fact] public async Task EmptyRowsAreCollectedAcrossPages()
    {
        using var repository = new ScanResultRepository(Path.Combine(root,"scan.db"));
        var entries = Enumerable.Range(1,2005).Select(i => new ScanEntry("ui.txt",i.ToString(),"Chat", "", .92,true,TextCategory.ShortUI)).ToArray();
        await repository.AppendAsync("s","g",new(new("ui.txt","TXT",true,""),"hash",entries,new(1,entries.Length,entries.Length,0)),default);
        Assert.Equal(2005,(await repository.ApplySelectionAsync("s",default)).EmptyTranslationRows.Count);
        Assert.Equal(5,(await repository.QueryAsync("s",new(Filter:"Пустые переводы"),1,default)).Rows.Count);
    }
}

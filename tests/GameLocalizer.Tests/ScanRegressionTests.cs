using System.Text;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.TranslationProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class ScanRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizerScanTests", Guid.NewGuid().ToString("N"));
    internal static ILocalizationAdapter[] Adapters() => [new BepInExLocalizationAdapter(), new JsonLocalizationAdapter(), new IniLocalizationAdapter(), new PlainTextLocalizationAdapter(), new CsvLocalizationAdapter(), new XmlLocalizationAdapter(), new PoLocalizationAdapter()];
    public ScanRegressionTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    private string Write(string name, string text) { var path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private ScanPipeline Pipeline() => new(new ResourceScanner(Adapters()), Adapters(), NullLogger<ScanPipeline>.Instance);
    internal static ScanWorkspaceService Workspace(ScanResultRepository repository, string memoryPath, ITranslationProvider? provider = null, TranslationJobStore? jobs = null)
    {
        var adapters = Adapters();
        return new(new(new(adapters), adapters, NullLogger<ScanPipeline>.Instance), repository,
            new(provider ?? new MockTranslationProvider(), new TranslationMemoryService(memoryPath), NullLogger<TranslationService>.Instance),
            new(NullLogger<BackupService>.Instance), adapters, jobs);
    }
    [Theory]
    [InlineData("true")][InlineData("false")][InlineData("null")][InlineData("yes")][InlineData("no")][InlineData("TRUE")]
    public void ConfigFlagsAreExcluded(string value) => Assert.Equal(0, new TextCandidateDetector().Score(value, isConfiguration: true));
    [Theory] [InlineData("Yes")][InlineData("No")]
    public void DialogueAnswersAreNotConfigFlags(string value) => Assert.True(new TextCandidateDetector().Score(value) >= .6);
    [Theory]
    [InlineData("GfxDevice: creating device client")][InlineData("Direct3D")][InlineData("Direct3D11: initialized")]
    [InlineData("Renderer")][InlineData("Vendor: NVIDIA")][InlineData("VRAM")][InlineData("MonoManager")]
    [InlineData("ReloadAssembly")][InlineData("Initialized input")][InlineData("touch support")][InlineData("UnloadTime")]
    [InlineData("FPS: 60")][InlineData("driver info")]
    public void EngineDiagnosticsAreExcluded(string value) => Assert.True(new TextCandidateDetector().Score(value) < .35);
    [Theory]
    [InlineData("output_log.txt", ResourceKind.LogFile)][InlineData("Player.log", ResourceKind.LogFile)]
    [InlineData("debug.log", ResourceKind.LogFile)][InlineData("crash.log", ResourceKind.LogFile)][InlineData("error.log", ResourceKind.LogFile)]
    [InlineData("logs/menu.json", ResourceKind.LogFile)][InlineData("Localization/output_log.txt", ResourceKind.LogFile)]
    [InlineData("Localization/menu.json", ResourceKind.UIResource)][InlineData("StreamingAssets/en.txt", ResourceKind.LocalizationCandidate)]
    [InlineData("config.ini", ResourceKind.TechnicalFile)][InlineData("game.dll", ResourceKind.Binary)]
    [InlineData("notes.txt", ResourceKind.PossibleTextResource)][InlineData("unknown.xyz", ResourceKind.Unknown)]
    public void ClassifiesResources(string path, ResourceKind expected) => Assert.Equal(expected, new ResourceClassifier().Classify(path));
    [Fact]
    public async Task LogsAndTechnicalFilesNeverReachTranslationRows()
    {
        Write("output_log.txt", "Start Game\nGfxDevice: creating device client"); Write("Player.log", "Start Game"); Write("config.ini", "label=Start Game");
        Write("menu.ini", "enabled=true\noff=false\nempty=null\nflag=yes\nflag2=no\nplay=Start Game\nrenderer=Renderer");
        var entries = new List<ScanEntry>();
        await foreach (var batch in Pipeline().ScanAsync(root, default)) entries.AddRange(batch.Entries);
        Assert.All(entries.Where(e => e.Category == TextCategory.Technical), e => Assert.False(e.Selected));
        var entry = Assert.Single(entries, e => e.Category != TextCategory.Technical); Assert.Equal("Start Game", entry.Original); Assert.Equal("menu.ini", entry.FilePath);
    }
    internal static void Generate(string directory, int count)
    {
        Directory.CreateDirectory(directory);
        for (var part = 0; part * 20000 < count; part++)
        {
            using var writer = new StreamWriter(Path.Combine(directory, $"dialogue-{part:00}.csv"), false, new UTF8Encoding(false));
            writer.WriteLine("id,text");
            for (var row = part * 20000; row < Math.Min(count, (part + 1) * 20000); row++) writer.WriteLine($"line{row},Welcome to the village number {row}");
        }
    }
    [Fact]
    public async Task Over100000RowsBatchedPagedAndSearchedGlobally()
    {
        var gameRoot = Path.Combine(root, "game"); Generate(gameRoot, 100005);
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db"));
        var batchCount = 0; long lastProcessed = 0;
        await foreach (var batch in Pipeline().ScanAsync(gameRoot, default))
        {
            Assert.InRange(batch.Entries.Count, 0, ScanPipeline.BatchSize); Assert.True(batch.Progress.Processed >= lastProcessed);
            lastProcessed = batch.Progress.Processed;
            await repository.AppendAsync("scan", "game", batch, default); batchCount++;
        }
        Assert.True(batchCount > 100); Assert.Equal(100005, lastProcessed);
        var first = await repository.QueryAsync("scan", new(), 0, default);
        Assert.Equal(100005, first.Total); Assert.Equal(100005, first.Selected); Assert.Equal(2000, first.Rows.Count);
        var last = await repository.QueryAsync("scan", new(), 50, default); Assert.Equal(5, last.Rows.Count);
        var search = await repository.QueryAsync("scan", new(Search: "number 100004"), 0, default);
        Assert.Equal(1, search.Matching); var target = Assert.Single(search.Rows);
        await repository.SaveEditsAsync("scan", [new(target.Id, "Привет, путник", false)], default);
        var translated = await repository.QueryAsync("scan", new(Search: "ПРИВЕТ", Status: "Manual"), 0, default);
        Assert.Equal(target.Id, Assert.Single(translated.Rows).Id); Assert.Equal(100004, translated.Selected);
        Assert.Equal(5, (await repository.QueryAsync("scan", new(File: "dialogue-05.csv"), 0, default)).Matching);
        Assert.Equal(0, (await repository.QueryAsync("scan", new(MinimumConfidence: 1), 0, default)).Matching);
        var sorted = await repository.QueryAsync("scan", new(Sort: ScanSort.Id, Descending: true), 0, default);
        Assert.Equal(target.Id, sorted.Rows[0].Id);
        Assert.Empty((await repository.QueryAsync("other-session", new(), 0, default)).Rows);
    }
    [Fact]
    public async Task CancellationRetainsCommittedBatchesAndStopsFurtherScanning()
    {
        Generate(root, 10000); using var cts = new CancellationTokenSource();
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db"));
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var batch in Pipeline().ScanAsync(root, cts.Token))
            {
                await repository.AppendAsync("scan", "game", batch, cts.Token); count += batch.Entries.Count;
                if (count >= 2000) cts.Cancel();
            }
        });
        Assert.Equal(2000, (await repository.QueryAsync("scan", new(), 0, default)).Total);
    }
    [Fact]
    public async Task JsonLargeArrayOffsetsRemainCorrect()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 60000).Select(i => $"Welcome to the village {i}"));
        Write("dialogue.json", json); var count = 0;
        await foreach (var batch in Pipeline().ScanAsync(root, default)) count += batch.Entries.Count;
        Assert.Equal(60000, count);
    }
    [Fact]
    public async Task OffPageEditsTranslateApplyAndRestore()
    {
        var gameRoot = Path.Combine(root, "game"); Generate(gameRoot, 4005);
        var file = Directory.GetFiles(gameRoot).Single(); var original = await File.ReadAllBytesAsync(file);
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db"));
        var workspace = Workspace(repository, Path.Combine(root, "memory.db")); var game = new Game("game", "Synthetic", gameRoot, "Manual");
        await workspace.ScanAsync(game, "scan", null, default);
        Assert.Equal(4005, await workspace.TranslateAsync(game, "scan", null, default));
        var last = await repository.QueryAsync("scan", new(), 2, default);
        var target = last.Rows[^1];
        await repository.SaveEditsAsync("scan", [new(target.Id, "Последняя строка переведена вручную", true)], default);
        Assert.Equal(1, await workspace.ApplyAsync(game, "scan", default));
        Assert.Contains("Последняя строка переведена вручную", await File.ReadAllTextAsync(file));
        await new BackupService(NullLogger<BackupService>.Instance).RestoreAsync(gameRoot, default);
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
    }
    [Fact]
    public async Task InvalidOffPageTranslationBlocksAllWrites()
    {
        var gameRoot = Path.Combine(root, "game"); Generate(gameRoot, 2001);
        var file = Directory.GetFiles(gameRoot).Single(); var original = await File.ReadAllBytesAsync(file);
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db")); var workspace = Workspace(repository, Path.Combine(root, "memory.db"));
        var game = new Game("game", "Synthetic", gameRoot, "Manual"); await workspace.ScanAsync(game, "scan", null, default);
        await workspace.TranslateAsync(game, "scan", null, default);
        var target = Assert.Single((await repository.QueryAsync("scan", new(), 1, default)).Rows);
        await repository.SaveEditsAsync("scan", [new(target.Id, "Ошибка {added}", true)], default);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.ApplyAsync(game, "scan", default));
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
    }
}

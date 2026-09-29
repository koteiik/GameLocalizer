using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Translation;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.Models;
using GameLocalizer.Infrastructure.TranslationProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace GameLocalizer.Tests;

public sealed class OfflineTranslationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizerOfflineTests", Guid.NewGuid().ToString("N"));
    public OfflineTranslationTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    private Game Game { get { var path = Path.Combine(root, "game"); Directory.CreateDirectory(path); return new("game", "Synthetic", path, "Manual"); } }
    private TranslationMemoryService Memory => new(Path.Combine(root, "memory.db"));
    private static TranslationItem Line(string value, string id = "1") => new(id, value, "");
    private TranslationService Service(ITranslationProvider provider, GlossaryService? glossary = null) => new(provider, Memory, NullLogger<TranslationService>.Instance, glossary);
    private sealed class Models(bool valid = true) : ITranslationModelManager
    {
        public string ModelDirectory => "synthetic-model";
        public bool IsInstalled => valid;
        public TranslationModelInfo GetModelInfo() => new("synthetic", "Synthetic", "v1", "test", []);
        public IReadOnlyList<TranslationModelInfo> GetInstalledModels() => valid ? [GetModelInfo()] : [];
        public Task DownloadModelAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct) => throw new InvalidOperationException("Network must not be called by provider");
        public Task<bool> VerifyModelAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(valid); }
        public Task DeleteModelAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Hardware(bool gpu = false) : IHardwareDetectionService
    { public HardwareInfo Detect() => new("Synthetic CPU", 8L << 30, gpu ? "Synthetic GPU" : "None", null, gpu); }
    private sealed class Runtime : ITranslationRuntime
    {
        public bool IsLoaded { get; private set; }
        public string Device { get; private set; } = "CPU";
        public int Loads, Calls, Unloads;
        public bool FailGpu, GpuOom;
        public int MaximumBatch = int.MaxValue;
        public Action? AfterBatch;
        public List<TranslationDevice> Attempts { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public List<string> Inputs { get; } = [];
        public Task LoadAsync(string directory, TranslationDevice device, CancellationToken ct)
        {
            Attempts.Add(device); ct.ThrowIfCancellationRequested();
            if (FailGpu && device == TranslationDevice.GPU) throw new InvalidOperationException("No DirectML");
            Loads++; IsLoaded = true; Device = device.ToString(); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; BatchSizes.Add(text.Count);
            if (text.Count > MaximumBatch || GpuOom && Device == "GPU") throw new OutOfMemoryException();
            Inputs.AddRange(text); AfterBatch?.Invoke();
            return Task.FromResult<IReadOnlyList<string>>(text.Select(s => "Перевод " + s).ToArray());
        }
        public void Unload() { if (IsLoaded) Unloads++; IsLoaded = false; }
        public void Dispose() => Unload();
    }
    private static LocalOfflineTranslationProvider Offline(Runtime runtime, OfflineSettings? settings = null, bool gpu = false, bool valid = true) =>
        new(new Models(valid), runtime, new Hardware(gpu), settings ?? new() { Device = TranslationDevice.CPU });

    [Fact]
    public async Task SameStringUsesPersistentMemoryAcrossNewServiceAndNeverLoadsModelAgain()
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime);
        var first = await Service(provider).TranslateAsync(Game, "a.json", [Line("Hello")], default);
        Assert.Equal(1, runtime.Calls); Assert.Equal(1, runtime.Loads); Assert.False(runtime.IsLoaded);
        var second = await Service(provider).TranslateAsync(Game, "a.json", [Line("Hello")], default);
        Assert.Equal(first["1"], second["1"]); Assert.Equal(1, runtime.Calls); Assert.Equal(1, runtime.Loads);
    }
    [Fact]
    public async Task OnlyNewOrChangedStringsInvokeRuntime()
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime); var service = Service(provider);
        await service.TranslateAsync(Game, "a.json", [Line("Old line", "1"), Line("Unchanged line", "2")], default);
        runtime.Inputs.Clear();
        await Service(provider).TranslateAsync(Game, "a.json", [Line("Changed line", "1"), Line("Unchanged line", "2"), Line("New line", "3")], default);
        Assert.Equal(new[] { "Changed line", "New line" }, runtime.Inputs);
        var old = await service.FindAsync(Game, Line("Old line"), default); Assert.NotNull(old);
    }
    [Fact]
    public async Task ManualOverridesCacheModelVersionGlossaryAndExplicitRetranslation()
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime); var glossary = new GlossaryService();
        var service = Service(provider, glossary);
        await service.SaveManualAsync(Game, "a.json", Line("Hello"), "Здравствуйте", default);
        glossary.Save([new("Hello", "Привет", Category: "Names")]);
        var results = await service.TranslateDetailedAsync(Game, "a.json", [Line("Hello")], true, default);
        Assert.Equal("Здравствуйте", Assert.Single(results).Translation); Assert.Equal(TranslationStatus.Manual, results[0].Status); Assert.Equal(0, runtime.Loads);
        var changedModel = service.Key(Game, Line("Hello")) with { ModelVersion = "next-version", Model = "next-model", Provider = "other-provider" };
        Assert.True((await Memory.FindAsync(changedModel, default))?.Manual);
        Assert.Equal("Здравствуйте", (await Memory.FindAsync(changedModel, default))?.Text);
    }
    [Fact]
    public async Task MachineMemoryIsScopedToModelGlossaryContextAndGame()
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime); var service = Service(provider);
        await service.TranslateAsync(Game, "a.json", [Line("Hello")], default);
        var key = service.Key(Game, Line("Hello"));
        Assert.NotNull(await Memory.FindAsync(key, default));
        foreach (var changed in new[] { key with { ModelVersion = "v2" }, key with { Model = "other" }, key with { GlossaryVersion = "new" }, key with { Context = "other" }, key with { GameId = "other" }, key with { Category = "Names" } })
            Assert.Null(await Memory.FindAsync(changed, default));
    }
    [Fact]
    public async Task ExplicitIgnoreMemoryRetranslatesAutomaticEntries()
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime); var service = Service(provider);
        await service.TranslateAsync(Game, "a.json", [Line("Hello")], default);
        await service.TranslateDetailedAsync(Game, "a.json", [Line("Hello")], true, default); service.EndJob();
        Assert.Equal(2, runtime.Calls); Assert.False(runtime.IsLoaded);
    }
    [Fact]
    public async Task KeepLoadedIsOptInAndCancellationForcesUnload()
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime, new() { Device = TranslationDevice.CPU, KeepModelLoaded = true });
        await Service(provider).TranslateAsync(Game, "a.json", [Line("Hello")], default);
        Assert.True(runtime.IsLoaded); provider.EndJob(true); Assert.False(runtime.IsLoaded);
    }
    [Fact]
    public async Task MissingOrCorruptModelCannotLoadRuntime()
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime, valid: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.TranslateAsync(new(new([Line("Hello")])), default));
        Assert.Equal(0, runtime.Loads);
    }
    [Fact]
    public async Task AutoAttemptsGpuAndFallsBackToCpuWithoutCuda()
    {
        using var runtime = new Runtime { FailGpu = true }; using var provider = Offline(runtime, new() { Device = TranslationDevice.Auto }, true);
        await Service(provider).TranslateAsync(Game, "a.json", [Line("Hello")], default);
        Assert.Equal(new[] { TranslationDevice.GPU, TranslationDevice.CPU }, runtime.Attempts); Assert.False(runtime.IsLoaded);
    }
    [Fact]
    public async Task GpuAbstractionUsesGpuWhenSupported()
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime, new() { Device = TranslationDevice.GPU }, true);
        await Service(provider).TranslateAsync(Game, "a.json", [Line("Hello")], default);
        Assert.Equal(TranslationDevice.GPU, Assert.Single(runtime.Attempts)); Assert.Equal(1, runtime.Unloads);
    }
    [Fact]
    public async Task OutOfMemoryReducesBatchAndPreservesIds()
    {
        using var runtime = new Runtime { MaximumBatch = 2 }; using var provider = Offline(runtime, new() { Device = TranslationDevice.CPU, BatchSize = 8 });
        var items = Enumerable.Range(0, 8).Select(i => Line("Text " + i, i.ToString())).ToArray();
        var translated = await Service(provider).TranslateAsync(Game, "a.json", items, default);
        Assert.Equal(new[] { 8, 4, 2, 2, 2, 2 }, runtime.BatchSizes); Assert.Equal(8, translated.Count); Assert.Equal(8, runtime.Inputs.Distinct().Count());
    }
    [Fact]
    public async Task GpuOutOfMemoryFallsBackToCpuAfterReducingBatch()
    {
        using var runtime = new Runtime { GpuOom = true }; using var provider = Offline(runtime, new() { Device = TranslationDevice.GPU, BatchSize = 4 }, true);
        var result = await Service(provider).TranslateAsync(Game, "a.json", [Line("Hello")], default);
        Assert.Single(result); Assert.Equal(new[] { TranslationDevice.GPU, TranslationDevice.CPU }, runtime.Attempts);
    }
    [Theory]
    [InlineData("Hello {player}")][InlineData("Hello {0} %s %d\\n\\t")][InlineData("<b>Hello</b> <color=red>world</color> [i]Hi[/i]")]
    public async Task OfflineProviderPreservesPlaceholdersWithoutSendingTokensToModel(string source)
    {
        using var runtime = new Runtime(); using var provider = Offline(runtime);
        var result = await Service(provider).TranslateAsync(Game, "a.json", [Line(source)], default);
        Assert.True(new TranslationValidator().Validate(source, result["1"], out _)); Assert.DoesNotContain(runtime.Inputs, i => i.Contains("__GL_"));
    }
    [Fact]
    public async Task GlossaryNamesAreConsistentAndDoNotBlindlyReplaceSentenceFragments()
    {
        var glossary = new GlossaryService(Path.Combine(root, "glossary.json")); glossary.Save([new("Alice", "Алиса", Category: "Names")]);
        using var runtime = new Runtime(); using var provider = Offline(runtime); var service = Service(provider, glossary);
        var result = await service.TranslateAsync(Game, "a.json", [Line("Alice", "1"), Line("Alice", "2")], default);
        Assert.Equal("Алиса", result["1"]); Assert.Equal(result["1"], result["2"]); Assert.Equal(0, runtime.Loads);
        Assert.Null(glossary.Match("Talk to Alice", "Dialogue")); Assert.Equal("Алиса", new GlossaryService(Path.Combine(root, "glossary.json")).Match("Alice", "Names"));
        var before = glossary.Version; glossary.Save([new("Alice", "Элис", Category: "Names")]); Assert.NotEqual(before, glossary.Version);
        Assert.Equal("Элис", (await service.TranslateAsync(Game, "a.json", [Line("Alice")], default))["1"]);
    }
    [Theory] [InlineData(".json")][InlineData(".csv")]
    public void GlossaryImportExportHandlesQuotesCommasAndCase(string extension)
    {
        var glossary = new GlossaryService(); glossary.Save([new("Hello, \"friend\"", "Привет, друг", false), new("Alice", "Алиса", Category: "Names")]);
        var path = Path.Combine(root, "glossary" + extension); glossary.Export(path); var imported = new GlossaryService(); imported.Import(path);
        Assert.Equal(glossary.Version, imported.Version); Assert.Equal("Привет, друг", imported.Match("HELLO, \"FRIEND\"", "UI"));
        Assert.Throws<InvalidDataException>(() => imported.Save([new("Hello {name}", "Привет")]));
    }
    [Fact]
    public void BatchBuilderBoundsCountAndCharacters()
    {
        var batches = new TranslationBatchBuilder().Build(Enumerable.Range(0, 10).Select(i => Line(new string('x', 50), i.ToString())).ToArray(), 8, 120).ToArray();
        Assert.Equal(5, batches.Length); Assert.All(batches, b => Assert.Equal(2, b.Count));
    }
    private sealed class DownloadHandler(byte[] content) : HttpMessageHandler
    {
        public int Calls;
        public bool Block;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; if (Block) throw new InvalidOperationException("Network forbidden");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
    [Fact]
    public async Task ModelDownloadsOnceVerifiesCorruptionAndOfflineProviderUsesNoNetwork()
    {
        var content = Encoding.UTF8.GetBytes("synthetic model"); using var handler = new DownloadHandler(content); using var client = new HttpClient(handler);
        var info = new TranslationModelInfo("test", "Test", "v1", "test", [new("weights.bin", content.Length, Convert.ToHexString(SHA256.HashData(content)), "https://model.invalid/file")]);
        var models = new TranslationModelManager(Path.Combine(root, "Models"), client, info);
        Assert.False(models.IsInstalled); await models.DownloadModelAsync(null, default); Assert.True(await models.VerifyModelAsync(default));
        await models.DownloadModelAsync(null, default); Assert.Equal(1, handler.Calls); Assert.Single(models.GetInstalledModels());
        handler.Block = true;
        using var runtime = new Runtime(); using var provider = new LocalOfflineTranslationProvider(models, runtime, new Hardware(), new() { Device = TranslationDevice.CPU });
        await Service(provider).TranslateAsync(Game, "a.json", [Line("Hello")], default); Assert.Equal(1, handler.Calls);
        var file = Path.Combine(models.ModelDirectory, "weights.bin"); var timestamp = File.GetLastWriteTimeUtc(file);
        File.WriteAllBytes(file, Enumerable.Repeat((byte)1, content.Length).ToArray()); File.SetLastWriteTimeUtc(file, timestamp);
        Assert.False(await models.VerifyModelAsync(default));
        await models.DeleteModelAsync(default); Assert.False(models.IsInstalled); Assert.True(File.Exists(Path.Combine(root, "memory.db")));
    }
    [Fact]
    public async Task BadDownloadNeverInstallsModel()
    {
        using var handler = new DownloadHandler([1, 2, 3]); using var client = new HttpClient(handler);
        var models = new TranslationModelManager(root, client, new("test", "Test", "v1", "test", [new("weights.bin", 3, new string('0', 64), "https://model.invalid/file")]));
        await Assert.ThrowsAsync<InvalidDataException>(() => models.DownloadModelAsync(null, default)); Assert.False(models.IsInstalled);
        Assert.Empty(Directory.GetFiles(models.ModelDirectory, "*.partial-*"));
    }
    private ScanWorkspaceService Workspace(ScanResultRepository repository, ITranslationProvider provider)
    {
        var adapters = ScanRegressionTests.Adapters();
        return new(new(new(adapters), adapters, NullLogger<ScanPipeline>.Instance), repository, Service(provider), new(NullLogger<BackupService>.Instance), adapters, new(Path.Combine(root, "jobs")));
    }
    [Fact]
    public async Task CancellationPersistsCompletedRuntimeBatchesAndRestartResumesOnlyRemaining()
    {
        var file = Path.Combine(Game.Path, "dialogue.csv"); File.WriteAllText(file, "id,text\n" + string.Join("\n", Enumerable.Range(0, 24).Select(i => $"id{i},Original game line number {i}.")));
        using var runtime = new Runtime(); using var provider = Offline(runtime, new() { Device = TranslationDevice.CPU, BatchSize = 4 });
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db")); var workspace = Workspace(repository, provider);
        await workspace.ScanAsync(Game, "one", null, default); using var cancellation = new CancellationTokenSource(); runtime.AfterBatch = () => cancellation.Cancel();
        var cancelled = await workspace.RunTranslationJobAsync(Game, "one", false, false, null, cancellation.Token);
        Assert.Equal(TranslationJobStatus.Cancelled, cancelled.Status); Assert.Equal(4, cancelled.TranslatedStrings); Assert.Equal(20, cancelled.CancelledStrings); Assert.False(runtime.IsLoaded);
        Assert.NotNull(workspace.FindIncomplete(Game)); runtime.AfterBatch = null; var completedBefore = runtime.Inputs.ToArray(); runtime.Inputs.Clear();
        var restarted = Workspace(repository, provider); await restarted.ScanAsync(Game, "two", null, default);
        var preflight = await restarted.PreflightAsync(Game, "two", false, default); Assert.Equal(4, preflight.CachedStrings); Assert.Equal(20, preflight.RequiresTranslation);
        var resumed = await restarted.RunTranslationJobAsync(Game, "two", false, false, null, default);
        Assert.Equal(TranslationJobStatus.Completed, resumed.Status); Assert.Equal(20, resumed.TranslatedStrings); Assert.Equal(4, resumed.CachedStrings);
        Assert.DoesNotContain(runtime.Inputs, completedBefore.Contains); Assert.Null(restarted.FindIncomplete(Game)); Assert.False(runtime.IsLoaded);
    }
    [Fact]
    public async Task OfflineApplyAndRestoreDoNotRequireTranslatorWhenGameReadsFiles()
    {
        var file = Path.Combine(Game.Path, "menu.json"); File.WriteAllText(file, "{\"play\":\"Start Game\",\"continue\":\"Continue\"}"); var original = File.ReadAllBytes(file);
        using var runtime = new Runtime(); using var provider = Offline(runtime); using var repository = new ScanResultRepository(Path.Combine(root, "scan.db")); var workspace = Workspace(repository, provider);
        await workspace.ScanAsync(Game, "one", null, default); await workspace.TranslateAsync(Game, "one", null, default);
        Assert.Equal(original, File.ReadAllBytes(file)); Assert.False(runtime.IsLoaded);
        await workspace.ApplyAsync(Game, "one", default); var calls = runtime.Calls;
        Assert.Contains("Перевод", System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("play").GetString()); Assert.Contains("Перевод", System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("play").GetString()); Assert.Equal(calls, runtime.Calls); Assert.False(runtime.IsLoaded);
        await new BackupService(NullLogger<BackupService>.Instance).RestoreAsync(Game.Path, default); Assert.Equal(original, File.ReadAllBytes(file));
        var restarted = Workspace(repository, provider); await restarted.ScanAsync(Game, "two", null, default); Assert.All((await repository.QueryAsync("two", new(), 0, default)).Rows, r => Assert.Equal("FromMemory", r.Status));
        await restarted.TranslateAsync(Game, "two", null, default); Assert.Equal(calls, runtime.Calls);
    }
}

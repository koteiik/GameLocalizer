using System.Text;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.TranslationProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace GameLocalizer.Tests;

public sealed class IntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizerTests", Guid.NewGuid().ToString("N"));
    public IntegrationTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    private string Write(string relative, string text) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private BackupService Backup() => new(NullLogger<BackupService>.Instance);
    [Theory]
    [InlineData("UnityPlayer.dll", EngineType.Unity)] [InlineData("Demo_Data/globalgamemanagers", EngineType.Unity)]
    [InlineData("Content/Paks/demo.pak", EngineType.Unreal)] [InlineData("Content/en.locres", EngineType.Unreal)]
    [InlineData("project.godot", EngineType.Godot)] [InlineData("game/script.rpy", EngineType.RenPy)]
    [InlineData("js/rmmz_core.js", EngineType.RpgMaker)] [InlineData("other.txt", EngineType.Unknown)]
    public void DetectEngine(string path, EngineType expected) { Write(path, "synthetic"); Assert.Equal(expected, new EngineDetector().Detect(root, default).EngineType); }
    [Fact] public async Task BackupApplyAndRestoreExactBytes()
    {
        var path = Write("text/menu.json", "{\"play\":\"Play\"}"); var original = await File.ReadAllBytesAsync(path);
        await Backup().ApplyAsync(root, [new("text/menu.json", TextFiles.Hash(original), Encoding.UTF8.GetBytes("{\"play\":\"Играть\"}"))], default);
        Assert.Contains("Играть", File.ReadAllText(path)); Assert.True(File.Exists(Path.Combine(root, "GameLocalizer_Backup", "manifest.json")));
        await Backup().RestoreAsync(root, default); Assert.Equal(original, File.ReadAllBytes(path));
    }
    [Fact] public async Task RepeatedApplyNeverOverwritesOriginal()
    {
        var path = Write("a.txt", "Original"); var first = File.ReadAllBytes(path);
        await Backup().ApplyAsync(root, [new("a.txt", TextFiles.Hash(first), Encoding.UTF8.GetBytes("First"))], default);
        await Backup().ApplyAsync(root, [new("a.txt", TextFiles.Hash(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("Second"))], default);
        await Backup().RestoreAsync(root, default); Assert.Equal(first, File.ReadAllBytes(path));
    }
    [Fact] public async Task ChangedFileRejected()
    {
        var path = Write("a.txt", "Original"); var hash = TextFiles.Hash(File.ReadAllBytes(path)); File.WriteAllText(path, "Game update");
        await Assert.ThrowsAsync<IOException>(() => Backup().ApplyAsync(root, [new("a.txt", hash, [])], default)); Assert.Equal("Game update", File.ReadAllText(path));
    }
    [Fact] public async Task RestoreRejectsExternalChanges()
    {
        var path = Write("a.txt", "Original");
        await Backup().ApplyAsync(root, [new("a.txt", TextFiles.Hash(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("First"))], default);
        File.WriteAllText(path, "External"); await Assert.ThrowsAsync<IOException>(() => Backup().RestoreAsync(root, default)); Assert.Equal("External", File.ReadAllText(path));
    }
    [Fact] public async Task CorruptBackupRejected()
    {
        var path = Write("a.txt", "Original"); await Backup().ApplyAsync(root, [new("a.txt", TextFiles.Hash(File.ReadAllBytes(path)), Encoding.UTF8.GetBytes("First"))], default);
        File.WriteAllText(Directory.GetFiles(Path.Combine(root, "GameLocalizer_Backup"), "*.original")[0], "Corrupted");
        await Assert.ThrowsAsync<IOException>(() => Backup().RestoreAsync(root, default)); Assert.Equal("First", File.ReadAllText(path));
    }
    [Theory] [InlineData("../escape.txt")] [InlineData("game.exe")] [InlineData("game.dll")] [InlineData("GameLocalizer_Backup/original.txt")]
    public void UnsafePathsRejected(string path) => Assert.Throws<IOException>(() => BackupService.Resolve(root, path));
    [Fact] public async Task CancellationDoesNotChangeFile()
    {
        var path = Write("a.txt", "Original"); using var c = new CancellationTokenSource(); c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Backup().ApplyAsync(root, [new("a.txt", TextFiles.Hash(File.ReadAllBytes(path)), [])], c.Token)); Assert.Equal("Original", File.ReadAllText(path));
    }
    [Fact] public async Task EncodingPreserved()
    {
        var path = Path.Combine(root, "a.txt"); File.WriteAllText(path, "Start Game", new UnicodeEncoding(false, true));
        var file = await TextFiles.ReadAsync(path, default); var output = file.Encode("Начать игру"); Assert.Equal(new byte[] { 255, 254 }, output[..2]); Assert.Equal("Начать игру", Encoding.Unicode.GetString(output[2..]));
    }
    [Fact] public async Task TranslationMemoryPersistsAndScopes()
    {
        var path = Path.Combine(root, "memory.db"); var memory = new TranslationMemoryService(path); var now = DateTimeOffset.UtcNow;
        await memory.SaveAsync(new("Play", "Играть", "en", "ru", "42", "Demo", "a.json", "play", "menu", TranslationMemoryService.Hash("Play"), "Mock", now, now), default);
        Assert.Equal("Играть", await new TranslationMemoryService(path).FindAsync("Play", "en", "ru", "42", "menu", "Mock", default));
        Assert.Null(await memory.FindAsync("Play", "en", "ru", "43", "menu", "Mock", default));
        Assert.Null(await memory.FindAsync("Play", "en", "ru", "42", "dialogue", "Mock", default));
    }
    [Fact] public async Task TranslationBatchPreservesTokensAndUsesMemory()
    {
        var provider = new CountingProvider(); var memory = new TranslationMemoryService(Path.Combine(root, "memory.db"));
        var service = new TranslationService(provider, memory, NullLogger<TranslationService>.Instance); var game = new Game("1", "Demo", root, "Manual");
        TranslationItem[] items = [new("a", "Hello {name}", "menu"), new("b", "Hello {name}", "menu")];
        var first = await service.TranslateAsync(game, "a.json", items, default); var second = await service.TranslateAsync(game, "a.json", items, default);
        Assert.Equal(1, provider.Calls); Assert.Equal(first["a"], second["b"]); Assert.Contains("{name}", first["a"]);
    }
    private sealed class CountingProvider : GameLocalizer.Core.Interfaces.ITranslationProvider
    {
        public string Name => "Test"; public int Calls { get; private set; }
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct) { Calls++; return Task.FromResult(new TranslationResult(request.Batch.Items.ToDictionary(i => i.Id, i => "Перевод " + i.Text))); }
    }
    [Fact] public async Task FullWorkflowAllAdaptersRestoresByteForByte()
    {
        GameLocalizer.Core.Interfaces.ILocalizationAdapter[] adapters = [new GameLocalizer.Core.Localization.JsonLocalizationAdapter(), new GameLocalizer.Core.Localization.XmlLocalizationAdapter(), new GameLocalizer.Core.Localization.CsvLocalizationAdapter(), new GameLocalizer.Core.Localization.IniLocalizationAdapter(), new GameLocalizer.Core.Localization.PoLocalizationAdapter(), new GameLocalizer.Core.Localization.PlainTextLocalizationAdapter()];
        Write("menu.json", "{\"play\":\"Start Game\",\"welcome\":\"Hello {name}!\"}");
        Write("menu.xml", "<root><text id=\"play\">Start Game</text></root>");
        Write("menu.csv", "id,text\r\nplay,Start Game\r\n"); Write("menu.ini", "[menu]\r\nplay=Start Game\r\n");
        Write("menu.po", "msgid \"Start Game\"\nmsgstr \"\"\n"); Write("menu.txt", "Start Game\r\nSettings\r\n");
        var originals = Directory.GetFiles(root).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
        var scanner = new ResourceScanner(adapters); var resources = await scanner.ScanAsync(root, default);
        Assert.Equal(6, resources.Count);
        var memory = new TranslationMemoryService(Path.Combine(root, "memory.db"));
        var translator = new TranslationService(new MockTranslationProvider(), memory, NullLogger<TranslationService>.Instance);
        var changes = new List<FileChange>(); var game = new Game("sample", "Demo", root, "Manual");
        foreach (var resource in resources)
        {
            var snapshot = await TextFiles.ReadAsync(resource.Path, default); var adapter = adapters.Single(a => a.CanHandle(resource.Path));
            var entries = adapter.Extract(snapshot.Text).Where(e => new TextCandidateDetector().Score(e.Text) >= .6).ToArray();
            var translations = await translator.TranslateAsync(game, resource.Path, entries.Select(e => new TranslationItem(e.Key, e.Text, e.Context)).ToArray(), default);
            foreach (var entry in entries) Assert.True(new GameLocalizer.Core.Validation.TranslationValidator().Validate(entry.Text, translations[entry.Key], out _));
            var output = adapter.ApplyTranslations(snapshot.Text, translations); Assert.True(adapter.Validate(snapshot.Text, output, translations));
            changes.Add(new(Path.GetFileName(resource.Path), snapshot.Hash, snapshot.Encode(output)));
        }
        foreach (var (name, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, name!))); // Preview changed nothing.
        await Backup().ApplyAsync(root, changes, default);
        Assert.Contains("Начать игру", File.ReadAllText(Path.Combine(root, "menu.txt")));
        await Backup().RestoreAsync(root, default);
        foreach (var (name, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, name!)));
    }
}

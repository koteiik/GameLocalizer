using System.Text;
using System.Text.Json;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.TranslationProviders;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class BepInExLocalizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizerKeyValue", Guid.NewGuid().ToString("N"));
    private readonly BepInExLocalizationAdapter adapter = new();
    public BepInExLocalizationTests() => Directory.CreateDirectory(root);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    [Theory]
    [InlineData("ありがとう", "Thank you.", "Спасибо.")]
    [InlineData("NoName", "No Name", "Без имени")]
    [InlineData("FK関節", "FK Joint", "FK-сустав")]
    [InlineData("BGM", "Game BGM", "Игровая музыка")]
    [InlineData("e\u0301", "Welcome home.", "Добро пожаловать домой.")]
    public void JapaneseLatinAndNonNormalizedKeysRemainUnchanged(string key, string value, string russian)
    {
        var source = key + "=" + value; var entry = Assert.Single(adapter.Extract(source));
        Assert.Equal(key, entry.Key); Assert.Equal(value, entry.Text);
        var edits = new Dictionary<string, string> { [entry.Id] = russian };
        var output = adapter.ApplyTranslations(source, edits);
        Assert.Equal(key + "=" + russian, output); Assert.True(adapter.Validate(source, output, edits));
        Assert.Equal(Encoding.UTF8.GetBytes(key + "="), Encoding.UTF8.GetBytes(output[..(key.Length + 1)]));
    }

    [Theory]
    [InlineData("Formula=A=B+C", "Formula", "A=B+C")]
    [InlineData(@"a\=b=Thank you.=Again", @"a\=b", "Thank you.=Again")]
    [InlineData(@"a\\=Thank you.", @"a\\", "Thank you.")]
    [InlineData(@"a\\\=b=Thank you.", @"a\\\=b", "Thank you.")]
    [InlineData(@"a%3Db=Thank you.", @"a%3Db", "Thank you.")]
    public void SplitAtFirstUnescapedSeparatorOnly(string source, string key, string value)
    {
        var entry = Assert.Single(adapter.Extract(source)); Assert.Equal(key, entry.Key); Assert.Equal(value, entry.Text);
        Assert.Equal(source, adapter.ApplyTranslations(source, new Dictionary<string, string> { [entry.Id] = value }));
    }

    [Fact]
    public void WhitespaceCommentsDuplicatesAndMixedLineEndingsArePreserved()
    {
        const string source = "# note=Ignore this\r\n; note=Ignore this\n // note=Ignore this\r# set level\r\n" +
            " ありがとう \t= \tThank you.  // retained\r\nありがとう=Thank you.\n" +
            "r:expression=Do not translate regex\r\nsr:expression=Do not translate splitter\r\nempty=  \r\nNoName=No Name";
        var entries = adapter.Extract(source); Assert.Equal(3, entries.Count);
        Assert.Equal("ありがとう", entries[0].Key); Assert.Equal(entries[0].Key, entries[1].Key); Assert.NotEqual(entries[0].Id, entries[1].Id);
        var edits = new Dictionary<string, string> { [entries[0].Id] = "Спасибо.", [entries[1].Id] = "Благодарю.", [entries[2].Id] = "Без имени" };
        var expected = source.Replace("Thank you.  //", "Спасибо.  //").Replace("=Thank you.\n", "=Благодарю.\n").Replace("=No Name", "=Без имени");
        Assert.Equal(expected, adapter.ApplyTranslations(source, edits)); Assert.True(adapter.Validate(source, expected, edits));
        Assert.False(adapter.Validate(source, expected.Replace("ありがとう", "changed"), edits));
    }

    [Theory]
    [InlineData(@"Hello\nthere", @"Привет\nдруг")]
    [InlineData(@"Hello\tthere", @"Привет\tдруг")]
    [InlineData(@"Hello\=there", @"Привет\=друг")]
    [InlineData(@"Hello\\there", @"Привет\\друг")]
    [InlineData(@"Hello\u180ethere", @"Привет\u180eдруг")]
    [InlineData(@"Hello%3Dthere", @"Привет%3Dдруг")]
    [InlineData(@"Hello\/\/there", @"Привет\/\/друг")]
    public void EscapesAreProtectedAndRoundTrip(string value, string translated)
    {
        var protector = new PlaceholderProtector(); var protectedText = protector.Protect(value);
        Assert.NotEmpty(protectedText.Tokens); Assert.Equal(value, protector.Restore(protectedText.Text, protectedText.Tokens));
        Assert.True(new TranslationValidator().Validate(value, translated, out _));
        var entry = Assert.Single(adapter.Extract("key=" + value));
        Assert.Equal("key=" + translated, adapter.ApplyTranslations("key=" + value, new Dictionary<string, string> { [entry.Id] = translated }));
    }

    [Theory]
    [InlineData("Injected\nkey=value")][InlineData("Injected\rkey=value")][InlineData("Привет // removed")]
    [InlineData(" Привет")][InlineData("Привет ")][InlineData("")]
    public void InvalidManualValueCannotChangePhysicalStructure(string translated)
    {
        var entry = Assert.Single(adapter.Extract("key=Hello"));
        var edits = new Dictionary<string, string> { [entry.Id] = translated };
        Assert.Throws<InvalidDataException>(() => adapter.ApplyTranslations("key=Hello", edits));
    }

    [Theory]
    [InlineData("BepInEx/Translation/en/Text/dialogue.txt")]
    [InlineData("BepInEx/Translations/en/dialogue.txt")]
    [InlineData("BepInEx/plugins/XUnity.AutoTranslator/dialogue.txt")]
    [InlineData("XUnity.AutoTranslator/dialogue.txt")]
    [InlineData("Translation/dialogue.txt")][InlineData("Translations/dialogue.txt")]
    public void SpecializedAdapterHasPriorityInLocalizationPaths(string path)
    {
        Assert.True(ResourceClassifier.IsExtractable(new ResourceClassifier().Classify(path)));
        Assert.IsType<BepInExLocalizationAdapter>(LocalizationAdapterSelector.Select(ScanRegressionTests.Adapters(), path, "ありがとう=Thank you."));
        Assert.IsType<BepInExLocalizationAdapter>(LocalizationAdapterSelector.Select(ScanRegressionTests.Adapters(), path, "# comment=value\n// comment"));
        Assert.IsType<BepInExLocalizationAdapter>(LocalizationAdapterSelector.Select(ScanRegressionTests.Adapters(), path, "// comment only"));
        Assert.IsType<PlainTextLocalizationAdapter>(LocalizationAdapterSelector.Select(ScanRegressionTests.Adapters(), path, "Welcome to the village."));
    }

    [Theory]
    [InlineData("BepInEx/config/AutoTranslatorConfig.ini")]
    [InlineData("Translation/en/Text/_Preprocessors.txt")][InlineData("Translation/en/Text/_Postprocessors.txt")]
    [InlineData("Translation/en/Text/_Substitutions.txt")][InlineData("Translation/en/Text/_Resizer.txt")]
    public void ConfigAndAuxiliaryRulesRemainTechnical(string path) => Assert.False(ResourceClassifier.IsExtractable(new ResourceClassifier().Classify(path)));

    private sealed class SpyProvider : ITranslationProvider
    {
        public string Name => "KeyValueSpy";
        public List<TranslationRequest> Requests { get; } = [];
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TranslationResult(request.Batch.Items.ToDictionary(i => i.Id, i => i.Text switch
            { "Thank you." => "Спасибо.", "No Name" => "Без имени", "FK Joint" => "FK-сустав", "Game BGM" => "Игровая музыка", _ => throw new InvalidDataException(i.Text) })));
        }
    }
    private ScanWorkspaceService Workspace(ScanResultRepository repository, SpyProvider provider)
    {
        var adapters = ScanRegressionTests.Adapters();
        return new(new(new(adapters), adapters, NullLogger<ScanPipeline>.Instance), repository,
            new(provider, new TranslationMemoryService(Path.Combine(root, "memory.db")), NullLogger<TranslationService>.Instance),
            new(NullLogger<BackupService>.Instance), adapters);
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public async Task Test20ValueOnlyManualApplyAndByteExactRestore(int encodingKind)
    {
        Encoding encoding = encodingKind switch { 0 => new UTF8Encoding(false), 1 => new UTF8Encoding(true), 2 => new UnicodeEncoding(false, true),
            3 => new UnicodeEncoding(true, true), 4 => new UTF32Encoding(false, true), _ => new UTF32Encoding(true, true) };
        var gameRoot = Path.Combine(root, "game"); var folder = Path.Combine(gameRoot, "BepInEx", "Translation", "en", "Text"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "dialogue.txt");
        const string source = "// synthetic\r\nありがとう = Thank you. \t// keep\r\nNoName=No Name\nFK関節=FK Joint\rBGM=Game BGM\r\nありがとう=Thank you.";
        await File.WriteAllTextAsync(path, source, encoding); var original = await File.ReadAllBytesAsync(path);
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db")); var spy = new SpyProvider(); var workspace = Workspace(repository, spy);
        var game = new Game("synthetic", "Synthetic", gameRoot, "Manual"); await workspace.ScanAsync(game, "scan", null, default);
        var rows = await workspace.GetTestRowsAsync("scan", default); Assert.Equal(5, rows.Count);
        Assert.Equal(new[] { "ありがとう", "NoName", "FK関節", "BGM", "ありがとう" }, rows.Select(r => r.DisplayKey));
        Assert.All(rows, r => { Assert.True(r.Selected); Assert.DoesNotContain('=', r.Original); });
        var job = await workspace.RunTranslationJobAsync(game, "scan", false, true, null, default);
        Assert.Equal(TranslationJobStatus.Completed, job.Status); Assert.Equal(original, await File.ReadAllBytesAsync(path));
        var sent = spy.Requests.SelectMany(r => r.Batch.Items).ToArray(); Assert.Equal(4, sent.Length);
        Assert.Equal(new[] { "Thank you.", "No Name", "FK Joint", "Game BGM" }, sent.Select(i => i.Text));
        Assert.All(sent, i => { Assert.Empty(i.Key); Assert.True(int.TryParse(i.Id, out _)); Assert.Equal("XUnity key-value localization", i.Context); });
        var json = JsonSerializer.Serialize(spy.Requests); Assert.DoesNotContain("NoName", json); Assert.DoesNotContain("\\u3042", json);
        var edit = new ScanEdit(rows[1].Id, "Безымянный", true);
        await repository.SaveEditsAsync("scan", [edit], default); await workspace.SaveManualEditsAsync(game, "scan", [edit], default);
        await workspace.RunTranslationJobAsync(game, "scan", true, false, null, default);
        Assert.Equal("Безымянный", (await repository.RowsByIdAsync("scan", new HashSet<long> { rows[1].Id }, default)).Single().Translation);
        Assert.Equal(1, await workspace.ApplyAsync(game, "scan", default));
        var expected = source.Replace("Thank you.", "Спасибо.").Replace("No Name", "Безымянный").Replace("FK Joint", "FK-сустав").Replace("Game BGM", "Игровая музыка");
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)).ToArray(), await File.ReadAllBytesAsync(path));
        await new BackupService(NullLogger<BackupService>.Instance).RestoreAsync(gameRoot, default);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        // A different immutable key reuses exactly the same RHS cache, including when selecting Text itself as root.
        await File.WriteAllTextAsync(path, "感謝=Thank you.", encoding); var requestCount = spy.Requests.Count;
        await workspace.ScanAsync(new Game(game.Id, game.Name, folder, "Manual"), "rescan", null, default);
        var cached = Assert.Single((await repository.QueryAsync("rescan", new(), 0, default)).Rows);
        Assert.Equal("感謝", cached.DisplayKey); Assert.Equal("Thank you.", cached.Original); Assert.Equal("Спасибо.", cached.Translation); Assert.Equal("FromMemory", cached.Status);
        Assert.Equal(requestCount, spy.Requests.Count);
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "memory.db")}"); await db.OpenAsync();
        using var command = db.CreateCommand(); command.CommandText = "SELECT SourceText, Key FROM TranslationMemory";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { Assert.DoesNotContain('=', reader.GetString(0)); Assert.DoesNotContain("kv:", reader.GetString(1)); }
    }

    [Fact]
    public async Task ProviderNeverReceivesKeysEvenWhenCallerUsesKeyAsId()
    {
        var spy = new SpyProvider(); var service = new TranslationService(spy, new TranslationMemoryService(Path.Combine(root, "memory.db")), NullLogger<TranslationService>.Instance);
        var game = new Game("test", "Synthetic", root, "Manual");
        var result = await service.TranslateAsync(game, "dialogue.txt", [new("ありがとう", "Thank you.", "XUnity key-value localization", "Localization", "ありがとう")], default);
        Assert.Equal("Спасибо.", result["ありがとう"]); var sent = Assert.Single(Assert.Single(spy.Requests).Batch.Items);
        Assert.Equal("0", sent.Id); Assert.Empty(sent.Key); Assert.Equal("Thank you.", sent.Text);
        var cached = await service.FindAsync(game, new("感謝", "Thank you.", "XUnity key-value localization", "Localization", "感謝"), default);
        Assert.Equal("Спасибо.", cached?.Text);
    }
}

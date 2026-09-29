using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class ModInfrastructureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizerModTests", Guid.NewGuid().ToString("N"));
    private string Write(string path, string text)
    {
        var full = Path.GetFullPath(Path.Combine(root, path)); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); return full;
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Theory]
    [InlineData("BepInEx/config/AutoTranslatorConfig.ini", ResourceKind.ToolConfiguration)]
    [InlineData("bepinex/Launcher/menu.json", ResourceKind.ToolConfiguration)]
    [InlineData("BepInEx/core/readme.txt", ResourceKind.ModInfrastructure)]
    [InlineData("BepInEx/plugins/dialogue.txt", ResourceKind.ModInfrastructure)]
    [InlineData("BepInEx/patchers/settings.json", ResourceKind.ModInfrastructure)]
    [InlineData("MelonLoader/menu.json", ResourceKind.ModInfrastructure)]
    [InlineData("Mods/plugin/options.ini", ResourceKind.ToolConfiguration)]
    [InlineData("modloader/help.txt", ResourceKind.ModInfrastructure)]
    [InlineData("doorstop_config.ini", ResourceKind.ToolConfiguration)]
    [InlineData("winhttp.dll.config", ResourceKind.ToolConfiguration)]
    [InlineData("AutoTranslatorConfig.ini", ResourceKind.ToolConfiguration)]
    [InlineData("XUnity.AutoTranslator/config.json", ResourceKind.ToolConfiguration)]
    [InlineData("Text/BepInEx/config/menu.ini", ResourceKind.ToolConfiguration)]
    [InlineData("config/menu.json", ResourceKind.ToolConfiguration)]
    [InlineData("Tools/settings.xml", ResourceKind.ToolConfiguration)]
    public void InfrastructureCannotBecomePlayerText(string path, ResourceKind expected)
    {
        var kind = new ResourceClassifier().Classify(path);
        Assert.Equal(expected, kind); Assert.False(ResourceClassifier.IsExtractable(kind));
        Assert.Equal(TextCategory.Technical, ResourceClassifier.Category(kind));
        Assert.Equal(0, new TextCandidateDetector().Score("Continue", source: kind));
    }
    [Theory]
    [InlineData("Times New Roman")][InlineData("Arial")][InlineData("Segoe UI")][InlineData("UTF-8")]
    [InlineData("VERSION")][InlineData("[UTILITY] KKManager")][InlineData("ReplaceMacronWithCircumflex")]
    [InlineData("ReplaceMacronWithCircumflex;RemoveSomething")][InlineData(@"\u180e;")]
    [InlineData("1-1;1-2;Shortcut_Custom")][InlineData("Ctrl+Shift+F1")][InlineData("v1.2.3")]
    [InlineData(@"^.*\d+$")]
    public void ConfigurationValuesAreTechnical(string value)
    {
        var detector = new TextCandidateDetector();
        Assert.True(detector.Score(value, path: "preferences.ini") < .35);
        Assert.Equal(0, detector.Score(value, path: "BepInEx/config/AutoTranslatorConfig.ini"));
    }
    [Theory]
    [InlineData("Times New Roman")][InlineData("Arial")][InlineData("Segoe UI")][InlineData("[UTILITY] KKManager")]
    public void ConfigurationVocabularyIsNotAGlobalBlacklist(string value) =>
        Assert.True(new TextCandidateDetector().Score(value, source: ResourceKind.DialogueResource, path: "Dialogue/en.txt") >= .35);
    [Fact]
    public void UiFontAndOptionsKeysAreStrongGameContext()
    {
        var detector = new TextCandidateDetector();
        Assert.True(detector.Score("Times New Roman", source: ResourceKind.UIResource, path: "UI/en.json", key: "font") >= .85);
        Assert.True(detector.Score("Options", source: ResourceKind.UIResource, path: "UI/en.json", key: "options") >= .85);
        Assert.True(detector.Score("Times New Roman", path: "preferences.json", key: "font") < .35);
    }
    [Theory]
    [InlineData("Translation/en/Text/dialogue.txt")][InlineData("Translations/en.json")]
    [InlineData("Localization/en.xml")][InlineData("Language/en.ini")][InlineData("Text/en.po")]
    [InlineData("plugins/Example/Managed/Translation/en/Text/dialogue.txt")]
    public async Task ExplicitModLocalizationSurvivesTraversal(string relative)
    {
        var path = Write("BepInEx/" + relative, relative.EndsWith(".xml") ? "<text>Continue</text>" : relative.EndsWith(".json") ? "{\"play\":\"Continue\"}" : relative.EndsWith(".ini") ? "play=Continue" : relative.EndsWith(".po") ? "msgid \"Continue\"\nmsgstr \"\"" : "Continue");
        Write("BepInEx/config/AutoTranslatorConfig.ini", "font=Times New Roman\noptions=ReplaceMacronWithCircumflex;Remove\nplugin=[UTILITY] KKManager");
        var scanner = new ResourceScanner(ScanRegressionTests.Adapters());
        Assert.True(scanner.Enumerate(root, default).Single(r => r.Path == path).Editable);
        var entries = new List<ScanEntry>();
        await foreach (var batch in new ScanPipeline(scanner, ScanRegressionTests.Adapters(), NullLogger<ScanPipeline>.Instance).ScanAsync(root, default)) entries.AddRange(batch.Entries);
        var row = Assert.Single(entries); Assert.Equal("Continue", row.Original); Assert.True(row.Selected);
    }
    [Fact]
    public void SelectingModConfigAsRootDoesNotLoseItsContext()
    {
        Write("BepInEx/config/menu.ini", "label=Continue");
        var file = Assert.Single(new ResourceScanner(ScanRegressionTests.Adapters()).Enumerate(Path.Combine(root, "BepInEx/config"), default));
        Assert.Equal(ResourceKind.ToolConfiguration, file.Kind); Assert.False(file.Editable);
    }
    [Theory]
    [InlineData("preferences.ini")][InlineData("preferences.cfg")][InlineData("preferences.config")]
    public void ConfigurationWithoutLocalizationEvidenceCannotAutoSelect(string path) =>
        Assert.True(new TextCandidateDetector().Score("Welcome to the launcher configuration.", path: path) < .85);
    [Fact]
    public void NumberedDialogueFilesHaveExplicitDialogueEvidence() =>
        Assert.Equal(ResourceKind.DialogueResource, new ResourceClassifier().Classify("dialogue-01.csv"));
    [Fact]
    public async Task PossibleHighConfidenceRemainsVisibleUnselectedAndNeverTranslatedAutomatically()
    {
        Write("notes.json", "{\"label\":\"Welcome to the launcher configuration.\"}");
        Write("preferences.ini", "label=Welcome to the launcher configuration.");
        Write("UI/en.json", "{\"continue\":\"Continue\"}");
        Write("Dialogue/en.txt", "I don't think we should go there.");
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db"));
        var workspace = ScanRegressionTests.Workspace(repository, Path.Combine(root, "memory.db"));
        var game = new Game("test", "Synthetic", root, "Manual");
        await workspace.ScanAsync(game, "scan", null, default);
        var page = await repository.QueryAsync("scan", new(), 0, default);
        Assert.Equal(4, page.Total); Assert.Equal(2, page.Selected); Assert.Equal(2, page.UserText); Assert.Equal(2, page.Doubtful);
        var possible = Assert.Single(page.Rows, r => r.FilePath == "notes.json");
        Assert.True(possible.Confidence >= .85); Assert.Equal(TextCategory.Possible, possible.Category); Assert.False(possible.Selected);
        Assert.True(page.Rows.Single(r => r.FilePath == "preferences.ini").Confidence < .85);
        Assert.Equal(2, (await repository.QueryAsync("scan", new(Filter: "Сомнительные"), 0, default)).Rows.Count);
        Assert.Equal(2, (await repository.QueryAsync("scan", new(Filter: "Высокая уверенность"), 0, default)).Rows.Count);
        Assert.Equal(2, await workspace.TranslateAsync(game, "scan", null, default));
        var after = await repository.QueryAsync("scan", new(), 0, default);
        Assert.All(after.Rows.Where(r => r.Category == TextCategory.Possible), r => Assert.Empty(r.Translation));
    }
    [Theory]
    [InlineData(TextCategory.Possible, false)][InlineData(TextCategory.Technical, false)][InlineData(TextCategory.Names, false)]
    [InlineData(TextCategory.UI, true)][InlineData(TextCategory.Dialogue, true)][InlineData(TextCategory.Subtitle, true)]
    [InlineData(TextCategory.Localization, true)][InlineData(TextCategory.Quest, true)][InlineData(TextCategory.Item, true)][InlineData(TextCategory.Story, true)]
    public void AutoSelectRequiresBothSourceCategoryAndConfidence(TextCategory category, bool expected)
    {
        Assert.Equal(expected, ResourceClassifier.CanAutoSelect(.99, category));
        Assert.False(ResourceClassifier.CanAutoSelect(.84, category));
    }
}

using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class UnityFilteringTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizerUnityTests", Guid.NewGuid().ToString("N"));
    public UnityFilteringTests() => Directory.CreateDirectory(root);
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    private string Write(string path, string text)
    {
        var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); return full;
    }
    [Theory]
    [InlineData("UnityEngine.AccessibilityModule")][InlineData("UnityEngine.AIModule")][InlineData("System.Collections.Generic")]
    [InlineData("Namespace.Class")][InlineData("Namespace.Class.Method")][InlineData("Uses UnityEngine.Vector3 internally.")]
    [InlineData("Microsoft.Extensions.Logging")][InlineData("Mono.Runtime")][InlineData("Il2Cpp.Type")]
    [InlineData("Assembly-CSharp")][InlineData("mscorlib")][InlineData("netstandard")]
    public void SymbolsCannotBePromotedByLocalizationPath(string value) => Assert.True(new TextCandidateDetector().Score(value, source: ResourceKind.LocalizationCandidate) < .35);
    [Theory]
    [InlineData("A class containing methods to assist with accessibility")][InlineData("Singleton class to access the baked NavMesh")]
    [InlineData("Gets the current value")][InlineData("Sets the current value")][InlineData("Returns the current value")]
    [InlineData("Describes how far in the future the agents predict")][InlineData("The maximum amount of nodes processed each iteration")]
    [InlineData("The minimum number of iterations")][InlineData("An array of engine objects")][InlineData("Adds a link to the navigation mesh")]
    [InlineData("Method used to render a frame")][InlineData("Property that controls rendering")]
    public void DocumentationContextExcludesEnglishSummaries(string value)
    {
        var detector = new TextCandidateDetector();
        Assert.Equal(0, detector.Score(value, source: ResourceKind.TechnicalDocumentation));
        Assert.Equal(0, detector.Score(value, source: ResourceKind.EngineRuntime));
    }
    [Theory]
    [InlineData("I don't think we should go there.", ResourceKind.DialogueResource)]
    [InlineData("Continue", ResourceKind.UIResource)]
    [InlineData("Run for your life!", ResourceKind.SubtitleResource)]
    [InlineData("Returns from the war are never easy.", ResourceKind.DialogueResource)]
    [InlineData("The maximum reward is yours!", ResourceKind.DialogueResource)]
    [InlineData("Gets me every time!", ResourceKind.DialogueResource)]
    public void PlayerTextRemainsHighlyConfident(string value, ResourceKind source) => Assert.True(new TextCandidateDetector().Score(value, source: source) >= .85);
    [Fact]
    public void LocalizationEvidenceBoostsButDoesNotRescueTechnicalText()
    {
        var detector = new TextCandidateDetector();
        Assert.True(detector.Score("Find the hidden treasure", source: ResourceKind.LocalizationCandidate) > detector.Score("Find the hidden treasure"));
        Assert.True(detector.Score("m_LocalPosition", source: ResourceKind.LocalizationCandidate) < .35);
    }
    [Theory]
    [InlineData("Managed")][InlineData("MonoBleedingEdge")][InlineData("Demo_Data/Managed")][InlineData("Demo_Data/Mono")]
    [InlineData("Demo_Data/Plugins")][InlineData("Demo_Data/il2cpp_data")][InlineData("Demo_Data/Native")]
    public void RuntimeDirectoriesArePrunedBeforeOpeningAnyChild(string directory)
    {
        Write(directory + "/must-not-read.xml", "malformed XML, deliberately not parsed");
        var resources = new ResourceScanner(ScanRegressionTests.Adapters()).Enumerate(root, default).ToArray();
        Assert.DoesNotContain(resources, r => r.Path.EndsWith("must-not-read.xml"));
        Assert.Contains(resources, r => r.Kind == ResourceKind.EngineRuntime && !r.Editable);
    }
    [Theory]
    [InlineData("engine.pdb", ResourceKind.AssemblyMetadata)][InlineData("engine.deps.json", ResourceKind.AssemblyMetadata)]
    [InlineData("engine.runtimeconfig.json", ResourceKind.AssemblyMetadata)][InlineData("Packages/packages-lock.json", ResourceKind.AssemblyMetadata)]
    [InlineData("Packages/manifest.json", ResourceKind.AssemblyMetadata)][InlineData("assembly.asmdef", ResourceKind.AssemblyMetadata)]
    [InlineData("Demo_Data/Resources/unity_builtin_extra", ResourceKind.EngineRuntime)]
    [InlineData("Dialogue/en.csv", ResourceKind.DialogueResource)][InlineData("Subtitles/en.txt", ResourceKind.SubtitleResource)]
    [InlineData("UI/en.json", ResourceKind.UIResource)][InlineData("Texts/en.txt", ResourceKind.LocalizationCandidate)]
    public void SourceKinds(string path, ResourceKind expected) => Assert.Equal(expected, new ResourceClassifier().Classify(path));
    [Fact]
    public void SelectingManagedItselfDoesNotBypassRuntimeExclusion()
    {
        Write("Managed/notes.txt", "A class containing engine methods");
        var resource = Assert.Single(new ResourceScanner(ScanRegressionTests.Adapters()).Enumerate(Path.Combine(root, "Managed"), default));
        Assert.Equal("Directory", resource.Format); Assert.False(resource.Editable);
    }
    [Theory]
    [InlineData("T:UnityEngine.Agent")][InlineData("M:UnityEngine.Agent.Move")]
    public void DocumentationXmlOutsideRuntimeIsExcluded(string member)
    {
        Write("api.xml", $"<doc><members><member name=\"{member}\"><summary>A class containing methods to assist with accessibility</summary><param name=\"n\">The maximum number</param><returns>An array of values</returns></member></members></doc>");
        var resource = Assert.Single(new ResourceScanner(ScanRegressionTests.Adapters()).Enumerate(root, default));
        Assert.Equal(ResourceKind.TechnicalDocumentation, resource.Kind); Assert.False(resource.Editable);
    }
    [Fact]
    public void XmlBesideAssemblyIsExcludedButGameXmlIsNot()
    {
        Write("engine.dll", "synthetic marker, not an assembly"); Write("engine.xml", "<root>Continue</root>");
        Write("menu.xml", "<root><text>Continue</text></root>");
        var resources = new ResourceScanner(ScanRegressionTests.Adapters()).Enumerate(root, default).ToArray();
        Assert.Equal(ResourceKind.TechnicalDocumentation, resources.Single(r => r.Path.EndsWith("engine.xml")).Kind);
        Assert.True(resources.Single(r => r.Path.EndsWith("menu.xml")).Editable);
    }
    [Fact]
    public async Task SyntheticUnityFilters12TechnicalValuesKeeps10PlayerAnd2Doubtful()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "UnitySynthetic");
        var game = new Game("synthetic", "UnitySynthetic", source, "Manual");
        var resources = new ResourceScanner(ScanRegressionTests.Adapters()).Enumerate(source, default).ToArray();
        Assert.DoesNotContain(resources, r => r.Path.EndsWith("SyntheticUnity.xml") || r.Path.EndsWith("runtime.txt"));
        Assert.False(resources.Single(r => r.Path.EndsWith("EngineApi.xml")).Editable);
        // Count known fixture values independently of production scanning, including excluded source files.
        var xml = new GameLocalizer.Core.Localization.XmlLocalizationAdapter();
        var managed = xml.Extract(File.ReadAllText(Path.Combine(source, "UnityDemo_Data/Managed/SyntheticUnity.xml"))).Count(e => e.Context == "summary");
        var docs = xml.Extract(File.ReadAllText(Path.Combine(source, "Documentation/EngineApi.xml"))).Count;
        var runtime = File.ReadAllLines(Path.Combine(source, "UnityDemo_Data/Mono/runtime.txt")).Length;
        Assert.Equal(7, managed + docs + runtime);
        using var repository = new ScanResultRepository(Path.Combine(root, "scan.db"));
        var workspace = ScanRegressionTests.Workspace(repository, Path.Combine(root, "memory.db"));
        await workspace.ScanAsync(game, "scan", null, default);
        var normal = await repository.QueryAsync("scan", new(), 0, default);
        Assert.Equal(17, normal.Total); Assert.Equal(10, normal.UserText); Assert.Equal(2, normal.Doubtful); Assert.Equal(5, normal.Technical);
        Assert.Equal(12, managed + docs + runtime + normal.Technical);
        Assert.Equal(10, normal.Selected); Assert.Equal(11, normal.Rows.Count); // medium confidence visible; low hidden
        Assert.DoesNotContain(normal.Rows, r => r.Category == TextCategory.Technical);
        Assert.Contains(normal.Rows, r => r.Category is TextCategory.Localization or TextCategory.ShortUI);
        Assert.Contains(normal.Rows, r => r.Category == TextCategory.Dialogue);
        Assert.Contains(normal.Rows, r => r.Category == TextCategory.Subtitle);
        Assert.False(normal.Rows.Single(r => r.Original == "Lantern").Selected);
        var doubtful = await repository.QueryAsync("scan", new(Filter: "Сомнительные", MinimumConfidence: 1), 0, default);
        Assert.Equal(2, doubtful.Rows.Count); Assert.All(doubtful.Rows, r => Assert.False(r.Selected));
        var technical = await repository.QueryAsync("scan", new(Filter: "Технические", MinimumConfidence: 1), 0, default);
        Assert.Equal(5, technical.Rows.Count); Assert.All(technical.Rows, r => Assert.False(r.Selected));
        // Even a forged edit cannot select technical rows for translation/application.
        await repository.SaveEditsAsync("scan", technical.Rows.Select(r => new ScanEdit(r.Id, "Техническая строка", true)).ToArray(), default);
        var selected = await repository.QueryAsync("scan", new(Filter: "Выбранные"), 0, default);
        var high = await repository.QueryAsync("scan", new(Filter: "Высокая уверенность"), 0, default);
        Assert.Equal(10, selected.Rows.Count); Assert.Equal(10, high.Rows.Count);
        Assert.Equal(10, await workspace.TranslateAsync(game, "scan", null, default));
        Assert.All(await repository.ReadSelectedAsync("scan", 0, false, default), r => Assert.NotEqual(TextCategory.Technical, r.Category));
    }
}

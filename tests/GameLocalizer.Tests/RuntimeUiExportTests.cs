using System.Text;
using System.Text.Json;
using GameLocalizer.Infrastructure.Runtime;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class RuntimeUiExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RuntimeUiExport-" + Guid.NewGuid().ToString("N"));
    public RuntimeUiExportTests() => Directory.CreateDirectory(root);
    private static RuntimeUiEntry Entry(int index) => new()
    {
        Text = $"日本語 — Дать совет {index}\n\"quoted\"", Scene = "Map", Object = "Текст",
        Hierarchy = "MapScene/日本語/Команды/Text", Component = "UnityEngine.UI.Text", Assembly = "UnityEngine.UI",
        SeenCount = index + 4, FirstSeen = DateTimeOffset.Parse("2026-10-02T12:00:00+03:00"),
        LastSeen = DateTimeOffset.Parse("2026-10-02T12:30:00+03:00"), MatchStatus = "RuntimeOnly", Source = "RuntimeCollector"
    };

    [Fact] public void JsonExportsAll571ImportedRowsAndAllRequiredFields()
    {
        var imported = Enumerable.Range(0, 571).Select(Entry).ToArray();
        var result = RuntimeUiExport.Write(Path.Combine(root, "export.json"), RuntimeUiExport.Snapshot(imported));
        using var json = JsonDocument.Parse(File.ReadAllText(result.JsonPath, Encoding.UTF8));
        Assert.Equal(571, result.Count); Assert.Equal(571, json.RootElement.GetArrayLength());
        for (var i = 0; i < imported.Length; i++)
        {
            var source = imported[i]; var row = json.RootElement[i];
            Assert.Equal(source.Text, row.GetProperty("Text").GetString());
            Assert.Equal(source.Scene, row.GetProperty("Scene").GetString());
            Assert.Equal(source.Object, row.GetProperty("Object").GetString());
            Assert.Equal(source.Hierarchy, row.GetProperty("Hierarchy").GetString());
            Assert.Equal(source.Component, row.GetProperty("Component").GetString());
            Assert.Equal(source.Assembly, row.GetProperty("Assembly").GetString());
            Assert.Equal(source.SeenCount, row.GetProperty("SeenCount").GetInt64());
            Assert.Equal(source.FirstSeen, row.GetProperty("FirstSeen").GetDateTimeOffset());
            Assert.Equal(source.LastSeen, row.GetProperty("LastSeen").GetDateTimeOffset());
            Assert.Equal(source.MatchStatus, row.GetProperty("Match").GetString());
            Assert.Equal(source.Source, row.GetProperty("Source").GetString());
        }
        Assert.Contains("日本語 — Дать совет", File.ReadAllText(result.JsonPath));
    }

    [Fact] public void TxtExportsEveryRowWithUnicodeHierarchyCountsAndContext()
    {
        var rows = Enumerable.Range(0, 571).Select(Entry).ToArray();
        var result = RuntimeUiExport.Write(Path.Combine(root, "export.txt"), RuntimeUiExport.Snapshot(rows));
        var text = File.ReadAllText(result.TextPath, Encoding.UTF8);
        Assert.Equal(571, text.Split("\n---\n").Length - 1 + text.Split("\r\n---\r\n").Length - 1);
        foreach (var row in rows) Assert.Contains($"Text: {row.Text}", text);
        foreach (var expected in new[] {"Scene: Map", "Object: Текст", "Hierarchy: MapScene/日本語/Команды/Text",
                     "Component: UnityEngine.UI.Text", "Assembly: UnityEngine.UI", "SeenCount: 574",
                     "FirstSeen: 2026-10-02T12:00:00.0000000+03:00", "LastSeen: 2026-10-02T12:30:00.0000000+03:00",
                     "Match: RuntimeOnly", "Source: RuntimeCollector"}) Assert.Contains(expected, text);
        Assert.True(File.Exists(result.JsonPath));
    }

    [Fact] public void SnapshotDoesNotChangeWhenImportedEntriesAreEdited()
    {
        var entry = Entry(0); var snapshot = RuntimeUiExport.Snapshot([entry]);
        entry.Text = "changed"; entry.SeenCount = 999;
        var result = RuntimeUiExport.Write(Path.Combine(root, "snapshot.json"), snapshot);
        var row = JsonSerializer.Deserialize<RuntimeUiExportRow[]>(File.ReadAllText(result.JsonPath))![0];
        Assert.Equal(Entry(0).Text, row.Text); Assert.Equal(4, row.SeenCount);
    }

    [Fact] public void EmptyExportCreatesNoFiles()
    {
        Assert.Throws<InvalidOperationException>(() => RuntimeUiExport.Write(Path.Combine(root, "empty.json"), []));
        Assert.Empty(Directory.EnumerateFiles(root));
    }

    [Fact] public void MissingOrInvalidDataFolderNeverCallsLauncher()
    {
        var called = false;
        Assert.False(RuntimeUiDataFolder.TryOpen(root, _ => called = true, out var missing));
        Assert.Contains("ещё не существует", missing);
        Assert.False(RuntimeUiDataFolder.TryOpen("\0invalid", _ => called = true, out var invalid));
        Assert.Contains("Не удалось открыть", invalid); Assert.False(called);
    }

    [Fact] public void DataFolderLauncherReceivesExactRawCaptureFolderAndHandlesShellFailure()
    {
        var folder = RuntimeCollectorService.DataDirectory(root); Directory.CreateDirectory(folder);
        try
        {
            string? opened = null;
            Assert.True(RuntimeUiDataFolder.TryOpen(root, path => opened = path, out var message));
            Assert.Equal(folder, opened); Assert.Equal(folder, message);
            Assert.False(RuntimeUiDataFolder.TryOpen(root, _ => throw new System.ComponentModel.Win32Exception("shell failed"), out var failure));
            Assert.Contains("shell failed", failure);
        }
        finally { Directory.Delete(folder); }
    }

    [Fact] public void FileNameUsesGameNameAndTimestampAndRemovesUnsafeCharacters()
    {
        var time = new DateTimeOffset(2026, 10, 2, 20, 30, 0, TimeSpan.FromHours(3));
        Assert.Equal("GameLocalizer_RuntimeUI_AI-Shoujo_20261002_203000_000.json", RuntimeUiExport.SuggestedFileName("AI-Shoujo", time));
        var name = RuntimeUiExport.SuggestedFileName("Game/Name:日本語", time);
        Assert.DoesNotContain("/", name); Assert.DoesNotContain(":", name); Assert.Contains("日本語", name);
    }

    public void Dispose() => Directory.Delete(root, true);
}

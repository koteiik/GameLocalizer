using System.Text;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.TranslationProviders;
using Microsoft.Extensions.Logging.Abstractions;

internal static class KeyValueSmoke
{
    public static async Task RunAsync(string root, TranslationService translator, IsolatedTranslationRuntime runtime)
    {
        var gameRoot = Path.Combine(root, "SyntheticGames", "key-value-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(gameRoot, "BepInEx", "Translation", "en", "Text", "dialogue.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string source = "// Synthetic smoke\r\nありがとう = Thank you.\r\nNoName=No Name\r\nFK関節=FK Joint\r\nBGM=Game BGM";
        await File.WriteAllTextAsync(path, source, new UTF8Encoding(true)); var original = await File.ReadAllBytesAsync(path);
        ILocalizationAdapter[] adapters = [new BepInExLocalizationAdapter(), new PlainTextLocalizationAdapter()];
        using var repository = new ScanResultRepository(Path.Combine(gameRoot, "scan.db"));
        var backup = new BackupService(NullLogger<BackupService>.Instance);
        var workspace = new ScanWorkspaceService(new(new(adapters), adapters, NullLogger<ScanPipeline>.Instance), repository, translator, backup, adapters);
        var game = new Game(Guid.NewGuid().ToString("N"), "Synthetic key-value", gameRoot, "Manual");
        await workspace.ScanAsync(game, "scan", null, default);
        if ((await workspace.GetTestRowsAsync("scan", default)).Count != 4) throw new Exception("Selection failed");
        var job = await workspace.RunTranslationJobAsync(game, "scan", false, true, null, default);
        if (job.Status != TranslationJobStatus.Completed || !(await File.ReadAllBytesAsync(path)).SequenceEqual(original)) throw new Exception("Test 20 failed");
        foreach (var row in await workspace.GetTestRowsAsync("scan", default))
        {
            Console.WriteLine($"{row.DisplayKey} | {row.Original} | {row.Translation}");
            if (!row.Translation.Any(c => c >= 'А' && c <= 'я')) throw new Exception("No Russian output");
        }
        var calls = runtime.InferenceCount;
        if (calls == 0 || runtime.IsLoaded) throw new Exception("Real model lifecycle failed");
        await workspace.ApplyAsync(game, "scan", default);
        var output = await File.ReadAllTextAsync(path);
        var adapter = new BepInExLocalizationAdapter();
        if (!adapter.Extract(source).Select(e => e.Key).SequenceEqual(adapter.Extract(output).Select(e => e.Key))) throw new Exception("Key changed");
        await backup.RestoreAsync(gameRoot, default);
        if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(original)) throw new Exception("Restore failed");
        await workspace.ScanAsync(game, "rescan", null, default);
        await workspace.RunTranslationJobAsync(game, "rescan", false, true, null, default);
        if (runtime.InferenceCount != calls || runtime.IsLoaded) throw new Exception("Cache loaded/invoked model");
        Console.WriteLine($"PASS: real offline key-value Test20 / apply / byte-exact restore / cache; model calls={calls}, device={runtime.Device}");
    }
}

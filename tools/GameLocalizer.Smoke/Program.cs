using System.Net;
using System.Text.Json;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.Models;
using GameLocalizer.Infrastructure.Hardware;
using GameLocalizer.Infrastructure.TranslationProviders;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;

var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/offline-smoke");
Directory.CreateDirectory(root);
var models = new TranslationModelManager(Path.Combine(root, "Models"));
Console.WriteLine($"Model: {models.GetModelInfo().Name}; bytes={models.GetModelInfo().DownloadBytes}");
if (!models.IsInstalled) await models.DownloadModelAsync(new Progress<ModelDownloadProgress>(p => Console.WriteLine($"Download {p.File}: {p.DownloadedBytes}/{p.TotalBytes}")), default);
Console.WriteLine("Verified: " + await models.VerifyModelAsync(default));
using var blockNetwork = new BlockNetwork(); using var offlineClient = new HttpClient(blockNetwork);
var offlineModels = new TranslationModelManager(Path.Combine(root, "Models"), offlineClient);
var hostOption = Array.IndexOf(args, "--host");
using var runtime = new IsolatedTranslationRuntime(hostOption >= 0 ? Path.GetFullPath(args[hostOption + 1]) : null);
using var provider = new LocalOfflineTranslationProvider(offlineModels, runtime, new HardwareDetectionService(), new OfflineSettings { Device = args.Contains("--gpu") ? TranslationDevice.GPU : TranslationDevice.CPU, BatchSize = 4 });
var memoryPath = Path.Combine(root, "memory.db");
var service = new TranslationService(provider, new TranslationMemoryService(memoryPath), NullLogger<TranslationService>.Instance);
var replay = args.Contains("--replay"); var identityPath = Path.Combine(root, "last-smoke-game.txt");
var identity = replay ? File.ReadAllText(identityPath) : "offline-smoke-" + Guid.NewGuid().ToString("N");
if (!replay) File.WriteAllText(identityPath, identity);
var game = new Game(identity, "Synthetic smoke", root, "Manual");
var examples = new[] { "Hello", "Continue", "Settings", "New Game", "I don't think we should go there.", "Hello {player}", "<b>New Game</b>" };
var items = examples.Select((text, i) => new TranslationItem(i.ToString(), text, "")).ToArray();
var result = await service.TranslateAsync(game, "synthetic.json", items, default);
foreach (var item in items)
{
    Console.WriteLine(item.Text + " => " + result[item.Id]);
    if (!result[item.Id].Any(c => c >= 'А' && c <= 'я') || !new TranslationValidator().Validate(item.Text, result[item.Id], out _)) throw new Exception("Translation/placeholder smoke failed");
}
var calls = runtime.InferenceCount;
if (runtime.IsLoaded || runtime.ProcessId != null || runtime.MemoryBytes != 0) throw new Exception("Unload failed");
if (replay && (runtime.LoadCount != 0 || calls != 0)) throw new Exception("Restart invoked model");
if (!replay && calls == 0) throw new Exception("Real runtime not exercised");
var restarted = new TranslationService(provider, new TranslationMemoryService(memoryPath), NullLogger<TranslationService>.Instance);
await restarted.TranslateAsync(game, "synthetic.json", items, default);
if (runtime.InferenceCount != calls || runtime.IsLoaded) throw new Exception("Cache reuse loaded/invoked model");
if (blockNetwork.Calls != 0) throw new Exception("Offline network call");
if (!replay)
{
    var gameRoot = Path.Combine(root, "SyntheticGames", identity); Directory.CreateDirectory(gameRoot);
    var path = Path.Combine(gameRoot, "menu.json"); var source = JsonSerializer.Serialize(items.ToDictionary(i => i.Id, i => i.Text)); File.WriteAllText(path, source);
    var original = await File.ReadAllBytesAsync(path); var adapter = new JsonLocalizationAdapter();
    var entries = adapter.Extract(source); var translations = entries.ToDictionary(e => e.Key, e => result[items.Single(i => i.Text == e.Text).Id]);
    var output = adapter.ApplyTranslations(source, translations);
    if (!adapter.Validate(source, output, translations)) throw new Exception("Resource validation failed");
    var backup = new BackupService(NullLogger<BackupService>.Instance);
    await backup.ApplyAsync(gameRoot, [new("menu.json", TextFiles.Hash(original), System.Text.Encoding.UTF8.GetBytes(output))], default);
    for (var launch = 0; launch < 2; launch++)
    {
        using var resources = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        if (resources.RootElement.GetProperty("3").GetString() != result["3"] || runtime.InferenceCount != calls || runtime.IsLoaded) throw new Exception("Saved resource read failed");
    }
    await backup.RestoreAsync(gameRoot, default); if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(original)) throw new Exception("Restore failed");
    Console.WriteLine("Apply / two independent resource reads / restore: PASS");
}
Console.WriteLine($"PASS: device={runtime.Device}, loads={runtime.LoadCount}, runtime calls={calls}, cached repeat calls=0, loaded={runtime.IsLoaded}, network calls={blockNetwork.Calls}, replay={replay}");

sealed class BlockNetwork : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; throw new InvalidOperationException("All HTTP blocked during offline smoke"); }
}

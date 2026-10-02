using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.TranslationProviders;
using GameLocalizer.Infrastructure.Models;
using GameLocalizer.Infrastructure.Hardware;
using GameLocalizer.Infrastructure.GameDiscovery;
using GameLocalizer.Infrastructure.Update;
using Microsoft.Extensions.Logging.Abstractions;
var installed = @"E:\ProjectAI\GameLocalizer";
AssemblyLoadContext.Default.Resolving += (_, name) => { var path = Path.Combine(installed, name.Name + ".dll"); return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null; };
var root = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
ILocalizationAdapter[] adapters = [new BepInExLocalizationAdapter(), new JsonLocalizationAdapter(), new IniLocalizationAdapter(), new PlainTextLocalizationAdapter(), new CsvLocalizationAdapter(), new CsvLocalizationAdapter('\t'), new XmlLocalizationAdapter(), new PoLocalizationAdapter()];
Console.WriteLine("Diagnostic CLI; installed assembly SHA256: " + TextFiles.Hash(File.ReadAllBytes(typeof(UiResourceDiscovery).Assembly.Location)));
if(TextFiles.Hash(File.ReadAllBytes(typeof(UiResourceDiscovery).Assembly.Location)) != TextFiles.Hash(File.ReadAllBytes(Path.Combine(installed,"GameLocalizer.Infrastructure.dll")))) throw new Exception("Diagnostic assembly differs from installed binary; rebuild probe after installation");
if(args.Contains("--cache"))
{
 var data=ApplicationPaths.UserData;var settings=new SettingsService(data).Load();
 var steam=await new SteamDiscoveryService(NullLogger<SteamDiscoveryService>.Instance).DiscoverAsync(default);
 var game=steam.FirstOrDefault(g=>Path.GetFullPath(g.Path).Equals(root,StringComparison.OrdinalIgnoreCase)) ?? settings.ManualGames.FirstOrDefault(g=>Path.GetFullPath(g.Path).Equals(root,StringComparison.OrdinalIgnoreCase)) ?? new Game("manual:"+TranslationMemoryService.Hash(root.ToUpperInvariant()),Path.GetFileName(root),root,"Manual");
 using var offline=new LocalOfflineTranslationProvider(new TranslationModelManager(Path.Combine(data,"Models")),new IsolatedTranslationRuntime(Path.Combine(installed,"GameLocalizer.ModelHost.exe")),new HardwareDetectionService(),settings.Offline);
 var provider=new ConfiguredTranslationProvider(settings,offline,new MockTranslationProvider());
 var memory=new TranslationMemoryService(Path.Combine(data,"memory.db"));
 ScanWorkspaceService Workspace(ScanResultRepository repository)=>new(new(new(adapters),adapters,NullLogger<ScanPipeline>.Instance),repository,new(provider,memory,NullLogger<TranslationService>.Instance,new GlossaryService(Path.Combine(data,"glossary.json"))),new(NullLogger<BackupService>.Instance,Path.Combine(data,"games")),adapters);
 var database=Path.Combine(data,"analysis.db");string session=Guid.NewGuid().ToString("N");long firstRows, firstTranslations;int firstRescanned;
 using(var repository=new ScanResultRepository(database,true))
 {
  var ws=Workspace(repository);var detected=new EngineDetector().Detect(root,default);var result=await ws.RefreshAnalysisAsync(game,session,detected.EngineType.ToString(),detected.Confidence,false,null,default);
  firstRows=result.Candidates;firstRescanned=ws.LastFilesRescanned;firstTranslations=(await repository.LoadSnapshotAsync(root,default))!.Translated;
  Console.WriteLine($"Snapshot saved: {firstRows} rows; translated: {firstTranslations}; reread files: {firstRescanned}");
 }
 using(var repository=new ScanResultRepository(database,true))
 {
  var ws=Workspace(repository);var watch=System.Diagnostics.Stopwatch.StartNew();var snapshot=(await repository.LoadSnapshotAsync(root,default))!;var changes=await ws.ValidateAnalysisAsync(snapshot,default);var page=await repository.QueryAsync(snapshot.Session,new(),0,default);watch.Stop();var restoreMs=watch.ElapsedMilliseconds;
  var refreshSession=Guid.NewGuid().ToString("N");await ws.RefreshAnalysisAsync(game,refreshSession,snapshot.Engine,snapshot.EngineConfidence,false,null,default);
  var proof=new{InstalledExecutable=Path.Combine(installed,"GameLocalizer.exe"),InstalledInfrastructureHash=TextFiles.Hash(File.ReadAllBytes(typeof(UiResourceDiscovery).Assembly.Location)),Database=database,GameId=game.Id,Root=root,FirstRows=firstRows,RestoredRows=page.Total,FirstTranslated=firstTranslations,RestoredTranslated=snapshot.Translated,Changes=changes,RestoreMilliseconds=restoreMs,UnchangedFilesRescanned=ws.LastFilesRescanned,UnchangedFilesReused=ws.LastFilesReused,MainRowsPreserved=page.Total==firstRows,MemoryPreserved=File.Exists(Path.Combine(data,"memory.db"))};
  await File.WriteAllTextAsync(Path.Combine(output,"installed-analysis-cache.json"),JsonSerializer.Serialize(proof,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(proof));
  if(page.Total!=firstRows || firstRows==0 || changes!=0 || ws.LastFilesRescanned!=0)throw new Exception("Cache verification failed");
 }
 return;
}
if (args.Contains("--reproduce"))
{
 var method = typeof(UiResourceDiscovery).GetMethod("StringsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
 foreach(var file in SafeTree.Enumerate(root, default).Where(p => File.Exists(p) && (Path.GetExtension(p).ToLowerInvariant() is ".assets" or ".bundle" || p.Replace('\\','/').Split('/').Any(p => p.Equals("AssetBundle",StringComparison.OrdinalIgnoreCase) || p.Equals("AssetBundles",StringComparison.OrdinalIgnoreCase)))))
 {
  Console.WriteLine("Probe: " + file);
  try { Func<string,bool> accept = text => ShortUiClassifier.IsShortNatural(text) && ShortUiClassifier.HasVocabulary(text) && new TextCandidateDetector().Score(text,source:ResourceKind.UIResource)>=.85; await (Task)method.Invoke(null,[file,CancellationToken.None,accept])!; }
  catch(Exception e)
  {
   var detail = new { File=file, Stage="Unity read-only discovery / UiResourceDiscovery.StringsAsync", Encoding="UTF-8 / codepage 65001", API="Decoder.GetChars", InputBufferBytes=65536, OutputBufferChars=65536, Exception=e.ToString(), StackTrace=e.StackTrace };
   await File.WriteAllTextAsync(Path.Combine(output,"legacy-decoder-fault.json"),JsonSerializer.Serialize(detail,new JsonSerializerOptions{WriteIndented=true})); Console.WriteLine(e); return;
  }
 }
 Console.WriteLine("No reproduced fault"); return;
}
long rows=0, bep=0; var pipeline=new ScanPipeline(new(adapters),adapters,NullLogger<ScanPipeline>.Instance);
await foreach(var batch in pipeline.ScanAsync(root,default)) { rows+=batch.Entries.Count; if(batch.Resource.Format.StartsWith("BepInEx")) bep+=batch.Entries.Count; }
Console.WriteLine($"Supported text rows: {rows}; BepInEx rows: {bep}");
var discovery = new UiResourceDiscovery(adapters); var found=await discovery.DiscoverAsync(root,default);
Console.WriteLine("Unsupported Unity candidates: " + found.Count);
Console.WriteLine($"Unity resources examined: {discovery.Statistics.ResourcesExamined}; unsupported containers: {discovery.Statistics.UnsupportedContainers}; discovery errors: {discovery.Statistics.Errors.Count}; supported scan errors: {pipeline.ErrorCount}");
await File.WriteAllTextAsync(Path.Combine(output,"installed-scan.json"),JsonSerializer.Serialize(new { InstalledInfrastructureHash=TextFiles.Hash(File.ReadAllBytes(typeof(UiResourceDiscovery).Assembly.Location)), Root=root, SupportedTextRows=rows,BepInExRows=bep,UnsupportedCandidates=found.Count,Statistics=discovery.Statistics,SupportedScanErrors=pipeline.ErrorCount },new JsonSerializerOptions{WriteIndented=true}));
if(bep==0) throw new Exception("BepInEx rows are zero");
if(discovery.Statistics.Errors.Count != 0) throw new Exception("Discovery errors found; inspect report");

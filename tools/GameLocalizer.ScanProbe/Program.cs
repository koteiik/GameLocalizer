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
if(args.Contains("--verify-dialogue-dictionary"))
{
 var dialoguePath=Path.Combine(root,GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.PluginDirectory,"runtime-dictionary.json");
 var dialogueLookup=GameLocalizer.RuntimeCollector.RuntimeDictionaryLookup.Load(dialoguePath,GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.GameId(root),out var dialogueError);if(dialogueError!=null)throw new InvalidDataException(dialogueError);
 var dialogueChecks=new[]{"What do you think of me?","How are you?","Who are you friends with?","Hmm... Not bad.","Chat","Give Advice","Give Item","Options","Save","Map"}.Select(text=>{var found=dialogueLookup.TryTranslate(text,out var russian);return new{Original=text,Lookup=found?"FOUND":"MISSING",Russian=russian};}).ToArray();
 if(dialogueChecks.Any(c=>c.Lookup!="FOUND"))throw new InvalidDataException("Dialogue/menu dictionary key missing");
 File.WriteAllText(Path.Combine(output,"installed-dialogue-lookup.json"),JsonSerializer.Serialize(dialogueChecks,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));Console.WriteLine("Installed dialogue and menu exact lookup: PASS; gameplay not started.");return;
}
if(args.Contains("--review-runtime-quality"))
{
 var glossary=new GameLocalizer.Infrastructure.Runtime.RuntimeUiGlossary();
 var qualityRows=GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.ImportDirectory(GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.DataDirectory(root));
 var controls=new[]{"Loader","Options","Save","Map","Status","Help","Camera","Journal","Chat","Give Advice","Give Item","Game Start","Quit"};
 var review=controls.Select(text=>new{Original=text,Captured=qualityRows.Any(r=>r.Text==text),Cases=qualityRows.Where(r=>r.Text==text).Select(r=>new{r.Scene,r.Hierarchy,r.Context,r.ContextConfidence,r.SeenCount,Glossary=glossary.Match(r)}).ToArray(),Fallback=glossary.Match(new(){Text=text})}).ToArray();
 var entries=Enumerable.Range(0,10000).Select(i=>new GameLocalizer.RuntimeCollector.RuntimeDictionaryEntry{OriginalText="Key "+i,RussianText="Строка "+i}).ToArray();
 var lookup=GameLocalizer.RuntimeCollector.RuntimeDictionaryLookup.FromEntries(entries);var random=new Random(42);var keys=Enumerable.Range(0,100000).Select(_=>entries[random.Next(entries.Length)].OriginalText).ToArray();foreach(var key in keys.Take(10000))lookup.TryTranslate(key,out _);
 var watch=new System.Diagnostics.Stopwatch();var before=GC.GetAllocatedBytesForCurrentThread();watch.Start();foreach(var key in keys)if(!lookup.TryTranslate(key,out _))throw new Exception("Lookup missing");watch.Stop();var allocation=GC.GetAllocatedBytesForCurrentThread()-before;
 var result=new{ImportedRows=qualityRows.Count,Review=review,LoaderSaveLoad=glossary.Match(new(){Text="Loader",Scene="Title_Load"}),BenchmarkEntries=entries.Length,Lookups=keys.Length,AverageLookupNanoseconds=watch.Elapsed.TotalNanoseconds/keys.Length,AllocatedLookupBytes=allocation,RuntimeNetwork="NONE (dependency tests)",RuntimeInference="NONE (dependency tests)",DictionaryChanged=false,GameplayStarted=false};
 File.WriteAllText(Path.Combine(output,"runtime-quality-review.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions{IncludeFields=true}));return;
}
if(args.Contains("--reprocess-runtime"))
{
 if(!args.Contains("--accept-local-runtime"))throw new InvalidOperationException("Explicit --accept-local-runtime required.");
 var reprocessData=ApplicationPaths.UserData;var reprocessSettings=new SettingsService(reprocessData).Load();
 var reprocessGames=await new SteamDiscoveryService(NullLogger<SteamDiscoveryService>.Instance).DiscoverAsync(default);
 var reprocessGame=reprocessGames.FirstOrDefault(g=>Path.GetFullPath(g.Path).Equals(root,StringComparison.OrdinalIgnoreCase))??reprocessSettings.ManualGames.FirstOrDefault(g=>Path.GetFullPath(g.Path).Equals(root,StringComparison.OrdinalIgnoreCase))??new Game("manual:"+TranslationMemoryService.Hash(root.ToUpperInvariant()),Path.GetFileName(root),root,"Manual");
 using var reprocessOffline=new LocalOfflineTranslationProvider(new TranslationModelManager(Path.Combine(reprocessData,"Models")),new IsolatedTranslationRuntime(Path.Combine(installed,"GameLocalizer.ModelHost.exe")),new HardwareDetectionService(),reprocessSettings.Offline);
 var reprocessMemory=new TranslationMemoryService(Path.Combine(reprocessData,"memory.db"));var reprocessTranslator=new TranslationService(reprocessOffline,reprocessMemory,NullLogger<TranslationService>.Instance,new GlossaryService(Path.Combine(reprocessData,"glossary.json")));
 var reprocessService=new GameLocalizer.Infrastructure.Runtime.RuntimeDictionaryService(reprocessTranslator,reprocessMemory);
 var reprocessRows=reprocessService.LoadExisting(root);var reprocessCapture=GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.ImportDirectory(GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.DataDirectory(root));
 var reprocessJson=new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
 // The actual problematic title label is Uploader. Correct its known bad automatic value using the existing user glossary.
 if(reprocessRows.Any(r=>r.Text=="Uploader"&&r.Russian=="Погрузчик"&&r.Context=="MainMenu"&&r.TranslationSource is not ("Manual" or "UserGlossary")))
 {
  var terms=reprocessService.Glossary.Load().ToList();
  if(!terms.Any(t=>t.Original.Equals("Uploader",StringComparison.OrdinalIgnoreCase)))
  {
   if(File.Exists(reprocessService.Glossary.Path))File.Copy(reprocessService.Glossary.Path,Path.Combine(output,"user-glossary-before.json"),true);
   terms.Add(new("Uploader","Загрузка","MainMenu"));reprocessService.Glossary.Save(terms);
  }
 }
 Console.WriteLine($"Existing entries: {reprocessRows.Count}. Processing existing automatic translations (offline only).");
 var reprocessPreview=await reprocessService.PreviewReprocessAsync(reprocessGame,reprocessRows,default);
 File.WriteAllText(Path.Combine(output,"reprocess-preview.json"),JsonSerializer.Serialize(reprocessPreview.Changes.Select(c=>new{Original=c.Row.Text,c.OldRussian,c.NewRussian,c.Context,c.OldSource,c.NewSource,c.Row.Scene,c.Row.Hierarchy,c.Row.Component}),reprocessJson));
 Console.WriteLine($"Preview saved: {reprocessPreview.Changes.Count} changes. Applying explicitly requested migration.");
 await reprocessService.ApplyReprocessAsync(reprocessGame,reprocessPreview,default);
 var reprocessDeployment=await reprocessService.RebuildAndDeployAsync(reprocessGame,reprocessRows,default);
 var reprocessDisk=JsonSerializer.Deserialize<GameLocalizer.Infrastructure.Runtime.RuntimeDictionaryFile>(File.ReadAllText(reprocessDeployment.Installed))!;
 var reprocessLookup=GameLocalizer.RuntimeCollector.RuntimeDictionaryLookup.Load(reprocessDeployment.Installed,GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.GameId(root),out var reprocessError);if(reprocessError!=null)throw new InvalidDataException(reprocessError);
 if(reprocessDisk.Entries.Any(e=>e.OriginalText is "Loader" or "Uploader"&&e.RussianText=="Погрузчик"))throw new InvalidDataException("Bad Loader/Uploader remains installed");
 bool Ready(GameLocalizer.Infrastructure.Runtime.RuntimeUiEntry r)=>r.Russian!=r.Text&&r.Russian.Any(c=>c is >= '\u0400' and <= '\u04ff')&&new GameLocalizer.Core.Validation.TranslationValidator().Validate(r.Text,r.Russian,out _);
 bool Included(GameLocalizer.Infrastructure.Runtime.RuntimeUiEntry r)=>reprocessLookup.TryTranslate(r.Text,out var value)&&value==r.Russian;
 var reprocessHelp=reprocessCapture.Where(r=>r.Category=="Runtime Help / Tutorial").Select(r=>reprocessRows.First(p=>p.Identity==r.Identity)).ToArray();
 var reprocessDialogue=new[]{"What do you think of me?","How are you?","Who are you friends with?","Hmm... Not bad."}.Select(text=>new{Original=text,Captured=reprocessCapture.Any(r=>r.Text==text),Translated=reprocessRows.Any(r=>r.Text==text&&Ready(r)),InDictionary=reprocessDisk.Entries.Any(e=>e.OriginalText==text),Components=reprocessCapture.Where(r=>r.Text==text).Select(r=>r.Component).Distinct().ToArray()}).ToArray();
 var reprocessProof=new{GameId=reprocessGame.Id,ExistingEntriesReprocessed=reprocessRows.Count,ChangedTranslations=reprocessPreview.Changes.Count,ChangedRussian=reprocessPreview.Changes.Count(c=>c.OldRussian!=c.NewRussian),reprocessPreview.ManualPreserved,reprocessPreview.UserGlossaryPreserved,Deployment=reprocessDeployment,HashesMatch=reprocessDeployment.MasterSha256==reprocessDeployment.InstalledSha256,LoaderCaptured=reprocessCapture.Any(r=>r.Text=="Loader"),LoaderInstalled=reprocessDisk.Entries.Where(e=>e.OriginalText=="Loader").ToArray(),ActualProblem=reprocessRows.Where(r=>r.Text=="Uploader").Select(r=>new{Original=r.Text,r.Scene,r.Context,r.Hierarchy,r.Component,OldRussian=reprocessPreview.Changes.FirstOrDefault(c=>c.Row.Identity==r.Identity)?.OldRussian,OldSource=reprocessPreview.Changes.FirstOrDefault(c=>c.Row.Identity==r.Identity)?.OldSource,NewRussian=r.Russian,NewSource=r.TranslationSource}).ToArray(),HelpCaptured=reprocessHelp.Length,HelpTranslated=reprocessHelp.Count(Ready),HelpInDictionary=reprocessHelp.Count(Included),HelpStillUntranslated=reprocessHelp.Count(r=>!Ready(r)),HelpNotInDictionary=reprocessHelp.Count(r=>!Included(r)),Dialogue=reprocessDialogue,GameplayStarted=false};
 File.WriteAllText(Path.Combine(output,"reprocess-verification.json"),JsonSerializer.Serialize(reprocessProof,reprocessJson));Console.WriteLine(JsonSerializer.Serialize(reprocessProof,reprocessJson));return;
}
if(args.Contains("--install-runtime-package"))
{
 if(!args.Contains("--accept-local-runtime"))throw new InvalidOperationException("Explicit --accept-local-runtime required.");
 var master=Path.Combine(ApplicationPaths.UserData,"RuntimeDictionary",GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.GameId(root),"runtime-dictionary.json");
 var lookup=GameLocalizer.RuntimeCollector.RuntimeDictionaryLookup.Load(master,GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.GameId(root),out var error);if(error!=null)throw new InvalidDataException(error);
 var installer=new GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService();var plugin=installer.Install(root,Path.Combine(installed,"RuntimeCollector","GameLocalizer.RuntimeCollector.dll"));var copy=installer.InstallDictionary(root,master);
 var proof=new{Plugin=plugin,DictionaryCopy=copy,DictionaryMaster=master,Entries=lookup.Count,GameplayStarted=false,PretranslationStarted=false};File.WriteAllText(Path.Combine(output,"runtime-package-install.json"),JsonSerializer.Serialize(proof,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(proof));return;
}
if(args.Contains("--prepare-runtime-dictionary"))
{
 if(!args.Contains("--accept-local-runtime"))throw new InvalidOperationException("Explicit --accept-local-runtime required: prepared dictionary will replace known UI text locally.");
 var data=ApplicationPaths.UserData;var settings=new SettingsService(data).Load();
 var steam=await new SteamDiscoveryService(NullLogger<SteamDiscoveryService>.Instance).DiscoverAsync(default);
 var game=steam.FirstOrDefault(g=>Path.GetFullPath(g.Path).Equals(root,StringComparison.OrdinalIgnoreCase))??settings.ManualGames.FirstOrDefault(g=>Path.GetFullPath(g.Path).Equals(root,StringComparison.OrdinalIgnoreCase))??new Game("manual:"+TranslationMemoryService.Hash(root.ToUpperInvariant()),Path.GetFileName(root),root,"Manual");
 using var offline=new LocalOfflineTranslationProvider(new TranslationModelManager(Path.Combine(data,"Models")),new IsolatedTranslationRuntime(Path.Combine(installed,"GameLocalizer.ModelHost.exe")),new HardwareDetectionService(),settings.Offline);
 var memory=new TranslationMemoryService(Path.Combine(data,"memory.db"));var translation=new TranslationService(offline,memory,NullLogger<TranslationService>.Instance,new GlossaryService(Path.Combine(data,"glossary.json")));
 var service=new GameLocalizer.Infrastructure.Runtime.RuntimeDictionaryService(translation,memory);
 var runtimeRows=GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.ImportDirectory(GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.DataDirectory(root)).ToList();
 await service.HydrateAsync(game,runtimeRows,default);Console.WriteLine($"Runtime rows: {runtimeRows.Count}; ready from persisted state/memory: {runtimeRows.Count(r=>r.Russian.Length>0)}");
 // Include the already authored localization template, only when it is an existing supported analysis source.
 using(var repo=new ScanResultRepository(Path.Combine(data,"analysis.db"),true))
 {
  var snapshot=await repo.LoadSnapshotAsync(root,default);
  if(snapshot!=null)
  {
   var page=await repo.QueryAsync(snapshot.Session,new ScanQuery(Search:"Talk with {{A}}"),0,default);
   foreach(var r in page.Rows.Where(r=>r.Original=="Talk with {{A}}"))
   {
    var row=new GameLocalizer.Infrastructure.Runtime.RuntimeUiEntry{Text=r.Original,Hierarchy="ExplicitSourcePattern",Source=r.FilePath,Component="ExplicitPlaceholderTemplate"};
    if(new GameLocalizer.Core.Validation.TranslationValidator().Validate(r.Original,r.Translation,out _))row.SetTranslation(r.Translation,r.ManualEdit?"Manual":"Imported",DateTimeOffset.UtcNow);
    runtimeRows.Add(row);
   }
  }
 }
 var translated=0;var pending=runtimeRows.Where(r=>string.IsNullOrWhiteSpace(r.Russian)&&!r.DictionaryConflict).Select(r=>r.Text).Distinct(StringComparer.Ordinal).ToArray();
 // Progress remains observable and completed translations are durable between bounded batches.
 foreach(var texts in pending.Chunk(16))
 {
  var batch=runtimeRows.Where(r=>texts.Contains(r.Text,StringComparer.Ordinal)).ToArray();translated+=await service.TranslateNewAsync(game,batch,default);
  Console.WriteLine($"Pretranslated: {translated}/{pending.Length}");
 }
 var build=await service.GenerateAsync(game,runtimeRows,default);Console.WriteLine($"Master dictionary: {build.Dictionary.EntryCount}; conflicts: {build.Conflicts.Count}");
 var installer=new GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService();var plugin=installer.Install(root,Path.Combine(installed,"RuntimeCollector","GameLocalizer.RuntimeCollector.dll"));
 var copy=installer.InstallDictionary(root,service.DictionaryPath(root));
 var lookup=GameLocalizer.RuntimeCollector.RuntimeDictionaryLookup.Load(copy,GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.GameId(root),out var loadError);
 var controls=new Dictionary<string,object>();foreach(var text in new[]{"Chat","Give Advice","Give Item","Talk with {{A}}","Talk with Tsubomi"}){var dictionaryFound=lookup.TryTranslate(text,out var value);controls[text]=new{Found=dictionaryFound,Russian=value};}
 if(loadError!=null)throw new InvalidDataException(loadError);
 if(new[]{"Chat","Give Advice","Give Item"}.Any(text=>!lookup.TryTranslate(text,out _)))throw new InvalidDataException("Required control strings absent from generated dictionary; inspect captures/translations.");
 var benchmarkEntries=Enumerable.Range(0,10000).Select(i=>new GameLocalizer.RuntimeCollector.RuntimeDictionaryEntry{OriginalText="Key "+i,RussianText="Строка "+i}).ToArray();GC.Collect();var before=GC.GetTotalMemory(true);var benchmark=GameLocalizer.RuntimeCollector.RuntimeDictionaryLookup.FromEntries(benchmarkEntries);var memoryEstimate=GC.GetTotalMemory(true)-before;
 var random=new Random(42);var keys=Enumerable.Range(0,100000).Select(_=>benchmarkEntries[random.Next(10000)].OriginalText).ToArray();foreach(var key in keys.Take(10000))benchmark.TryTranslate(key,out _);
 var watch=new System.Diagnostics.Stopwatch();var allocated=GC.GetAllocatedBytesForCurrentThread();watch.Start();foreach(var key in keys)benchmark.TryTranslate(key,out _);watch.Stop();allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
 var timing=new double[10000];for(var i=0;i<timing.Length;i++){var t=System.Diagnostics.Stopwatch.GetTimestamp();benchmark.TryTranslate(keys[i],out _);timing[i]=(System.Diagnostics.Stopwatch.GetTimestamp()-t)*1e9/System.Diagnostics.Stopwatch.Frequency;}Array.Sort(timing);
 GC.KeepAlive(benchmark);offline.EndJob();
 var proof=new{RuntimeTranslationDictionary="PASS (synthetic; real gameplay pending)",ImportedRows=runtimeRows.Count,Pretranslated=translated,DictionaryEntries=build.Dictionary.EntryCount,Conflicts=build.Conflicts.Count,DictionaryMaster=service.DictionaryPath(root),DictionaryCopy=copy,Plugin=plugin,Controls=controls,BenchmarkEntries=10000,RandomLookups=100000,AverageLookupNanoseconds=watch.Elapsed.TotalNanoseconds/keys.Length,P95LookupNanoseconds=timing[9499],AllocatedLookupBytes=allocated,LookupMapMemoryEstimateBytes=memoryEstimate,ModelHostDuringGame="NOT USED (no plugin dependency; manual gameplay pending)",RuntimeModelInference="NONE",NetworkUsage="NONE (static dependency verification)",GameplayStarted=false};
 File.WriteAllText(Path.Combine(output,"runtime-dictionary-verification.json"),JsonSerializer.Serialize(proof,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));Console.WriteLine(JsonSerializer.Serialize(proof));return;
}
if(args.Contains("--install-runtime-collector"))
{
 if(!args.Contains("--accept-read-only")) throw new InvalidOperationException("Explicit --accept-read-only required. Runtime UI Collector temporarily adds a diagnostic BepInEx plugin; it only observes UI text and can be removed.");
 Console.WriteLine("Runtime UI Collector временно добавит диагностический BepInEx-плагин в папку игры. Он только собирает отображаемый UI-текст и не изменяет его. После сбора плагин можно полностью удалить.");
 var service=new GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService();
 var plugin=service.Install(root,Path.Combine(installed,"RuntimeCollector","GameLocalizer.RuntimeCollector.dll"));
 var proof=new {Target=root,Plugin=plugin,PluginSHA256=TextFiles.Hash(File.ReadAllBytes(plugin)),DataDirectory=GameLocalizer.Infrastructure.Runtime.RuntimeCollectorService.DataDirectory(root),Status=service.Status(root),GameplayStarted=false};
 File.WriteAllText(Path.Combine(output,"collector-install-verification.json"),JsonSerializer.Serialize(proof,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(proof));return;
}
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

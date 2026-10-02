using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using System.IO;
using System.Reflection;
using System.Text.Json;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.RuntimeCollector;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Tests;
[Collection("Runtime plugin")]
public sealed class RuntimeCollectorTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"GLCollector-"+Guid.NewGuid().ToString("N"));
    public RuntimeCollectorTests(){Directory.CreateDirectory(root);}
    private string MakeGame()
    {
        var game=Path.Combine(root,"game");Directory.CreateDirectory(Path.Combine(game,"BepInEx/core"));File.WriteAllText(Path.Combine(game,"BepInEx/core/BepInEx.dll"),"synthetic");
        Directory.CreateDirectory(Path.Combine(game,"Game_Data/Managed"));File.WriteAllText(Path.Combine(game,"Game_Data/Managed/UnityEngine.dll"),"synthetic");return game;
    }
    private string Package(){var path=Path.Combine(root,"plugin.dll");File.WriteAllBytes(path,new byte[]{77,90,0,1});return path;}
    [Theory]
    [InlineData("2745fb3d2161624460c143a4",false)]
    [InlineData("GameId=2745fb3d2161624460c143a4",false)]
    [InlineData("# comment\nDialogueTraceEnabled=true\nGameId=2745fb3d2161624460c143a4",true)]
    [InlineData("GameId=2745fb3d2161624460c143a4\nDialogueTraceEnabled=false",false)]
    public void ConfigurationSupportsLegacyAndExplicitSwitch(string input,bool expected)
    {
        var config=CollectorConfiguration.Parse(input.Split('\n'));
        Assert.Equal("2745fb3d2161624460c143a4",config.GameId);Assert.Equal(expected,config.DialogueTraceEnabled);
        Assert.Equal(expected,CollectorConfiguration.Parse(config.Serialize().Split('\n')).DialogueTraceEnabled);
    }
    [Theory][InlineData("")][InlineData("../other")][InlineData("GameId=2745fb3d2161624460c143a4\nDialogueTraceEnabled=yes")]
    public void InvalidConfigurationRejected(string input)=>Assert.Throws<InvalidDataException>(()=>CollectorConfiguration.Parse(input.Split('\n')));
    [Fact] public void TraceSwitchUpdatesOwnedConfigAndSurvivesReinstallWithoutDictionaryChanges()
    {
        var game=MakeGame();var svc=new RuntimeCollectorService();var dll=svc.Install(game,Package());
        Assert.False(svc.ReadDialogueTraceEnabled(game));
        var dictionary=Path.Combine(root,"dictionary.json");File.WriteAllText(dictionary,"preserve dictionary bytes");svc.InstallDictionary(game,dictionary);
        svc.SetDialogueTraceEnabled(game,true);Assert.True(svc.ReadDialogueTraceEnabled(game));svc.Install(game,Package());Assert.True(svc.ReadDialogueTraceEnabled(game));
        Assert.Equal("preserve dictionary bytes",File.ReadAllText(Path.Combine(Path.GetDirectoryName(dll)!,"runtime-dictionary.json")));
        svc.SetDialogueTraceEnabled(game,false);Assert.False(svc.ReadDialogueTraceEnabled(game));
        Assert.Equal("GameId="+RuntimeCollectorService.GameId(game)+"\nDialogueTraceEnabled=false\n",File.ReadAllText(Path.Combine(Path.GetDirectoryName(dll)!,"collector.config")));
        svc.Uninstall(game);Assert.False(File.Exists(dll));
    }
    [Theory][InlineData("Chat")][InlineData("Yes")][InlineData("No")][InlineData("OK")][InlineData("Back")][InlineData("Give Item")][InlineData("Give Advice")][InlineData("Talk with Tsubomi")]
    public void ShortUiPreserved(string text)=>Assert.True(CaptureBuffer.Accept(text));
    [Theory][InlineData("")][InlineData("   ")][InlineData("123")][InlineData("60 FPS")][InlineData("FPS: 59")][InlineData("12:34:56")][InlineData("ObjectID=123")]
    public void NoiseIgnored(string text)=>Assert.False(CaptureBuffer.Accept(text));
    [Fact]public void BufferedDedupeCrashTailAndRepeatedImport()
    {
        var file=Path.Combine(root,"runtime-ui-a.jsonl");var buffer=new CaptureBuffer(file);
        for(var i=0;i<4;i++)buffer.Observe("Chat","Main","Text","Canvas/Text","UnityEngine.UI.Text","UnityEngine.UI");
        Assert.False(File.Exists(file));buffer.Flush();Assert.Single(File.ReadAllLines(file));
        buffer.Observe("Chat","Main","Text","Canvas/Text","UnityEngine.UI.Text","UnityEngine.UI");buffer.Flush();File.AppendAllText(file,"{\"Text\":");
        var row=Assert.Single(RuntimeCollectorService.ImportDirectory(root));Assert.Equal(5,row.SeenCount);Assert.Equal(5,Assert.Single(RuntimeCollectorService.ImportDirectory(root)).SeenCount);
    }
    [Fact]public void WriteFailureRetainsBufferedData()
    {
        var folder=Path.Combine(root,"missing");var buffer=new CaptureBuffer(Path.Combine(folder,"runtime-ui-a.jsonl"));buffer.Observe("OK","S","O","H","T","A");Assert.Throws<DirectoryNotFoundException>(()=>buffer.Flush());Directory.CreateDirectory(folder);buffer.Flush();Assert.Single(RuntimeCollectorService.ImportDirectory(folder));
    }
    [Fact]public void InstallOwnershipAndSafeUninstallPreservesForeignPlugin()
    {
        var game=MakeGame();Directory.CreateDirectory(Path.Combine(game,"BepInEx/plugins"));var foreign=Path.Combine(game,"BepInEx/plugins/Foreign.dll");File.WriteAllText(foreign,"foreign");
        var svc=new RuntimeCollectorService();var dll=svc.Install(game,Package());Assert.True(File.Exists(dll));
        var manifest=JsonSerializer.Deserialize<CollectorOwnership>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(dll)!,"collector-ownership.json")))!;
        Assert.Equal("1.0.0",manifest.InstallVersion);Assert.Equal(4,manifest.CreatedFiles.Count);Assert.All(manifest.CreatedFiles.Values,h=>Assert.Equal(64,h.Length));
        var foreignInside=Path.Combine(Path.GetDirectoryName(dll)!,"foreign.txt");File.WriteAllText(foreignInside,"keep");svc.Uninstall(game);Assert.False(File.Exists(dll));Assert.Equal("foreign",File.ReadAllText(foreign));Assert.True(File.Exists(foreignInside));
    }
    [Fact]public void ChangedOwnedFileBlocksUninstallAndReinstall()
    {
        var game=MakeGame();var svc=new RuntimeCollectorService();var dll=svc.Install(game,Package());File.AppendAllText(dll,"changed");Assert.Throws<IOException>(()=>svc.Uninstall(game));Assert.Throws<IOException>(()=>svc.Install(game,Package()));Assert.True(File.Exists(dll));
    }
    [Fact]public void ForeignCollectorDirectoryIsNeverOverwritten()
    {
        var game=MakeGame();Directory.CreateDirectory(Path.Combine(game,RuntimeCollectorService.PluginDirectory));Assert.Throws<IOException>(()=>new RuntimeCollectorService().Install(game,Package()));
    }
    [Fact]public void Il2CppRejected(){var game=MakeGame();File.WriteAllText(Path.Combine(game,"GameAssembly.dll"),"");Assert.Contains("IL2CPP",RuntimeCollectorService.Compatibility(game));Assert.Throws<NotSupportedException>(()=>new RuntimeCollectorService().Install(game,Package()));}
    [Fact]public async Task MatchingUsesCachedWritableUnsupportedAndRuntimeOnly()
    {
        using var repo=new ScanResultRepository(Path.Combine(root,"analysis.db"));
        await repo.AppendAsync("s","g",new ScanBatch(new Resource("ui.txt","BepInEx",true,""),"hash",[new ScanEntry("ui.txt","chat","Chat","",1,false,TextCategory.ShortUI){AdapterType="BepInEx / XUnity key=value"},new ScanEntry("binary","advice","Give Advice","",1,false,TextCategory.UnsupportedUI)],new(1,2,1,0)),default);
        var rows=new[]{new RuntimeUiEntry{Text="Chat"},new RuntimeUiEntry{Text="Give Advice"},new RuntimeUiEntry{Text="Talk with Tsubomi"}};
        await repo.MatchRuntimeAsync("s",root,rows,default);Assert.Equal("MatchedWritable",rows[0].MatchStatus);Assert.Equal("MatchedUnsupported",rows[1].MatchStatus);Assert.Equal("RuntimeOnly",rows[2].MatchStatus);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ActualObserverReadsUnityAndTmpWithoutChangingText(bool tmp)
    {
        var file=Path.Combine(root,"runtime-ui-hook.jsonl");var plugin=new CollectorPlugin();
        Set("buffer",new CaptureBuffer(file));Set("mainThread",Environment.CurrentManagedThreadId);typeof(CollectorPlugin).GetField("current",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,plugin);
        UnityEngine.Component component=tmp?new TMPro.TextMeshProUGUI{ text="Talk with Tsubomi"}:new UnityEngine.UI.Text{text="Give Advice"};
        var original=(string)component.GetType().GetProperty("text")!.GetValue(component)!;
        if(tmp)CollectorPlugin.ObserveRendered(component);else CollectorPlugin.Observe(component);typeof(CollectorPlugin).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plugin,null);
        Assert.Equal(original,component.GetType().GetProperty("text")!.GetValue(component));Assert.False(File.Exists(file));
        typeof(CollectorPlugin).GetMethod("Flush",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plugin,null);
        Assert.Equal(original,Assert.Single(RuntimeCollectorService.ImportDirectory(root)).Text);
        typeof(CollectorPlugin).GetField("current",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,null);
        void Set(string name,object value)=>typeof(CollectorPlugin).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(plugin,value);
    }
    [Fact] public void SeparateSessionsSumActualTextAndPreserveEscapes()
    {
        var a=new CaptureBuffer(Path.Combine(root,"runtime-ui-a.jsonl"));var b=new CaptureBuffer(Path.Combine(root,"runtime-ui-b.jsonl"));
        var text="Talk with A\n\"B\"\\C";
        a.Observe(text,"S","O","H","T","A");a.Flush();b.Observe(text,"S","O","H","T","A");b.Flush();
        var row=Assert.Single(RuntimeCollectorService.ImportDirectory(root));Assert.Equal(text,row.Text);Assert.Equal(2,row.SeenCount);
    }
    [Fact] public void CollectorFailureIsIsolatedAndInactiveUiIsIgnored()
    {
        var plugin=new CollectorPlugin();var file=Path.Combine(root,"runtime-ui-failure.jsonl");
        typeof(CollectorPlugin).GetField("buffer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(plugin,new CaptureBuffer(file));
        typeof(CollectorPlugin).GetField("mainThread",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(plugin,Environment.CurrentManagedThreadId);
        typeof(CollectorPlugin).GetField("current",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,plugin);
        var inactive=new UnityEngine.UI.Text{text="Chat"};inactive.gameObject.activeInHierarchy=false;
        CollectorPlugin.Observe(inactive);CollectorPlugin.Observe(new ThrowingUi());
        typeof(CollectorPlugin).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plugin,null);
        typeof(CollectorPlugin).GetMethod("Flush",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plugin,null);
        Assert.False(File.Exists(file));typeof(CollectorPlugin).GetField("current",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,null);
    }
    private sealed class ThrowingUi:UnityEngine.Behaviour{public string text=>throw new InvalidOperationException("synthetic getter failure");}
    [Fact] public void BufferIsThreadSafe()
    {
        var buffer=new CaptureBuffer(Path.Combine(root,"runtime-ui-thread.jsonl"));
        Parallel.For(0,1000,_=>buffer.Observe("OK","S","O","H","T","A"));buffer.Flush();Assert.Equal(1000,Assert.Single(RuntimeCollectorService.ImportDirectory(root)).SeenCount);
    }
    [Fact] public void PackagedPluginReferencesExistingLoaderAndContainsNoNetworkDependencies()
    {
        using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"RuntimeCollector","GameLocalizer.RuntimeCollector.dll"));using var pe=new PEReader(stream);var metadata=pe.GetMetadataReader();
        var names=metadata.AssemblyReferences.Select(h=>metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
        Assert.Contains("BepInEx",names);Assert.Contains("0Harmony",names);Assert.Contains("UnityEngine.CoreModule",names);
        Assert.DoesNotContain(names,n=>n.StartsWith("System.Net",StringComparison.Ordinal)||n=="GameLocalizer.Tests"||n=="GameLocalizer.Infrastructure");
        var members=metadata.MemberReferences.Select(h=>metadata.GetString(metadata.GetMemberReference(h).Name)).ToArray();
        Assert.DoesNotContain(members,n=>n=="set_text" || n=="SetText" || n=="DownloadString" || n=="SendAsync");
    }
    [Fact] public void HookDiscoveryCoversTextSetterTmpSetTextAndRendering()
    {
        var plugin=new CollectorPlugin();HarmonyLib.Harmony.Patched.Clear();
        typeof(CollectorPlugin).GetField("harmony",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(plugin,new HarmonyLib.Harmony("test"));
        typeof(CollectorPlugin).GetMethod("PatchAssembly",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plugin,[typeof(TMPro.TMP_Text).Assembly]);
        var methods=HarmonyLib.Harmony.Patched.Select(m=>m.Name).ToArray();Assert.Contains("set_text",methods);Assert.Contains("SetText",methods);Assert.Contains("GenerateTextMesh",methods);Assert.Contains("OnEnable",methods);
        Assert.Equal(HarmonyLib.Harmony.Patched.Count,HarmonyLib.Harmony.Patched.Distinct().Count());
    }
    [Fact] public void FormattedTmpCapturesRenderedValueWithoutChangingFormat()
    {
        var plugin=new CollectorPlugin();var file=Path.Combine(root,"runtime-ui-dynamic.jsonl");
        typeof(CollectorPlugin).GetField("buffer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(plugin,new CaptureBuffer(file));typeof(CollectorPlugin).GetField("mainThread",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(plugin,Environment.CurrentManagedThreadId);typeof(CollectorPlugin).GetField("current",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,plugin);
        var tmp=new TMPro.TextMeshProUGUI{text="Talk with {0}",ParsedText="Talk with Tsubomi"};CollectorPlugin.ObserveRendered(tmp);
        typeof(CollectorPlugin).GetMethod("LateUpdate",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plugin,null);typeof(CollectorPlugin).GetMethod("Flush",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plugin,null);
        Assert.Equal("Talk with {0}",tmp.text);Assert.Equal("Talk with Tsubomi",Assert.Single(RuntimeCollectorService.ImportDirectory(root)).Text);typeof(CollectorPlugin).GetField("current",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,null);
    }
    public void Dispose(){Directory.Delete(root,true);}
}






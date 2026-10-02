using System.Reflection;
using GameLocalizer.RuntimeCollector;
using Xunit;

namespace GameLocalizer.Tests
{
    [Collection("Runtime plugin")]
    public sealed class DialogueRuntimeTests : IDisposable
    {
        private readonly RuntimeDictionaryPlugin plugin=new();
        private readonly List<DialogueTraceEvent> trace=[];
        private readonly UnityEngine.UI.Text label=new();
        public DialogueRuntimeTests()
        {
            label.transform.name="Text";label.transform.parent=new(){name="ADVWindow(Clone)"};
            Set("lookup",RuntimeDictionaryLookup.FromEntries([new(){OriginalText="Hmm... Not bad.",RussianText="Неплохо."},new(){OriginalText="How are you?",RussianText="Как ты?"},new(){OriginalText="What do you think of me?",RussianText="Что ты обо мне думаешь?"},new(){OriginalText="Who are you friends with?",RussianText="С кем ты дружишь?"},new(){OriginalText="Talk with {{A}}",RussianText="Поговорить с {{A}}"}]));
            Set("mainThread",Environment.CurrentManagedThreadId);typeof(RuntimeDictionaryPlugin).GetField("current",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,plugin);
            typeof(RuntimeDictionaryPlugin).GetProperty(nameof(RuntimeDictionaryPlugin.DialogueTraceEnabled))!.SetValue(plugin,true);plugin.DialogueTraceSink=trace.Add;
        }
        private void Set(string field,object value)=>typeof(RuntimeDictionaryPlugin).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(plugin,value);
        [Theory][InlineData("Hmm... Not bad.","Неплохо.")][InlineData("How are you?","Как ты?")][InlineData("What do you think of me?","Что ты обо мне думаешь?")][InlineData("Who are you friends with?","С кем ты дружишь?")]
        public void KnownDialogueSetterRemainsRussianAfterRepeatedWrites(string original,string expected)
        {
            for(var i=0;i<3;i++){var value=original;RuntimeDictionaryPlugin.BeforeText(label,ref value);label.text=value;RuntimeDictionaryPlugin.AfterDialogueText(label);Assert.Equal(expected,label.text);}
            Assert.Contains(trace,e=>e.CaptureStage=="SetterIncoming"&&e.ReplacementApplied);Assert.Contains(trace,e=>e.OverwriteAttempt);Assert.Contains(trace,e=>e.CaptureStage=="SetterFinal"&&e.NewText==expected);
            var root=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(root,"GameLocalizer.sln"))&&Directory.GetParent(root)!=null)root=Directory.GetParent(root)!.FullName;
            var output=Path.Combine(root,"artifacts","dev","dialogue-runtime");Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,"synthetic-trace-"+Array.IndexOf(new[]{"Hmm... Not bad.","How are you?","What do you think of me?","Who are you friends with?"},original)+".json"),System.Text.Json.JsonSerializer.Serialize(trace,new System.Text.Json.JsonSerializerOptions{IncludeFields=true,WriteIndented=true}));
        }
        [Fact]public void LatePrefixOverwriteIsDetectedAndCorrected()
        {
            var value="Hmm... Not bad.";RuntimeDictionaryPlugin.BeforeText(label,ref value);label.text=value;
            // Simulate another writer running after our prefix with the same complete English line.
            label.text="Hmm... Not bad.";RuntimeDictionaryPlugin.AfterDialogueText(label);Assert.Equal("Неплохо.",label.text);
            var correction=Assert.Single(trace,e=>e.CaptureStage=="PostSetOverrideCorrected");Assert.True(correction.OverwriteAttempt);Assert.True(correction.ReplacementApplied);Assert.Contains("ADVWindow",correction.Hierarchy);Assert.Equal("UnityEngine.UI.Text",correction.Component);Assert.NotEmpty(correction.StackTrace);
            Assert.DoesNotContain("RuntimeDictionaryPlugin",correction.Caller);
        }
        [Fact]public void AuthoritativeFullLineReachesLayoutAndTypewriterAsRussian()
        {
            var controller=new AIProject.CaptionScript.CaptionSystem(label);var value="Hmm... Not bad.";
            RuntimeDictionaryPlugin.BeforeDialogueSource(controller,ref value,controller.GetType().GetMethod("SetText"));controller.SetText(value,false);
            Assert.Equal("Неплохо.",controller.ReactiveValue);Assert.Equal("Неплохо.",controller.AnimationInput);Assert.Equal("Неплохо.",controller.FormatterInput);
            Assert.Contains(trace,e=>e.CaptureStage=="AuthoritativeFullLine"&&e.Caller=="AIProject.CaptionScript.CaptionSystem.SetText");
            var revealed=new List<string>();for(var i=1;i<=controller.AnimationInput.Length;i++)revealed.Add(controller.AnimationInput[..i]);Assert.All(revealed,s=>Assert.DoesNotContain("Hmm",s));
        }
        [Fact]public void FullLineIsTranslatedBeforeFormatterInsertsLineBreaks()
        {
            var formatter=new HyphenationJpn(label);var value="What do you think of me?";RuntimeDictionaryPlugin.BeforeDialogueSource(formatter,ref value);formatter.UpdateText(value);
            Assert.Equal("Что ты обо мне думаешь?",formatter.Input);Assert.Contains("\n",label.text);Assert.DoesNotContain("What",label.text);
        }
        [Theory][InlineData("Never captured dialogue")][InlineData("How ar")]
        public void UnknownOrPartialDialogueUnchanged(string original)
        {var value=original;RuntimeDictionaryPlugin.BeforeDialogueSource(new AIProject.CaptionScript.CaptionSystem(label),ref value);RuntimeDictionaryPlugin.BeforeText(label,ref value);label.text=value;RuntimeDictionaryPlugin.AfterDialogueText(label);Assert.Equal(original,label.text);}
        [Fact]public void DynamicDialogueUsesOnlyExistingSafeTemplate()
        {var value="Talk with Tsubomi";RuntimeDictionaryPlugin.BeforeDialogueSource(new AIProject.CaptionScript.CaptionSystem(label),ref value);Assert.Equal("Поговорить с Tsubomi",value);value="Talk with <tag>";RuntimeDictionaryPlugin.BeforeDialogueSource(new AIProject.CaptionScript.CaptionSystem(label),ref value);Assert.Equal("Talk with <tag>",value);}
        [Fact]public void ProductionFlagDisablesTraceAndStackCollection()
        {typeof(RuntimeDictionaryPlugin).GetProperty(nameof(RuntimeDictionaryPlugin.DialogueTraceEnabled))!.SetValue(plugin,false);var value="How are you?";RuntimeDictionaryPlugin.BeforeText(label,ref value);label.text=value;RuntimeDictionaryPlugin.AfterDialogueText(label);Assert.Empty(trace);Assert.Equal("Как ты?",label.text);}
        [Fact]public void OnlyVerifiedControllerStringSignaturesArePatched()
        {
            Set("harmony",new HarmonyLib.Harmony("test"));HarmonyLib.Harmony.Patched.Clear();typeof(RuntimeDictionaryPlugin).GetMethod("PatchDialogue",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(plugin,[typeof(AIProject.CaptionScript.CaptionSystem).Assembly]);
            Assert.Contains(HarmonyLib.Harmony.Patched,m=>m.DeclaringType==typeof(AIProject.CaptionScript.CaptionSystem)&&m.Name=="SetText");Assert.DoesNotContain(HarmonyLib.Harmony.Patched,m=>m.Name is "Update" or "LateUpdate" or "SetName");Assert.All(HarmonyLib.Harmony.Patched,m=>Assert.Equal(typeof(string),m.GetParameters()[0].ParameterType));
        }
        [Fact]public void DialoguePathHasNoPerFrameScanAndDefaultTraceIsFalse()
        {
            var fresh=new RuntimeDictionaryPlugin();Assert.False(fresh.DialogueTraceEnabled);
            Assert.DoesNotContain(typeof(RuntimeDictionaryPlugin).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public),m=>m.Name is "Update" or "LateUpdate" or "FixedUpdate");
        }
        [Fact]public void DialogueEventCostAndLookupRemainBoundedWithoutTrace()
        {
            typeof(RuntimeDictionaryPlugin).GetProperty(nameof(RuntimeDictionaryPlugin.DialogueTraceEnabled))!.SetValue(plugin,false);
            for(var i=0;i<10000;i++){var value="How are you?";RuntimeDictionaryPlugin.BeforeText(label,ref value);label.text=value;RuntimeDictionaryPlugin.AfterDialogueText(label);}
            var watch=new System.Diagnostics.Stopwatch();var allocation=GC.GetAllocatedBytesForCurrentThread();watch.Start();
            for(var i=0;i<100000;i++){var value="How are you?";RuntimeDictionaryPlugin.BeforeText(label,ref value);label.text=value;RuntimeDictionaryPlugin.AfterDialogueText(label);}watch.Stop();allocation=GC.GetAllocatedBytesForCurrentThread()-allocation;
            Assert.Equal("Как ты?",label.text);Assert.Empty(trace);Assert.True(watch.Elapsed.TotalSeconds<10,"Event-only hooks exceeded 10 seconds for 100k simulated setter events");
            var root=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(root,"GameLocalizer.sln"))&&Directory.GetParent(root)!=null)root=Directory.GetParent(root)!.FullName;
            Directory.CreateDirectory(Path.Combine(root,"artifacts/dev/dialogue-runtime"));File.WriteAllText(Path.Combine(root,"artifacts/dev/dialogue-runtime/event-performance.json"),System.Text.Json.JsonSerializer.Serialize(new{Events=100000,AverageEventNanoseconds=watch.Elapsed.TotalNanoseconds/100000,AllocatedEventBytes=allocation,PerFrameScan=false,TraceEnabled=false},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }
        [Theory][InlineData("Chat","Общение")][InlineData("Give Advice","Дать совет")][InlineData("Give Item","Дать предмет")][InlineData("Options","Настройки")][InlineData("Save","Сохранить")][InlineData("Map","Карта")]
        public void MenuReplacementUnchanged(string original,string russian)
        {Set("lookup",RuntimeDictionaryLookup.FromEntries([new(){OriginalText=original,RussianText=russian}]));var menu=new UnityEngine.UI.Text();var value=original;RuntimeDictionaryPlugin.BeforeText(menu,ref value);menu.text=value;RuntimeDictionaryPlugin.AfterDialogueText(menu);Assert.Equal(russian,menu.text);Assert.Empty(trace);}
        public void Dispose(){typeof(RuntimeDictionaryPlugin).GetField("current",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,null);}
    }
}
// These simulate the actual verified signatures and source ordering, not the game's implementation.
namespace AIProject.CaptionScript
{
    public sealed class CaptionSystem
    {
        private readonly UnityEngine.UI.Text _messageLabel;
        public string ReactiveValue="",AnimationInput="",FormatterInput="";
        public CaptionSystem(UnityEngine.UI.Text label)=>_messageLabel=label;
        public void SetText(string text,bool force){ReactiveValue=text;_messageLabel.text=text;FormatterInput=text;AnimationInput=text;}
        public void SetName(string text){}
    }
}
public sealed class HyphenationJpn
{
    private readonly UnityEngine.UI.Text _text;public string Input="";
    public HyphenationJpn(UnityEngine.UI.Text label)=>_text=label;
    public void SetText(string text)=>UpdateText(text);
    public void SetText(UnityEngine.UI.Text text){}
    public void UpdateText(string text){Input=text;_text.text=text.Replace(" ","\n");}
}

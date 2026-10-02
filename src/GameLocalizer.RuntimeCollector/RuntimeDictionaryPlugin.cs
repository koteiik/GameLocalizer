#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace GameLocalizer.RuntimeCollector
{
    [BepInPlugin("org.gamelocalizer.runtimedictionary", "GameLocalizer Local Runtime Dictionary", "1.0.0")]
    [BepInDependency("org.gamelocalizer.runtimecollector")]
    public sealed partial class RuntimeDictionaryPlugin : BaseUnityPlugin
    {
        private static RuntimeDictionaryPlugin current;
        private RuntimeDictionaryLookup lookup;
        private Harmony harmony;
        private int mainThread;
        [ThreadStatic] private static bool replacing;
        private readonly HashSet<MethodBase> patched=new HashSet<MethodBase>();
        private readonly Dictionary<Type,Access> access=new Dictionary<Type,Access>();
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,Applied> applied = new System.Runtime.CompilerServices.ConditionalWeakTable<object,Applied>();
        private sealed class Applied{public string Original,Target;}
        private sealed class Access{public PropertyInfo Text;public MethodInfo Parsed,Generate,Parse;}
        private void Awake()
        {
            try
            {
                mainThread=Thread.CurrentThread.ManagedThreadId;
                var folder=Path.GetDirectoryName(Info.Location);var config=CollectorConfiguration.Parse(File.ReadAllLines(Path.Combine(folder,"collector.config")));
                string error;lookup=RuntimeDictionaryLookup.Load(Path.Combine(folder,"runtime-dictionary.json"),config.GameId,out error);
                if(error!=null){CollectorPlugin.RecordError(error);Logger.LogWarning(error);}
                if(lookup.Count==0)return;
                InitializeDialogue(config);
                current=this;harmony=new Harmony("org.gamelocalizer.runtimedictionary");
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())Patch(assembly);
                AppDomain.CurrentDomain.AssemblyLoad+=Loaded;
                Logger.LogInfo("Local runtime dictionary loaded once: "+lookup.Count+" entries. Restart game after updates.");
            }
            catch(Exception e){CollectorPlugin.RecordError("Runtime translation disabled: "+e);}
        }
        private void Start()
        {
            if(lookup==null||lookup.Count==0)return;
            try{foreach(var type in new List<Type>(access.Keys))foreach(var obj in Resources.FindObjectsOfTypeAll(type))BeforeEnable(obj);}catch(Exception e){CollectorPlugin.RecordError(e.ToString());}
        }
        private void Loaded(object sender,AssemblyLoadEventArgs e){Patch(e.LoadedAssembly);}
        private void Patch(Assembly assembly)
        {
            PatchDialogue(assembly);
            foreach(var name in new[]{"UnityEngine.UI.Text","TMPro.TMP_Text","TMPro.TextMeshProUGUI","TMPro.TextMeshPro"})
            {
                try
                {
                    var type=assembly.GetType(name,false);if(type==null)continue;
                    lock(patched)
                    {
                        access[type]=new Access{Text=type.GetProperty("text"),Parsed=type.GetMethod("GetParsedText",Type.EmptyTypes),Generate=type.GetMethod("GenerateTextMesh",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public),Parse=type.GetMethod("ParseInputText",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)};
                        foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                        {
                            var parameters=method.GetParameters();string prefix=null,postfix=null;
                            if(method.Name=="set_text" || (method.Name=="SetText" && parameters.Length>0 && parameters[0].ParameterType==typeof(string) && (parameters.Length==1 || parameters.Length==2 && parameters[1].ParameterType==typeof(bool)))){prefix=nameof(BeforeText);if(name=="UnityEngine.UI.Text")postfix=nameof(AfterDialogueText);}
                            else if(method.Name=="OnEnable")prefix=nameof(BeforeEnable);
                            else if(method.Name=="GenerateTextMesh" && !method.IsAbstract)postfix=nameof(AfterGenerate);
                            if(prefix==null&&postfix==null||!patched.Add(method))continue;
                            try{harmony.Patch(method,prefix:prefix==null?null:Hook(prefix),postfix:postfix==null?null:Hook(postfix));}
                            catch(Exception e){CollectorPlugin.RecordError("Runtime hook: "+e.Message);}
                        }
                    }
                }
                catch(Exception e){CollectorPlugin.RecordError(e.ToString());}
            }
        }
        private static HarmonyMethod Hook(string name){var hook=new HarmonyMethod(typeof(RuntimeDictionaryPlugin).GetMethod(name));if(name==nameof(AfterDialogueText))hook.priority=Priority.Last;return hook;}
        public static void BeforeText(object __instance,ref string __0)
        {
            var owner=current;if(owner==null||replacing||Thread.CurrentThread.ManagedThreadId!=owner.mainThread)return;
                        try
            {
                Applied previous;if(__instance!=null&&owner.applied.TryGetValue(__instance,out previous)&&previous.Target==__0){CollectorPlugin.ObserveOriginal(__instance,previous.Original);return;}
                owner.TraceSetter(__instance,__0,"SetterIncoming");
                CollectorPlugin.ObserveOriginal(__instance,__0);
                string value;if(owner.lookup.TryTranslate(__0,out value))
                {owner.Track(__instance,__0,value);__0=value;}
                else if(__instance!=null)owner.applied.Remove(__instance);
            }
            catch(Exception e){CollectorPlugin.RecordError(e.ToString());}
        }
        public static void BeforeEnable(object __instance)
        {
            var owner=current;if(owner==null||replacing||Thread.CurrentThread.ManagedThreadId!=owner.mainThread)return;
            try
            {
                var properties=owner.GetAccess(__instance.GetType());var original=properties.Text?.GetValue(__instance,null) as string;string value;
                Applied previous;if(owner.applied.TryGetValue(__instance,out previous)&&previous.Target==original){CollectorPlugin.ObserveOriginal(__instance,previous.Original);return;}
                if(owner.lookup.TryTranslate(original,out value)&&value!=original){owner.Track(__instance,original,value);CollectorPlugin.ObserveOriginal(__instance,original);replacing=true;try{properties.Text?.SetValue(__instance,value,null);}finally{replacing=false;}}
            }
            catch(Exception e){CollectorPlugin.RecordError(e.ToString());}
        }
        // Formatted TMP SetText is resolved by TMP itself. Rebuild with a known ready value in the same callback, before rendering.
        public static void AfterGenerate(object __instance)
        {
            var owner=current;if(owner==null||replacing||Thread.CurrentThread.ManagedThreadId!=owner.mainThread)return;
            try
            {
                var properties=owner.GetAccess(__instance.GetType());var original=properties.Parsed?.Invoke(__instance,null) as string;
                Applied previous;if(owner.applied.TryGetValue(__instance,out previous)&&previous.Target==original){CollectorPlugin.ObserveOriginal(__instance,previous.Original);return;}
                CollectorPlugin.ObserveOriginal(__instance,original);
                string value;
                if(properties.Parse!=null&&properties.Generate!=null&&owner.lookup.TryTranslate(original,out value)&&value!=original)
                {
                    // A rendered-only match must not discard rich-text formatting that was absent from the captured string.
                    var raw=properties.Text.GetValue(__instance,null) as string;if(raw!=null&&raw.IndexOf('<')>=0&&original.IndexOf('<')<0)return;
                    owner.Track(__instance,original,value);replacing=true;
                    try{properties.Text.SetValue(__instance,value,null);properties.Parse.Invoke(__instance,null);properties.Generate.Invoke(__instance,null);}finally{replacing=false;}
                }
            }
            catch(Exception e){CollectorPlugin.RecordError(e.ToString());}
        }
        private void Track(object instance,string original,string target)
        {
            if(instance==null)return;Applied value;if(!applied.TryGetValue(instance,out value)){value=new Applied();applied.Add(instance,value);}value.Original=original;value.Target=target;
        }
        private Access GetAccess(Type type)
        {
            Access properties;if(access.TryGetValue(type,out properties))return properties;
            properties=new Access{Text=type.GetProperty("text"),Parsed=type.GetMethod("GetParsedText",Type.EmptyTypes),Generate=type.GetMethod("GenerateTextMesh",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic),Parse=type.GetMethod("ParseInputText",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)};access.Add(type,properties);return properties;
        }
        private void OnDestroy(){current=null;AppDomain.CurrentDomain.AssemblyLoad-=Loaded;dialogueTrace?.Dispose();try{harmony?.UnpatchSelf();}catch(Exception e){CollectorPlugin.RecordError(e.ToString());}}
    }
}

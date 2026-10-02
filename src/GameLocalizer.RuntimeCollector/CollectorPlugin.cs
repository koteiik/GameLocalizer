#nullable disable
using System;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;
namespace GameLocalizer.RuntimeCollector
{
    [BepInPlugin("org.gamelocalizer.runtimecollector", "GameLocalizer Runtime UI Collector", "1.0.0")]
    public sealed class CollectorPlugin : BaseUnityPlugin
    {
        private static CollectorPlugin current;
        private CaptureBuffer buffer;
        private Timer writer;
        private Harmony harmony;
        private int mainThread;
        private string errorPath;
        private readonly Dictionary<Component,string> originals = new Dictionary<Component,string>();
        private readonly HashSet<Component> pending = new HashSet<Component>();
        private readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
        private readonly Queue<string> errors = new Queue<string>();
        private readonly List<Type> textTypes = new List<Type>();
        private void Awake()
        {
            try
            {
                mainThread = Thread.CurrentThread.ManagedThreadId;
                var config = CollectorConfiguration.Parse(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(Info.Location), "collector.config")));
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLocalizer", "RuntimeCollector", config.GameId);
                Directory.CreateDirectory(folder);
                errorPath = Path.Combine(folder, "collector-errors.log");
                buffer = new CaptureBuffer(Path.Combine(folder, "runtime-ui-" + Guid.NewGuid().ToString("N") + ".jsonl"));
                current = this;
                harmony = new Harmony("org.gamelocalizer.runtimecollector");
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) PatchAssembly(assembly);
                AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
                writer = new Timer(_ => Flush(), null, 2000, 2000);
                Logger.LogInfo("Read-only UI observation started: " + folder);
            }
            catch (Exception e) { Report(e); }
        }
        private void Start()
        {
            // One initial snapshot; subsequent observation is driven by text/enable hooks.
            try { foreach(var type in textTypes.ToArray()) foreach(var obj in Resources.FindObjectsOfTypeAll(type)) ObserveRendered(obj); } catch(Exception e) { Report(e); }
        }
        private void AssemblyLoaded(object sender, AssemblyLoadEventArgs args) { PatchAssembly(args.LoadedAssembly); }
        private void PatchAssembly(Assembly assembly)
        {
            foreach (var name in new[] { "UnityEngine.UI.Text", "TMPro.TMP_Text", "TMPro.TextMeshProUGUI", "TMPro.TextMeshPro" })
            {
                try
                {
                    var type = assembly.GetType(name, false); if (type == null) continue;
                    lock(patched)
                    {
                        if(!textTypes.Contains(type)) textTypes.Add(type);
                        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .Where(m => m.Name == "set_text" || m.Name == "SetText" || m.Name == "SetCharArray" || m.Name == "OnEnable" || m.Name == "GenerateTextMesh");
                        foreach (var method in methods)
                        {
                            if(!patched.Add(method)) continue;
                            try { harmony.Patch(method, postfix: new HarmonyMethod(typeof(CollectorPlugin).GetMethod(method.Name == "GenerateTextMesh" ? nameof(ObserveRendered) : nameof(Observe), BindingFlags.Static | BindingFlags.Public))); }
                            catch (Exception e) { Report(e); }
                        }
                    }
                }
                catch (Exception e) { Report(e); }
            }
        }
        // Postfix only queues a component; original arguments/results are never changed.
        public static void Observe(object __instance)
        {
            var owner = current; if (owner == null || Thread.CurrentThread.ManagedThreadId != owner.mainThread) return;
            try { if(__instance!=null && __instance.GetType().GetMethod("GetParsedText",Type.EmptyTypes)!=null) return; var component=__instance as Component; if(component!=null && owner.pending.Count<10000) owner.pending.Add(component); }
            catch(Exception e) { owner.Report(e); }
        }
        public static void ObserveRendered(object __instance)
        {
            var owner=current;if(owner==null || Thread.CurrentThread.ManagedThreadId!=owner.mainThread)return;
            try{var component=__instance as Component;if(component!=null && owner.pending.Count<10000)owner.pending.Add(component);}catch(Exception e){owner.Report(e);}
        }
        public static void RecordError(string error)
        {
            var owner=current;if(owner==null)return;try{lock(owner.errors){if(owner.errors.Count<100)owner.errors.Enqueue(DateTime.UtcNow.ToString("O")+" "+error);}}catch{}
        }
        public static void ObserveOriginal(object instance,string original)
        {
            var owner=current;if(owner==null||Thread.CurrentThread.ManagedThreadId!=owner.mainThread)return;
            try{var component=instance as Component;if(component!=null&&owner.pending.Count<10000){owner.originals[component]=original;owner.pending.Add(component);}}catch(Exception e){owner.Report(e);}
        }
        private void LateUpdate()
        {
            if(buffer == null || pending.Count == 0) return;
            var components=pending.ToArray();pending.Clear();
            foreach(var component in components)
            {
                try
                {
                    if(component==null || !component.gameObject.activeInHierarchy) continue;
                    var behaviour=component as Behaviour;if(behaviour!=null && !behaviour.isActiveAndEnabled)continue;
                    var type=component.GetType();
                    var text=type.GetMethod("GetParsedText",Type.EmptyTypes) is MethodInfo parsed ? parsed.Invoke(component,null) as string : type.GetProperty("text")?.GetValue(component,null) as string;
                    string original;if(originals.TryGetValue(component,out original))text=original;
                    if(!CaptureBuffer.Accept(text))continue;
                    var parts=new List<string>();var transform=component.transform;
                    while(transform!=null){parts.Add(transform.name);transform=transform.parent;}parts.Reverse();
                    buffer.Observe(text,component.gameObject.scene.name,component.name,string.Join("/",parts),component.GetType().FullName,component.GetType().Assembly.GetName().Name);
                }
                catch(Exception e){Report(e);}
                finally{originals.Remove(component);}
            }
        }
        private void Flush()
        {
            try {buffer?.Flush();}catch(Exception e){Report(e);}
            try
            {
                string[] batch;lock(errors){batch=errors.ToArray();errors.Clear();}
                if(batch.Length>0 && errorPath!=null) File.AppendAllLines(errorPath,batch);
            }
            catch { }
        }
        private void Report(Exception e)
        { try {lock(errors){if(errors.Count<100)errors.Enqueue(DateTime.UtcNow.ToString("O")+" "+e);} }catch { } }
        private void OnDestroy()
        {
            current=null;AppDomain.CurrentDomain.AssemblyLoad-=AssemblyLoaded;
            writer?.Dispose();Flush();try{harmony?.UnpatchSelf();}catch(Exception e){Report(e);}
        }
    }
}




#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace GameLocalizer.RuntimeCollector
{
    [DataContract] public sealed class DialogueTraceEvent
    {
        [DataMember]public string Timestamp,Scene,Hierarchy,Object,Component,OldText,NewText,IncomingText,Caller,StackTrace,CaptureStage;
        [DataMember]public string[] AttachedScripts;
        [DataMember]public bool ReplacementApplied,OverwriteAttempt;
    }
    public sealed partial class RuntimeDictionaryPlugin
    {
        public bool DialogueTraceEnabled {get;private set;}
        private StreamWriter dialogueTrace;
        private int dialogueTraceCount;
        private readonly ConditionalWeakTable<object,DialogueScope> dialogueScopes=new ConditionalWeakTable<object,DialogueScope>();
        private sealed class DialogueScope {public bool IsDialogue;}
        // Optional DEV sink used by the simulation. Production does no stack collection unless the flag is enabled.
        internal Action<DialogueTraceEvent> DialogueTraceSink {get;set;}
        private void InitializeDialogue(CollectorConfiguration config)
        {
            DialogueTraceEnabled=config.DialogueTraceEnabled;
            if(!DialogueTraceEnabled)return;
            var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameLocalizer","RuntimeCollector",config.GameId);
            Directory.CreateDirectory(directory);dialogueTrace=new StreamWriter(Path.Combine(directory,"dialogue-trace-"+Guid.NewGuid().ToString("N")+".jsonl"),false,new System.Text.UTF8Encoding(false)){AutoFlush=true};
        }
        private void PatchDialogue(Assembly assembly)
        {
            // Signatures and call chain verified in the actual Assembly-CSharp.dll. No guessed controller scan.
            foreach(var name in new[]{"AIProject.CaptionScript.CaptionSystem","HyphenationJpn"})
            {
                var type=assembly.GetType(name,false);if(type==null)continue;
                foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                {
                    var p=method.GetParameters();
                    if(method.Name!="SetText"&&!(name=="HyphenationJpn"&&method.Name=="UpdateText"))continue;
                    if(p.Length==0||p[0].ParameterType!=typeof(string)||p.Length>2||p.Length==2&&p[1].ParameterType!=typeof(bool))continue;
                    lock(patched)
                    {
                        if(!patched.Add(method))continue;
                        try{harmony.Patch(method,prefix:new HarmonyMethod(typeof(RuntimeDictionaryPlugin).GetMethod(nameof(BeforeDialogueSource))){priority=Priority.Last});}
                        catch(Exception e){CollectorPlugin.RecordError("Dialogue source hook: "+method+" "+e.Message);}
                    }
                }
            }
        }
        public static void BeforeDialogueSource(object __instance,ref string __0,MethodBase __originalMethod=null)
        {
            var owner=current;if(owner==null||replacing||Thread.CurrentThread.ManagedThreadId!=owner.mainThread)return;
            try
            {
                string target;if(!owner.lookup.TryTranslate(__0,out target))return;
                var original=__0;var label=FindDialogueLabel(__instance);
                if(owner.DialogueTraceEnabled){var old=label==null?null:owner.GetAccess(label.GetType()).Text?.GetValue(label,null) as string;owner.Trace(label??__instance,old,target,"AuthoritativeFullLine",__originalMethod,true,false,original);}
                if(label!=null){DialogueScope scope;if(!owner.dialogueScopes.TryGetValue(label,out scope)){scope=new DialogueScope();owner.dialogueScopes.Add(label,scope);}scope.IsDialogue=true;owner.Track(label,original,target);CollectorPlugin.ObserveOriginal(label,original);}
                __0=target;
            }
            catch(Exception e){CollectorPlugin.RecordError("Dialogue source: "+e.Message);}
        }
        private static object FindDialogueLabel(object instance)
        {
            if(instance==null)return null;
            var name=instance.GetType().FullName;
            var field=name=="AIProject.CaptionScript.CaptionSystem"?"_messageLabel":name=="HyphenationJpn"?"_text":null;
            return field==null?null:instance.GetType().GetField(field,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(instance);
        }
        private bool IsDialogue(object instance)
        {
            if(instance==null)return false;
            DialogueScope scope;if(dialogueScopes.TryGetValue(instance,out scope))return scope.IsDialogue;
            scope=new DialogueScope();var component=instance as Component;
            if(component!=null)
                for(var transform=component.transform;transform!=null;transform=transform.parent)
                    if(transform.name.IndexOf("ADVWindow",StringComparison.OrdinalIgnoreCase)>=0||transform.name.IndexOf("CommandList",StringComparison.OrdinalIgnoreCase)>=0){scope.IsDialogue=true;break;}
            dialogueScopes.Add(instance,scope);return scope.IsDialogue;
        }
        private void TraceSetter(object instance,string incoming,string stage)
        {
            if(!DialogueTraceEnabled||!IsDialogue(instance))return;
            var old=GetAccess(instance.GetType()).Text?.GetValue(instance,null) as string;
            string target;var found=lookup.TryTranslate(incoming,out target);
            Applied prior;bool overwrite=applied.TryGetValue(instance,out prior)&&old==prior.Target&&found&&incoming==prior.Original;
            Trace(instance,old,found?target:incoming,stage,null,found,overwrite,incoming);
        }
        public static void AfterDialogueText(object __instance)
        {
            var owner=current;if(owner==null||replacing||Thread.CurrentThread.ManagedThreadId!=owner.mainThread||!owner.IsDialogue(__instance))return;
            try
            {
                var properties=owner.GetAccess(__instance.GetType());var actual=properties.Text?.GetValue(__instance,null) as string;
                string target;if(owner.lookup.TryTranslate(actual,out target)&&target!=actual)
                {
                    // Safe guard for later Harmony prefixes/postfixes writing the known English full line.
                    owner.Trace(__instance,actual,target,"PostSetOverrideCorrected",null,true,true,actual);
                    owner.Track(__instance,actual,target);CollectorPlugin.ObserveOriginal(__instance,actual);
                    replacing=true;try{properties.Text.SetValue(__instance,target,null);}finally{replacing=false;}
                }
                owner.Trace(__instance,actual,properties.Text?.GetValue(__instance,null) as string,"SetterFinal",null,false,false,actual);
            }
            catch(Exception e){CollectorPlugin.RecordError("Dialogue post-set: "+e.Message);}
        }
        private void Trace(object instance,string old,string value,string stage,MethodBase source,bool appliedReplacement,bool overwrite,string incoming)
        {
            if(!DialogueTraceEnabled||dialogueTraceCount++>=10000)return;
            try
            {
                var component=instance as Component;var hierarchy=new List<string>();
                if(component!=null)for(var t=component.transform;t!=null;t=t.parent)hierarchy.Add(t.name);hierarchy.Reverse();
                var scripts=new List<string>();if(component!=null)foreach(var script in component.GetComponentsInParent<MonoBehaviour>(true))if(script!=null)scripts.Add(script.GetType().FullName);
                var stack=new StackTrace(2,true);var caller=source??FindTraceCaller(stack);
                var row=new DialogueTraceEvent{Timestamp=DateTime.UtcNow.ToString("O"),Scene=component?.gameObject.scene.name??"",Hierarchy=string.Join("/",hierarchy),Object=component?.name??"",Component=instance?.GetType().FullName??"",AttachedScripts=scripts.ToArray(),OldText=old,NewText=value,IncomingText=incoming,Caller=caller?.DeclaringType?.FullName+"."+caller?.Name,StackTrace=stack.ToString(),CaptureStage=stage,ReplacementApplied=appliedReplacement,OverwriteAttempt=overwrite};
                DialogueTraceSink?.Invoke(row);
                if(dialogueTrace!=null){using(var stream=new MemoryStream()){new DataContractJsonSerializer(typeof(DialogueTraceEvent)).WriteObject(stream,row);dialogueTrace.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));}}
            }
            catch(Exception e){CollectorPlugin.RecordError("Dialogue trace: "+e.Message);}
        }
        private static MethodBase FindTraceCaller(StackTrace stack)
        {
            foreach(var frame in stack.GetFrames()??new StackFrame[0])
            {
                var method=frame.GetMethod();var name=method?.DeclaringType?.FullName;
                if(name==null||name.StartsWith("GameLocalizer.RuntimeCollector.",StringComparison.Ordinal)||name.StartsWith("HarmonyLib.",StringComparison.Ordinal)||name.StartsWith("UnityEngine.",StringComparison.Ordinal)||name.StartsWith("System.",StringComparison.Ordinal))continue;
                return method;
            }
            return null;
        }
    }
}

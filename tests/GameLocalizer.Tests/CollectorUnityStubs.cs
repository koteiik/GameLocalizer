#nullable disable
using System;
using System.Reflection;
namespace BepInEx
{
 [AttributeUsage(AttributeTargets.Class)]public sealed class BepInPlugin:Attribute{public BepInPlugin(string a,string b,string c){}}
 [AttributeUsage(AttributeTargets.Class)]public sealed class BepInDependency:Attribute{public BepInDependency(string id){}}
 public class BaseUnityPlugin:UnityEngine.Behaviour{public PluginInfo Info=new PluginInfo();public Log Logger=new Log();public BepInEx.Configuration.ConfigFile Config=new BepInEx.Configuration.ConfigFile();}
 public class PluginInfo{public string Location="";}public class Log{public void LogInfo(string s){}public void LogWarning(string s){}}
}
namespace BepInEx.Configuration{public class ConfigEntry<T>{public T Value{get;set;}}public class ConfigFile{public ConfigEntry<T> Bind<T>(string section,string key,T value,string description)=>new ConfigEntry<T>{Value=value};}}
namespace HarmonyLib
{
 public class Harmony{public Harmony(string id){} public static System.Collections.Generic.List<MethodBase> Patched=new System.Collections.Generic.List<MethodBase>();public void Patch(MethodBase m,HarmonyMethod prefix=null,HarmonyMethod postfix=null){Patched.Add(m);}public void UnpatchSelf(){}}
 public class HarmonyMethod{public int priority;public HarmonyMethod(MethodInfo m){}}
 public static class Priority{public const int Last=0;}
}
namespace UnityEngine
{
 public class Object{public string name="Text";}
 public class Component:Object{public GameObject gameObject=new GameObject();public Transform transform=new Transform();public T[] GetComponentsInParent<T>(bool includeInactive)=>new T[0];}
 public class MonoBehaviour:Behaviour{}
 public class Behaviour:Component{public bool isActiveAndEnabled=true;}
 public class Transform:Object{public Transform parent;}
 public class GameObject:Object{public bool activeInHierarchy=true;public Scene scene=new Scene();}
 public struct Scene{public string name=>"MainGame";}
 public static class Resources{public static Object[] FindObjectsOfTypeAll(Type t)=>new Object[0];}
}
namespace UnityEngine.UI{public class Text:UnityEngine.Behaviour{public string text{get;set;}}}
namespace TMPro{public class TMP_Text:UnityEngine.Behaviour{public string text{get;set;}public void SetText(string value){text=value;}public string ParsedText;public string GetParsedText()=>ParsedText??text;protected void GenerateTextMesh(){}protected void ParseInputText(){ParsedText=text;}protected void OnEnable(){}}public class TextMeshProUGUI:TMP_Text{}public class TextMeshPro:TMP_Text{}}



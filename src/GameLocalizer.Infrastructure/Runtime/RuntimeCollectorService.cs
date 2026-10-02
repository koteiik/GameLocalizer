using GameLocalizer.RuntimeCollector;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace GameLocalizer.Infrastructure.Runtime;
public sealed class RuntimeUiEntry : System.ComponentModel.INotifyPropertyChanged
{
    public string Text { get; set; } = "";
    public string Scene { get; set; } = "";
    public string Object { get; set; } = "";
    public string Hierarchy { get; set; } = "";
    public string Component { get; set; } = "";
    public string Assembly { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public long SeenCount { get; set; }
    public string MatchStatus { get; set; } = "RuntimeOnly";
    public string Source { get; set; } = "";
    private string russian = "", translationSource = "";
    private bool inDictionary, conflict;
    public string Russian
    {
        get => russian;
        set { if(russian==value)return; russian=value;translationSource="Manual";inDictionary=false;conflict=false;UpdatedAt=DateTimeOffset.UtcNow; Notify();PropertyChanged?.Invoke(this,new(nameof(Russian))); }
    }
    public string TranslationSource => translationSource;
    public bool Approved { get; set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public bool InDictionary => inDictionary;
    public bool DictionaryConflict => conflict;
    public string RuntimeStatus => conflict ? "Conflict" : inDictionary ? "InDictionary" : translationSource=="Manual" && russian.Length>0 ? "Manual" : russian.Length>0 ? "Translated" : "New";
    public string RuntimeDictionary => conflict ? "DictionaryConflict" : inDictionary ? "Да" : "Нет";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void SetTranslation(string text,string source,DateTimeOffset updated)
    {russian=text;translationSource=source;UpdatedAt=updated;Notify();PropertyChanged?.Invoke(this,new(nameof(Russian)));}
    public void SetDictionaryState(bool included,bool hasConflict)
    {inDictionary=included;conflict=hasConflict;Notify();}
    private void Notify()
    {foreach(var name in new[]{nameof(TranslationSource),nameof(RuntimeStatus),nameof(RuntimeDictionary),nameof(InDictionary),nameof(DictionaryConflict)})PropertyChanged?.Invoke(this,new(name));}
    public string NeighborLabels { get; set; } = "";
    public string ExistingCategory { get; set; } = "";
    public string Context => RuntimeUiClassifier.Classify(this).Context.ToString();
    public string ContextConfidence => RuntimeUiClassifier.Classify(this).Confidence;
    public string Category => Context == "Help" && Text.Length > 20 ? "Runtime Help / Tutorial" : "Runtime UI";
    public bool IsSuspiciousShort { get; set; }
    public string Identity => JsonSerializer.Serialize(new[] {Text,Scene,Hierarchy,Component});
}
public sealed record CollectorOwnership(string InstallVersion, Dictionary<string,string> CreatedFiles, string[] CreatedDirectories);
public sealed class RuntimeCollectorService
{
    public const string PluginDirectory = "BepInEx/plugins/GameLocalizerCollector";
    private const string ManifestName = "collector-ownership.json";
    public static string GameId(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\','/').ToUpperInvariant()))).ToLowerInvariant()[..24];
    public static string DataDirectory(string root) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameLocalizer","RuntimeCollector",GameId(root));
    private static string Folder(string root) => SafePath(root, PluginDirectory);
    private static string SafePath(string root,string relative)
    {
        root=Path.GetFullPath(root); var path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new IOException("Path outside game.");
        for(var part=path;part!=null;part=Path.GetDirectoryName(part))
            if((Directory.Exists(part)||File.Exists(part)) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked paths are unsupported.");
        return path;
    }
    public static string Compatibility(string root)
    {
        if(File.Exists(Path.Combine(root,"GameAssembly.dll")) || Directory.EnumerateDirectories(root,"*_Data").Any(d=>Directory.Exists(Path.Combine(d,"il2cpp_data")))) return "Unsupported: IL2CPP";
        if(!File.Exists(Path.Combine(root,"BepInEx","core","BepInEx.dll"))) return "Unsupported: нужен существующий BepInEx Mono";
        if(!Directory.EnumerateDirectories(root,"*_Data").Any(d=>File.Exists(Path.Combine(d,"Managed","UnityEngine.dll")))) return "Unsupported: Unity Mono не найден";
        return "Supported";
    }
    public string Status(string root)
    {
        if(Directory.Exists(DataDirectory(root)) && Directory.EnumerateFiles(DataDirectory(root),"runtime-ui-*.jsonl").Any()) return "Найдены данные";
        return File.Exists(Path.Combine(Folder(root),ManifestName)) ? "Установлен" : "Не установлен";
    }
    private static void AssertGameClosed(string root)
    {
        foreach(var process in System.Diagnostics.Process.GetProcesses())
        {
            using(process)
            { string? exe; try { exe=process.MainModule?.FileName; } catch { continue; }
              if(exe!=null && Path.GetDirectoryName(exe)!.Equals(Path.GetFullPath(root).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) throw new IOException("Закройте игру перед изменением сборщика."); }
        }
    }
    public string Install(string root,string package)
    {
        AssertGameClosed(root);
        if(Compatibility(root)!="Supported") throw new NotSupportedException(Compatibility(root));
        var folder=Folder(root); var manifest=Path.Combine(folder,ManifestName);
        if(Directory.Exists(folder))
        {
            if(!File.Exists(manifest)) throw new IOException("Каталог без ownership manifest: чужие файлы сохранены.");
            var owned=ReadOwnership(root);
            ValidateOwned(root,owned);
            // Keep existing collector intact until package validation has succeeded.
        }
        var bytes=File.ReadAllBytes(package); if(bytes.Length<2 || bytes[0]!='M' || bytes[1]!='Z') throw new InvalidDataException("Invalid collector package");
        var files=new Dictionary<string,byte[]> { ["GameLocalizer.RuntimeCollector.dll"]=bytes,["collector.config"]=Encoding.UTF8.GetBytes(new CollectorConfiguration(GameId(root),ReadDialogueTraceEnabled(root)).Serialize()) };
        foreach(var name in files.Keys)
            if(File.Exists(Path.Combine(folder,name)) && !File.Exists(manifest)) throw new IOException("Foreign file: "+name);
        foreach(var name in files.Keys.Select(n=>n+".installing"))
            if(File.Exists(Path.Combine(folder,name)) && (!File.Exists(manifest) || !ReadOwnership(root).CreatedFiles.ContainsKey(name))) throw new IOException("Foreign staging file preserved: "+name);
        var created=Directory.Exists(folder)?ReadOwnership(root).CreatedDirectories:new[]{PluginDirectory};
        Directory.CreateDirectory(folder);
        var hashes=File.Exists(manifest)?new Dictionary<string,string>(ReadOwnership(root).CreatedFiles):new Dictionary<string,string>();
        foreach(var pair in files.ToDictionary(p=>p.Key,p=>Convert.ToHexString(SHA256.HashData(p.Value))))hashes[pair.Key]=pair.Value;
        
        foreach(var name in files.Keys) hashes[name+".installing"]=hashes[name];
        // Record ownership before writes so an interrupted install can be inspected safely.
        File.WriteAllText(manifest,JsonSerializer.Serialize(new CollectorOwnership("1.0.0",hashes,created)));
        foreach(var file in files) { var target=SafePath(root,PluginDirectory+"/"+file.Key); var temp=target+".installing"; File.WriteAllBytes(temp,file.Value); File.Move(temp,target,true); }
        return Path.Combine(folder,"GameLocalizer.RuntimeCollector.dll");
    }
    public bool IsInstalled(string root) => File.Exists(Path.Combine(Folder(root),ManifestName));
    public bool ReadDialogueTraceEnabled(string root)
    {
        var path=SafePath(root,PluginDirectory+"/collector.config");
        if(!File.Exists(path))return false;
        var config=CollectorConfiguration.Parse(File.ReadAllLines(path));
        if(config.GameId!=GameId(root))throw new InvalidDataException("Collector game ID mismatch");
        return config.DialogueTraceEnabled;
    }
    public void SetDialogueTraceEnabled(string root,bool enabled)
    {
        AssertGameClosed(root);var owned=ReadOwnership(root);ValidateOwned(root,owned);
        var path=SafePath(root,PluginDirectory+"/collector.config");var temp=path+".installing";
        if(File.Exists(temp)&&!owned.CreatedFiles.ContainsKey("collector.config.installing"))throw new IOException("Foreign staging file preserved");
        var bytes=Encoding.UTF8.GetBytes(new CollectorConfiguration(GameId(root),enabled).Serialize());
        var hashes=new Dictionary<string,string>(owned.CreatedFiles);
        hashes["collector.config"]=hashes["collector.config.installing"]=Convert.ToHexString(SHA256.HashData(bytes));
        RuntimeDictionaryService.AtomicSave(Path.Combine(Folder(root),ManifestName),JsonSerializer.Serialize(owned with{CreatedFiles=hashes}));
        File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);
    }
    private static CollectorOwnership ReadOwnership(string root)
    {
        var owned=JsonSerializer.Deserialize<CollectorOwnership>(File.ReadAllText(Path.Combine(Folder(root),ManifestName))) ?? throw new IOException("Invalid ownership manifest");
        if(owned.CreatedFiles.Keys.Any(k=>k is not ("GameLocalizer.RuntimeCollector.dll" or "collector.config" or "GameLocalizer.RuntimeCollector.dll.installing" or "collector.config.installing" or "runtime-dictionary.json" or "runtime-dictionary.json.installing")) || owned.CreatedDirectories.Any(d=>d!=PluginDirectory)) throw new IOException("Invalid collector ownership paths");
        return owned;
    }
    private static void ValidateOwned(string root,CollectorOwnership owned)
    {
        foreach(var file in owned.CreatedFiles)
        { var path=SafePath(root,PluginDirectory+"/"+file.Key); if(File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))!=file.Value) throw new IOException("Изменённый файл сохранён: "+path); }
        foreach(var name in new[]{"GameLocalizer.RuntimeCollector.dll","collector.config"})
            if(File.Exists(Path.Combine(Folder(root),name))&&!owned.CreatedFiles.ContainsKey(name)) throw new IOException("Foreign collector file preserved");
    }
    public string InstallDictionary(string root,string masterPath)
    {
        AssertGameClosed(root);var owned=ReadOwnership(root);ValidateOwned(root,owned);
        var folder=Folder(root);var path=SafePath(root,PluginDirectory+"/runtime-dictionary.json");var temporary=path+".installing";
        foreach(var name in new[]{"runtime-dictionary.json","runtime-dictionary.json.installing"})
            if(File.Exists(Path.Combine(folder,name))&&!owned.CreatedFiles.ContainsKey(name))throw new IOException("Foreign dictionary preserved: "+name);
        var bytes=File.ReadAllBytes(masterPath);var hashes=new Dictionary<string,string>(owned.CreatedFiles);
        var hash=Convert.ToHexString(SHA256.HashData(bytes));hashes["runtime-dictionary.json"]=hash;hashes["runtime-dictionary.json.installing"]=hash;
        RuntimeDictionaryService.AtomicSave(Path.Combine(folder,ManifestName),JsonSerializer.Serialize(owned with{CreatedFiles=hashes}));
        File.WriteAllBytes(temporary,bytes);File.Move(temporary,path,true);return path;
    }
    public void Uninstall(string root)
    {
        AssertGameClosed(root);
        var owned=ReadOwnership(root); ValidateOwned(root,owned);
        foreach(var file in owned.CreatedFiles.Keys) { var path=SafePath(root,PluginDirectory+"/"+file); if(File.Exists(path)) File.Delete(path); }
        File.Delete(Path.Combine(Folder(root),ManifestName));
        foreach(var dir in owned.CreatedDirectories) { var path=SafePath(root,dir); if(Directory.Exists(path)&&!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
    }
    public static IReadOnlyList<RuntimeUiEntry> ImportDirectory(string directory)
    {
        var result=new Dictionary<string,RuntimeUiEntry>();
        if(!Directory.Exists(directory)) return [];
        foreach(var file in Directory.EnumerateFiles(directory,"runtime-ui-*.jsonl"))
        {
            var session=new Dictionary<string,RuntimeUiEntry>();
            using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            using var reader=new StreamReader(stream);
            while(reader.ReadLine() is { } line)
            {
                RuntimeUiEntry? row; try {row=JsonSerializer.Deserialize<RuntimeUiEntry>(line);} catch(JsonException){continue;}
                if(row==null || string.IsNullOrWhiteSpace(row.Text)||row.SeenCount<=0) continue;
                // Each line is a cumulative snapshot within one capture session.
                if(!session.TryGetValue(row.Identity,out var previous)||row.SeenCount>=previous.SeenCount) session[row.Identity]=row;
            }
            foreach(var row in session.Values)
            {
                if(result.TryGetValue(row.Identity,out var old)) {old.SeenCount+=row.SeenCount; old.FirstSeen=old.FirstSeen<row.FirstSeen?old.FirstSeen:row.FirstSeen; old.LastSeen=old.LastSeen>row.LastSeen?old.LastSeen:row.LastSeen;}
                else result.Add(row.Identity,row);
            }
        }
        return result.Values.OrderBy(r=>r.Text,StringComparer.Ordinal).ToArray();
    }
}



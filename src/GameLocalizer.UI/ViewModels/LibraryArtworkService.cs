using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GameLocalizer.Core.Models;
using Microsoft.Win32;

namespace GameLocalizer.UI.ViewModels;

public sealed record LibraryArtwork(BitmapSource Image, bool IsIcon, string Source);

// Local files only. Decode and icon extraction run off the dispatcher; frozen images are shareable.
public sealed class LibraryArtworkService(string cacheDirectory, IEnumerable<string>? steamRoots = null,
    Func<string, BitmapSource?>? iconExtractor = null)
{
    private readonly SemaphoreSlim gate = new(4);
    private sealed record CacheInfo(string Source, long Length, long Modified, bool IsIcon);
    public Task<LibraryArtwork?> LoadAsync(Game game, CancellationToken ct = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return Load(game); }
        finally { gate.Release(); }
    }, ct);
    private LibraryArtwork? Load(Game game)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(game.Path).ToUpperInvariant())));
        var imagePath = Path.Combine(cacheDirectory, key + ".png");
        var infoPath = Path.Combine(cacheDirectory, key + ".json");
        CacheInfo? info = null; BitmapSource? cached = null;
        try { info = JsonSerializer.Deserialize<CacheInfo>(File.ReadAllText(infoPath)); cached = Decode(imagePath); }
        catch(Exception e) when(IsRecoverable(e)) { }
        try
        {
            var sources = SteamArtwork(game).Select(p => (Path:p, Icon:Path.GetFileName(p).Contains("icon",StringComparison.OrdinalIgnoreCase), Exe:false)).Concat(Executables(game).Select(p => (Path:p, Icon:true, Exe:true)));
            foreach(var source in sources)
            {
                try
                {
                    var file = new FileInfo(source.Path);
                    if(cached != null && info != null && info.Source == source.Path && info.Length == file.Length && info.Modified == file.LastWriteTimeUtc.Ticks)
                        return new(cached, info.IsIcon, source.Path);
                    var image = source.Exe ? (iconExtractor ?? ExtractIcon)(source.Path) : Decode(source.Path);
                    if(image == null) continue;
                    if(image.CanFreeze) image.Freeze();
                    var isIcon=source.Icon || (image.PixelWidth==image.PixelHeight && image.PixelWidth<=256);
                    try {
                    Directory.CreateDirectory(cacheDirectory);
                    var temporary = imagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try {
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        using(var output = File.Create(temporary)) encoder.Save(output);
                        File.Move(temporary,imagePath,true);
                        File.WriteAllText(infoPath,JsonSerializer.Serialize(new CacheInfo(source.Path,file.Length,file.LastWriteTimeUtc.Ticks,isIcon)));
                    } finally { if(File.Exists(temporary)) File.Delete(temporary); }
                    } catch(Exception e) when(IsRecoverable(e)) { }
                    return new(image,isIcon,source.Path);
                }
                catch(Exception e) when(IsRecoverable(e)) { }
            }
        }
        catch(Exception e) when(IsRecoverable(e)) { }
        return cached != null ? new(cached, info?.IsIcon ?? true, info?.Source ?? "cache") : null;
    }
    private static bool IsRecoverable(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException or System.Security.SecurityException or InvalidOperationException;
    private static BitmapSource Decode(string path)
    {
        if(new FileInfo(path).Length > 20 * 1024 * 1024) throw new IOException("Artwork exceeds local size limit");
        using var input = File.OpenRead(path);
        var image=BitmapFrame.Create(input,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);image.Freeze();
        if(image.PixelWidth<=640)return image;
        var resized=new TransformedBitmap(image,new System.Windows.Media.ScaleTransform(640d/image.PixelWidth,640d/image.PixelWidth));resized.Freeze();return resized;
    }
    private IEnumerable<string> SteamArtwork(Game game)
    {
        if(game.Platform != "Steam" || !long.TryParse(game.Id,out _)) return [];
        var roots = steamRoots?.ToList() ?? DefaultSteamRoots();
        if(game.Library != null) roots.Add(game.Library);
        var result = new List<string>();
        foreach(var root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) {
            try {
            var cache = Path.Combine(root,"appcache","librarycache");
            if(!Directory.Exists(cache)) continue;
            result.AddRange(Directory.EnumerateFiles(cache,game.Id+"_*").Where(IsImage));
            var app = Path.Combine(cache,game.Id);
            if(Directory.Exists(app)) result.AddRange(Directory.EnumerateFiles(app,"*",SearchOption.TopDirectoryOnly).Where(IsImage));
            } catch(Exception e) when(IsRecoverable(e)) { }
        }
        return result.OrderBy(p => {
            var name=Path.GetFileName(p).ToLowerInvariant();
            return name.Contains("library_capsule") ? 0 : name.Contains("header") ? 1 : name.Contains("library_hero") ? 2 : name.Contains("600x900") ? 3 : name.Contains("icon") ? 4 : 5;
        });
    }
    private static bool IsImage(string p) => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".ico";
    private static List<string> DefaultSteamRoots()
    {
        var roots = new List<string>();
        foreach(var key in new[]{@"HKEY_CURRENT_USER\Software\Valve\Steam",@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam"})
            foreach(var name in new[]{"SteamPath","InstallPath"}) if(Registry.GetValue(key,name,null) is string path) roots.Add(path);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam")); return roots;
    }
    private static IEnumerable<string> Executables(Game game)
    {
        if(!Directory.Exists(game.Path)) return [];
        var files=Directory.EnumerateFiles(game.Path,"*.exe").ToList();
        foreach(var dir in Directory.EnumerateDirectories(game.Path).Take(32)) {
            var name=Path.GetFileName(dir);
            if(name.Equals("bin",StringComparison.OrdinalIgnoreCase) || name.Equals("Binaries",StringComparison.OrdinalIgnoreCase))
                files.AddRange(Directory.EnumerateFiles(dir,"*.exe",SearchOption.AllDirectories).Take(100));
        }
        return files.Where(p => !new[]{"unins","crash","setup","updater","UnityCrash","ModelHost"}.Any(s => Path.GetFileName(p).Contains(s,StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => Directory.Exists(Path.Combine(Path.GetDirectoryName(p)!,Path.GetFileNameWithoutExtension(p)+"_Data")))
            .ThenByDescending(p => Path.GetFileNameWithoutExtension(p).Equals(Path.GetFileName(game.Path),StringComparison.OrdinalIgnoreCase) || game.Name.Contains(Path.GetFileNameWithoutExtension(p),StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => new FileInfo(p).Length);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern uint ExtractIconEx(string file,int index,IntPtr[] large,IntPtr[] small,uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public static BitmapSource? ExtractIcon(string file)
    {
        var large=new IntPtr[1];var small=new IntPtr[1];
        try {
            if(ExtractIconEx(file,0,large,small,1)==0) return null;
            var handle=large[0]!=IntPtr.Zero?large[0]:small[0]; if(handle==IntPtr.Zero)return null;
            var image=Imaging.CreateBitmapSourceFromHIcon(handle,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());image.Freeze();return image;
        } finally { if(large[0]!=IntPtr.Zero)DestroyIcon(large[0]);if(small[0]!=IntPtr.Zero)DestroyIcon(small[0]); }
    }
}

using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLocalizer.Core.Models;
using GameLocalizer.UI.ViewModels;
using Xunit;
namespace GameLocalizer.Tests;
public sealed class LibraryArtworkTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"GameLocalizerArtworkTests",Guid.NewGuid().ToString("N"));
    public LibraryArtworkTests()=>Directory.CreateDirectory(root);
    public void Dispose()=>Directory.Delete(root,true);
    private Game Game(string platform="Manual") {var path=Path.Combine(root,"game");Directory.CreateDirectory(path);return new("123","Game",path,platform,root);}
    private static BitmapSource Icon() {var image=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)180,32*32*4).ToArray(),128);image.Freeze();return image;}
    private string Artwork(string name,int width=640,int height=300) {
        var path=Path.Combine(root,"appcache/librarycache/123",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)180,width*height*4).ToArray(),width*4);image.Freeze();
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(path);encoder.Save(output);return path;
    }
    [Fact] public async Task SteamLocalHeaderPreferredOverIconWithoutNetwork() {
        var game=Game("Steam");var capsule=Artwork("hash_library_capsule.png");Artwork("hash_header.png");File.WriteAllText(Path.Combine(game.Path,"game.exe"),"fake");
        var service=new LibraryArtworkService(Path.Combine(root,"cache"),[root],_=>throw new InvalidOperationException("Exe must not be extracted"));
        var result=Assert.IsType<LibraryArtwork>(await service.LoadAsync(game));Assert.Equal(Path.GetFullPath(capsule),result.Source);Assert.False(result.IsIcon);Assert.True(result.Image.IsFrozen);
    }
    [Fact] public async Task SteamHeaderFitsCardBetterThanWideHero() {
        var game=Game("Steam");Artwork("hash_library_hero.png",640,200);var header=Artwork("hash_header.png",640,300);
        var result=Assert.IsType<LibraryArtwork>(await new LibraryArtworkService(Path.Combine(root,"cache"),[root]).LoadAsync(game));
        Assert.Equal(Path.GetFullPath(header),result.Source);Assert.False(result.IsIcon);
    }
    [Fact] public async Task SteamMissingArtworkFallsBackToExecutableIcon() {
        var game=Game("Steam");File.WriteAllText(Path.Combine(game.Path,"game.exe"),"fake");
        var result=Assert.IsType<LibraryArtwork>(await new LibraryArtworkService(Path.Combine(root,"cache"),[root],_=>Icon()).LoadAsync(game));Assert.True(result.IsIcon);Assert.EndsWith("game.exe",result.Source);
    }
    [Fact] public async Task ManualIconCacheReusedAndSourceChangeRefreshes() {
        var game=Game();var exe=Path.Combine(game.Path,"game.exe");File.WriteAllText(exe,"one");var calls=0;
        var service=new LibraryArtworkService(Path.Combine(root,"cache"),[],_=>{calls++;return Icon();});
        Assert.True((await service.LoadAsync(game))!.IsIcon);Assert.Equal(1,calls);
        var restarted=new LibraryArtworkService(Path.Combine(root,"cache"),[],_=>{calls++;return Icon();});await restarted.LoadAsync(game);Assert.Equal(1,calls);
        File.WriteAllText(exe,"different-size");await restarted.LoadAsync(game);Assert.Equal(2,calls);
    }
    [Fact] public async Task MissingIconReturnsPlaceholder() {var game=Game();File.WriteAllText(Path.Combine(game.Path,"game.exe"),"fake");Assert.Null(await new LibraryArtworkService(Path.Combine(root,"cache"),[],_=>null).LoadAsync(game));}
    [Fact] public async Task CorruptCacheIsRepairedAndSurvivesMissingSource() {
        var game=Game("Steam");var source=Artwork("header.png");var cache=Path.Combine(root,"cache");var service=new LibraryArtworkService(cache,[root]);
        await service.LoadAsync(game);File.WriteAllText(Directory.GetFiles(cache,"*.png").Single(),"corrupt");Assert.NotNull(await service.LoadAsync(game));
        File.Delete(source);Assert.NotNull(await service.LoadAsync(game));
        File.WriteAllText(Directory.GetFiles(cache,"*.png").Single(),"corrupt");Assert.Null(await service.LoadAsync(game));
    }
    [Fact] public async Task LoadingDoesNotBlockCallerAndFrozenIconIsShareable() {
        var game=Game();File.WriteAllText(Path.Combine(game.Path,"game.exe"),"fake");using var release=new ManualResetEventSlim();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service=new LibraryArtworkService(Path.Combine(root,"cache"),[],_=>{started.TrySetResult();release.Wait(TimeSpan.FromSeconds(10));return Icon();});
        var load=service.LoadAsync(game);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(load.IsCompleted);release.Set();Assert.True((await load)!.Image.IsFrozen);
    }
    [Fact] public void ExtractsRealWindowsExecutableIcon() {Assert.NotNull(LibraryArtworkService.ExtractIcon(Path.Combine(Environment.SystemDirectory,"notepad.exe")));}
}

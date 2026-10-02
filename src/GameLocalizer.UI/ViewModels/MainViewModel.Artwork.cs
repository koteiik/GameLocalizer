using System.IO;
using GameLocalizer.Core.Models;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    private readonly HashSet<Game> artworkGames = [];
    private LibraryArtworkService artworkService = null!;
    public Task ArtworkLoadTask {get;private set;}=Task.CompletedTask;
    private void InitializeLibraryArtwork()
    {
        artworkService=new(Path.Combine(settingsService.DataDirectory,"Artwork"));
        void Queue(Game item) {
            if(!artworkGames.Add(item))return;
            ArtworkLoadTask=Task.WhenAll(ArtworkLoadTask,LoadGameArtworkAsync(item));
        }
        foreach(var item in Games)Queue(item);
        Games.CollectionChanged+=(_,e)=>{if(e.NewItems != null)foreach(Game item in e.NewItems)Queue(item);};
    }
    private async Task LoadGameArtworkAsync(Game item)
    {
        try {
            var result=await artworkService.LoadAsync(item);
            if(result != null){item.ArtworkIsIcon=result.IsIcon;item.Artwork=result.Image;}
            var applied=await Task.Run(()=>applyStates.Load(item.Path));
            if(applied != null && !ReferenceEquals(item,SelectedGame)) {var status=applied.State switch {
                GameLocalizer.Infrastructure.FileSystem.GameApplyState.Applied => await VerifyLibraryAppliedAsync(item,applied) ? "Перевод применён" : "Требуется обновление",
                GameLocalizer.Infrastructure.FileSystem.GameApplyState.ApplyFailed => "Ошибка применения",
                GameLocalizer.Infrastructure.FileSystem.GameApplyState.ApplyPartiallyCompleted => "Применено частично",
                _ => item.Status
            };if(!ReferenceEquals(item,SelectedGame))item.Status=status;}
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }
    private async Task<bool> VerifyLibraryAppliedAsync(Game item,GameLocalizer.Infrastructure.FileSystem.GameApplyResult result)
    {
        try {
            if(!await GameLocalizer.Infrastructure.FileSystem.ApplyStateStore.VerifyFilesAsync(result,CancellationToken.None))return false;
            await workspace.VerifyApplyOwnershipAsync(item,result.VerifiedFiles,CancellationToken.None);return true;
        } catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {return false;}
    }

}

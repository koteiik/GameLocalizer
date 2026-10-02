using GameLocalizer.Core.Models;
using GameLocalizer.Core.Localization;
using GameLocalizer.Infrastructure.Database;
namespace GameLocalizer.Infrastructure.FileSystem;
public sealed partial class ScanWorkspaceService
{
    public int LastFilesRescanned { get; private set; }
    public int LastFilesReused { get; private set; }
    public int LastValidFilesRescanned { get; private set; }
    private int analysisErrors;
    private static async Task<string> FileHashAsync(string path,CancellationToken ct)
    {
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream,ct));
    }
    public Task<AnalysisSnapshot?> LoadAnalysisAsync(Game game,CancellationToken ct) => repository.LoadSnapshotAsync(game.Path,ct);
    public async Task<int> ValidateAnalysisAsync(AnalysisSnapshot snapshot,CancellationToken ct)
    {
        int changed=0;
        foreach(var f in await repository.AnalysisFilesAsync(snapshot.Session,ct))
        {
            ct.ThrowIfCancellationRequested(); var info=new FileInfo(f.PhysicalPath);
            if(!info.Exists) { changed++; continue; }
            if(info.Length==f.Size && info.LastWriteTimeUtc.Ticks==f.LastWriteTicks) continue;
            if(await FileHashAsync(f.PhysicalPath,ct)!=f.Hash) changed++;
            else await repository.UpdateFileStampAsync(snapshot.Session,f with{Size=info.Length,LastWriteTicks=info.LastWriteTimeUtc.Ticks},ct);
        }
        var directories=await repository.AnalysisDirectoriesAsync(snapshot.Session,ct);
        if(changed==0 && directories.Any(d=>!Directory.Exists(d.Path) || Directory.GetLastWriteTimeUtc(d.Path).Ticks!=d.Ticks))changed=1;
        return changed;
    }
    public async Task<ScanProgress> RefreshAnalysisAsync(Game game,string session,string engine,double confidence,bool full,IProgress<ScanProgress>? progress,CancellationToken ct)
    {
        var old=await repository.LoadSnapshotAsync(game.Path,ct);
        var previous=old==null?new Dictionary<string,AnalysisFile>(StringComparer.OrdinalIgnoreCase):(await repository.AnalysisFilesAsync(old.Session,ct)).ToDictionary(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase);
        var catalog=new ResourceScanner(adapters).Enumerate(game.Path,ct).Where(r=>r.Editable).ToArray();
        var changed=new List<Resource>();var files=new List<AnalysisFile>();LastFilesRescanned=LastFilesReused=0;
        foreach(var resource in catalog)
        {
            ct.ThrowIfCancellationRequested();var relative=Path.GetRelativePath(game.Path,resource.Path);var info=new FileInfo(resource.Path);
            if(previous.TryGetValue(relative,out var cached) && !full)
            {
                bool same=info.Length==cached.Size && info.LastWriteTimeUtc.Ticks==cached.LastWriteTicks;
                if(!same) same=await FileHashAsync(resource.Path,ct)==cached.Hash;
                if(same) { await repository.CopyFileAsync(old!.Session,session,relative,ct);files.Add(cached with{Size=info.Length,LastWriteTicks=info.LastWriteTimeUtc.Ticks});LastFilesReused++;continue; }
            }
            changed.Add(resource);
        }
        var inspected=new Dictionary<string,AnalysisFile>(StringComparer.OrdinalIgnoreCase);
        await foreach(var batch in pipeline.ScanAsync(game.Path,ct,changed))
        {
            await repository.AppendAsync(session,game.Id,batch,ct);
            if(batch.SourceHash!=null)
            {
                var relative=Path.GetRelativePath(game.Path,batch.Resource.Path);var info=new FileInfo(batch.Resource.Path);
                inspected[relative]=new(relative,Path.GetFullPath(batch.Resource.Path),info.Length,info.LastWriteTimeUtc.Ticks,batch.SourceHash,batch.Resource.Format,ActiveLocalizationResolver.SlotFromPath(batch.Resource.Path));
            }
            else if(batch.Resource.Editable && File.Exists(batch.Resource.Path))
            {
                var relative=Path.GetRelativePath(game.Path,batch.Resource.Path);var info=new FileInfo(batch.Resource.Path);
                inspected[relative]=new(relative,Path.GetFullPath(batch.Resource.Path),info.Length,info.LastWriteTimeUtc.Ticks,await FileHashAsync(batch.Resource.Path,ct),batch.Resource.Format,ActiveLocalizationResolver.SlotFromPath(batch.Resource.Path),true);
            }
            progress?.Report(batch.Progress);
        }
        LastFilesRescanned=changed.Count;
        LastValidFilesRescanned=inspected.Count(p=>!p.Value.ScanFailed);
        foreach(var pair in inspected)
        {
            if(pair.Value.ScanFailed) { if(old!=null && previous.ContainsKey(pair.Key))await repository.CopyFileAsync(old.Session,session,pair.Key,ct); }
            else if(old!=null && previous.ContainsKey(pair.Key)) await repository.MergePreviousFileAsync(old.Session,session,pair.Key,ct);
            else await RestoreBackupOriginalAsync(game,session,pair.Value,ct);
            files.Add(pair.Value);
        }
        // Keep an earlier successful file if a changed optional parser failed; do not publish an empty replacement.
        foreach(var resource in changed)
        {
            var relative=Path.GetRelativePath(game.Path,resource.Path);
            if(!inspected.ContainsKey(relative) && old!=null && previous.TryGetValue(relative,out var cached)) { await repository.CopyFileAsync(old.Session,session,relative,ct);files.Add(cached); }
        }
        await RestoreMemoryAsync(game,session,ct);
        analysisErrors=files.Count(f=>f.ScanFailed);
        await repository.CommitSnapshotAsync(game,session,engine,confidence,files,ct);
        var page=await repository.QueryAsync(session,new(),0,ct);
        return new(catalog.Length,page.Total,page.Total,ScanErrorCount,page.Selected);
    }
    public async Task RefreshAppliedStampsAsync(Game game,string session,bool applied,CancellationToken ct)
    {
        var owned=await repository.AppliedFilesAsync(session,applied,ct);
        await repository.SetAppliedAsync(session,applied,ct);
        foreach(var f in await repository.AnalysisFilesAsync(session,ct))
        {
            if(!owned.Contains(f.RelativePath))continue;
            var info=new FileInfo(f.PhysicalPath);if(!info.Exists)continue;
            var hash=await FileHashAsync(f.PhysicalPath,ct);
            await repository.UpdateFileStampAsync(session,f with{Size=info.Length,LastWriteTicks=info.LastWriteTimeUtc.Ticks,Hash=hash},ct);
        }
        foreach(var dir in await repository.AnalysisDirectoriesAsync(session,ct))await repository.UpdateDirectoryStampAsync(session,dir.Path,Directory.GetLastWriteTimeUtc(dir.Path).Ticks,ct);
    }
    private async Task RestoreBackupOriginalAsync(Game game,string session,AnalysisFile file,CancellationToken ct)
    {
        var original=await backup.ReadOriginalForAppliedFileAsync(game.Path,file.RelativePath,file.Hash,ct);if(original==null)return;
        var adapter=LocalizationAdapterSelector.Select(adapters,file.PhysicalPath,original);
        var entries=adapter.Extract(original).ToDictionary(e=>e.Id,e=>e.Text);
        await repository.RestoreOriginalsAsync(session,file.RelativePath,entries,ct);
    }
}

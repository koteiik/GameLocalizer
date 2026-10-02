using System.Text.Json;
namespace GameLocalizer.Infrastructure.FileSystem;
public sealed partial class BackupService
{
    public async Task VerifyOwnershipAsync(string root,IReadOnlyDictionary<string,string> expected,CancellationToken ct)
    {
        var dir=Path.Combine(Path.GetFullPath(root),"GameLocalizer_Backup");
        if(!File.Exists(Path.Combine(dir,"manifest.json")))throw new IOException("Ownership manifest missing");
        for(string? parent=dir;parent!=null;parent=Path.GetDirectoryName(parent))
            if(Directory.Exists(parent)&&File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Unsafe ownership directory");
        if(File.GetAttributes(Path.Combine(dir,"manifest.json")).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Unsafe ownership manifest");
        var manifest=JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(Path.Combine(dir,"manifest.json"),ct))??throw new IOException("Invalid ownership manifest");
        ValidateManifest(root,manifest);
        foreach(var item in expected) {
            var target=Resolve(root,item.Key);
            var record=manifest.Files.SingleOrDefault(r=>Resolve(root,r.RelativePath).Equals(target,StringComparison.OrdinalIgnoreCase));
            if(record != null) {
                if(record.AppliedHash!=item.Value)throw new IOException("Ownership applied hash mismatch: "+item.Key);
                await ReadOriginal(dir,record,ct);
            } else {
                var created=manifest.CreatedFiles.SingleOrDefault(r=>Resolve(root,r.RelativePath).Equals(target,StringComparison.OrdinalIgnoreCase));
                if(!manifest.CreatedByGameLocalizer || created?.CreatedByGameLocalizer!=true || created.AppliedHash!=item.Value)throw new IOException("Ownership record missing: "+item.Key);
            }
            if(TextFiles.Hash(await File.ReadAllBytesAsync(target,ct))!=item.Value)throw new IOException("Applied SHA256 mismatch: "+item.Key);
        }
    }
}

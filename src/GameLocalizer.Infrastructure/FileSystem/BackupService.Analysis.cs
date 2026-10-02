using System.Text.Json;
namespace GameLocalizer.Infrastructure.FileSystem;
public sealed partial class BackupService
{
    public async Task<string?> ReadOriginalForAppliedFileAsync(string root,string relative,string currentHash,CancellationToken ct)
    {
        var manifestPath=Path.Combine(root,"GameLocalizer_Backup","manifest.json");
        var manifest=ReadRegistry(root);
        if(File.Exists(manifestPath))
        {
            var disk=JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath,ct));
            if(disk!=null && (manifest==null || disk.Revision>=manifest.Revision))manifest=disk;
        }
        if(manifest==null)return null;ValidateManifest(root,manifest);
        var record=manifest.Files.FirstOrDefault(f=>f.RelativePath.Equals(relative,StringComparison.OrdinalIgnoreCase) && f.AppliedHash==currentHash);
        if(record==null)return null;
        var bytes=await ReadOriginal(Path.Combine(root,"GameLocalizer_Backup"),record,ct);
        // The original file can carry a BOM; the existing BOM-aware reader is used on the owned backup.
        return (await TextFiles.ReadAsync(Path.Combine(root,"GameLocalizer_Backup",record.ObjectName),ct)).Text;
    }
}

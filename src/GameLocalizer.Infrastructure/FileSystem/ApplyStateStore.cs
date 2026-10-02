using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace GameLocalizer.Infrastructure.FileSystem;

public enum GameApplyState { ReadyToApply, Applying, Applied, ApplyPartiallyCompleted, ApplyFailed, NeedsUpdate }
public sealed class GameApplyResult
{
    public GameApplyState State { get; set; }
    public string Root { get; set; } = "";
    public string Session { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Details { get; set; } = "";
    public string? ReportPath { get; set; }
    public int FilesApplied { get; set; }
    public int FilesFailed { get; set; }
    public Dictionary<string,string> VerifiedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class ApplyStateStore(string directory)
{
    private string PathFor(string root) => Path.Combine(directory,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\','/').ToUpperInvariant())))+".json");
    public GameApplyResult? Load(string root)
    {
        try { var result=JsonSerializer.Deserialize<GameApplyResult>(File.ReadAllText(PathFor(root))); if(result?.State==GameApplyState.Applying) { result.State=GameApplyState.ApplyFailed;result.Reason="Применение было прервано. Проверьте отчёт перед повтором."; } return result; }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public async Task SaveAsync(GameApplyResult result)
    {
        Directory.CreateDirectory(directory);var path=PathFor(result.Root);var temporary=path+".tmp";
        try { await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(result,new JsonSerializerOptions {WriteIndented=true}));File.Move(temporary,path,true); }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static async Task<bool> VerifyFilesAsync(GameApplyResult result, CancellationToken ct)
    {
        foreach(var file in result.VerifiedFiles) {
            var target=BackupService.Resolve(result.Root,file.Key);
            if(!File.Exists(target) || TextFiles.Hash(await File.ReadAllBytesAsync(target,ct))!=file.Value)return false;
        }
        return result.VerifiedFiles.Count > 0;
    }
}

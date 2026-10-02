using GameLocalizer.Infrastructure.Runtime;
namespace GameLocalizer.Infrastructure.Database;
public sealed partial class ScanResultRepository
{
    public Task MatchRuntimeAsync(string? session,string root,IReadOnlyList<RuntimeUiEntry> entries,CancellationToken ct) => Run(db=>
    {
        var writable=new Dictionary<string,string>(StringComparer.Ordinal); var unsupported=new Dictionary<string,string>(StringComparer.Ordinal);
        if(session!=null)
        {
            using var cmd=db.CreateCommand(); cmd.CommandText="SELECT Original,FilePath,AdapterType,Category FROM ScanRows WHERE Session=$session";cmd.Parameters.AddWithValue("$session",session);
            using var reader=cmd.ExecuteReader();while(reader.Read()) { if(reader.GetString(3)=="UnsupportedUI") unsupported.TryAdd(reader.GetString(0),reader.GetString(1)); else if(reader.GetString(3)!="Technical" && reader.GetString(2) is "BepInEx / XUnity key=value" or "JSON" or "XML" or "TSV" or "CSV" or "INI" or "TXT" or "PO (singular)") writable.TryAdd(reader.GetString(0),reader.GetString(1)); }
        }
        foreach(var row in entries) { row.MatchStatus="RuntimeOnly";row.Source=""; if(writable.TryGetValue(row.Text,out var source)){row.MatchStatus="MatchedWritable";row.Source=source;} else if(unsupported.TryGetValue(row.Text,out var other)){row.MatchStatus="MatchedUnsupported";row.Source=other;} }
        return 0;
    },ct);
}




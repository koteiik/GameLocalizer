using System.Security.Cryptography;
using System.Text;
using GameLocalizer.Core.Models;
using Microsoft.Data.Sqlite;
namespace GameLocalizer.Infrastructure.Database;
public record AnalysisSnapshot(string Identity, string Root, string Session, string Engine, double EngineConfidence, DateTimeOffset Timestamp, long Translated);
public record AnalysisFile(string RelativePath, string PhysicalPath, long Size, long LastWriteTicks, string Hash, string AdapterType, string LocalizationSlot, bool ScanFailed = false);
public sealed partial class ScanResultRepository
{
    public const int AnalysisSchemaVersion = 1;
    public static string GameIdentity(string root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\','/').ToUpperInvariant())));
    private static void InitializeAnalysis(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS AnalysisSchema(Version INTEGER NOT NULL);
            INSERT INTO AnalysisSchema SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM AnalysisSchema);
            CREATE TABLE IF NOT EXISTS AnalysisSnapshots(Identity TEXT PRIMARY KEY, Root TEXT NOT NULL, Session TEXT NOT NULL, Engine TEXT NOT NULL, Confidence REAL NOT NULL, Timestamp TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AnalysisSessions(Identity TEXT NOT NULL,Session TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS AnalysisDirectories(Session TEXT NOT NULL,DirectoryPath TEXT NOT NULL,LastWriteTicks INTEGER NOT NULL,PRIMARY KEY(Session,DirectoryPath));
            CREATE TABLE IF NOT EXISTS AnalysisUi(Identity TEXT PRIMARY KEY,Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AnalysisFiles(Session TEXT NOT NULL, RelativePath TEXT NOT NULL, PhysicalPath TEXT NOT NULL, Size INTEGER NOT NULL, LastWriteTicks INTEGER NOT NULL, Hash TEXT NOT NULL, AdapterType TEXT NOT NULL, LocalizationSlot TEXT NOT NULL, ScanFailed INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(Session,RelativePath));
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText="PRAGMA table_info(AnalysisFiles)";bool hasFailure=false;
        using(var r=cmd.ExecuteReader())while(r.Read())if(r.GetString(1)=="ScanFailed")hasFailure=true;
        if(!hasFailure){cmd.CommandText="ALTER TABLE AnalysisFiles ADD COLUMN ScanFailed INTEGER NOT NULL DEFAULT 0";cmd.ExecuteNonQuery();}
        foreach(var (column,type) in new[]{("StableRowId","TEXT NOT NULL DEFAULT ''"),("ManualEdit","INTEGER NOT NULL DEFAULT 0"),("Applied","INTEGER NOT NULL DEFAULT 0"),("TranslationSource","TEXT NOT NULL DEFAULT ''")})
        {
            cmd.CommandText = "PRAGMA table_info(ScanRows)"; bool exists=false;
            using(var r=cmd.ExecuteReader()) while(r.Read()) if(r.GetString(1)==column) exists=true;
            if(!exists) { cmd.CommandText=$"ALTER TABLE ScanRows ADD COLUMN {column} {type}"; cmd.ExecuteNonQuery(); }
        }
        cmd.CommandText="SELECT Version FROM AnalysisSchema LIMIT 1";
        if(Convert.ToInt32(cmd.ExecuteScalar()) != AnalysisSchemaVersion) throw new InvalidDataException("Unsupported analysis schema version; cache retained");
    }
    public Task<AnalysisSnapshot?> LoadSnapshotAsync(string root,CancellationToken ct) => Run<AnalysisSnapshot?>(db =>
    {
        using var cmd=db.CreateCommand(); cmd.CommandText="SELECT Identity,Root,Session,Engine,Confidence,Timestamp,(SELECT COUNT(*) FROM ScanRows WHERE Session=s.Session AND Translation<>'' AND Status IN ('Manual','Translated','FromMemory')) FROM AnalysisSnapshots s WHERE Identity=$id";
        cmd.Parameters.AddWithValue("$id",GameIdentity(root)); using var r=cmd.ExecuteReader();
        return r.Read() ? new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetDouble(4),DateTimeOffset.Parse(r.GetString(5)),r.GetInt64(6)) : null;
    },ct);
    public Task<IReadOnlyList<AnalysisFile>> AnalysisFilesAsync(string session,CancellationToken ct) => Run<IReadOnlyList<AnalysisFile>>(db =>
    {
        using var cmd=db.CreateCommand(); cmd.CommandText="SELECT RelativePath,PhysicalPath,Size,LastWriteTicks,Hash,AdapterType,LocalizationSlot,ScanFailed FROM AnalysisFiles WHERE Session=$session"; Bind(cmd,session);
        using var r=cmd.ExecuteReader(); var files=new List<AnalysisFile>(); while(r.Read()) files.Add(new(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetBoolean(7))); return files;
    },ct);
    public Task UpdateAnalysisEngineAsync(string root,string engine,double confidence,CancellationToken ct) => Run(db=>
    {
        using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$id",GameIdentity(root));cmd.Parameters.AddWithValue("$engine",engine);cmd.Parameters.AddWithValue("$confidence",confidence);cmd.CommandText="UPDATE AnalysisSnapshots SET Engine=$engine,Confidence=$confidence WHERE Identity=$id";return cmd.ExecuteNonQuery();
    },ct);
    private const string CopyColumns="GameId,FilePath,EntryKey,Original,Translation,Context,Confidence,Selected,Status,Category,PhysicalSourceFile,LocalizationSlot,AdapterType,StableRowId,ManualEdit,Applied,TranslationSource";
    public Task CopyFileAsync(string oldSession,string session,string file,CancellationToken ct) => Run(db =>
    {
        using var tx=db.BeginTransaction(); using var cmd=db.CreateCommand(); cmd.Transaction=tx;
        cmd.Parameters.AddWithValue("$old",oldSession); Bind(cmd,session); cmd.Parameters.AddWithValue("$file",file);
        cmd.CommandText=$"INSERT INTO ScanRows(Session,{CopyColumns}) SELECT $session,{CopyColumns} FROM ScanRows WHERE Session=$old AND FilePath=$file"; var count=cmd.ExecuteNonQuery();
        cmd.CommandText="INSERT OR REPLACE INTO ScanFiles SELECT $session,FilePath,SourceHash FROM ScanFiles WHERE Session=$old AND FilePath=$file"; cmd.ExecuteNonQuery(); ct.ThrowIfCancellationRequested(); tx.Commit(); return count;
    },ct);
    public Task MergePreviousFileAsync(string oldSession,string session,string file,CancellationToken ct) => Run(db =>
    {
        using var tx=db.BeginTransaction(); using var cmd=db.CreateCommand(); cmd.Transaction=tx; Bind(cmd,session); cmd.Parameters.AddWithValue("$old",oldSession); cmd.Parameters.AddWithValue("$file",file);
        // Stable adapter entry IDs already encode duplicate occurrences; additionally partition repeated IDs.
        cmd.CommandText="""
            CREATE TEMP TABLE Matches AS WITH old AS MATERIALIZED (SELECT *,gl_entry_key(EntryKey) matchKey,ROW_NUMBER() OVER(PARTITION BY FilePath,AdapterType,gl_entry_key(EntryKey) ORDER BY Id) occurrence FROM ScanRows WHERE Session=$old AND FilePath=$file),
                 current AS (SELECT Id,ROW_NUMBER() OVER(PARTITION BY FilePath,AdapterType,gl_entry_key(EntryKey) ORDER BY Id) occurrence FROM ScanRows WHERE Session=$session AND FilePath=$file)
            SELECT n.Id,o.Original,o.Translation,o.Status,o.Selected,o.ManualEdit,o.TranslationSource,o.StableRowId,o.Category,o.Confidence,
            (o.Applied=1 AND n.Original=o.Translation) appliedMatch,(n.Original=o.Original OR (o.Applied=1 AND n.Original=o.Translation)) same
            FROM ScanRows n JOIN current c ON c.Id=n.Id JOIN old o ON o.FilePath=n.FilePath AND o.matchKey=gl_entry_key(n.EntryKey) AND o.AdapterType=n.AdapterType AND o.occurrence=c.occurrence
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText="""
            UPDATE ScanRows SET Original=CASE WHEN m.appliedMatch THEN m.Original ELSE ScanRows.Original END,
            Translation=m.Translation,Status=CASE WHEN m.same THEN m.Status ELSE 'SourceChanged' END,
            Selected=(m.Selected AND m.same),ManualEdit=(m.ManualEdit AND m.same),Applied=m.appliedMatch,
            TranslationSource=m.TranslationSource,StableRowId=m.StableRowId,
            Category=CASE WHEN m.appliedMatch THEN m.Category ELSE ScanRows.Category END,
            Confidence=CASE WHEN m.appliedMatch THEN m.Confidence ELSE ScanRows.Confidence END
            FROM Matches m WHERE ScanRows.Id=m.Id
            """;
        var count=cmd.ExecuteNonQuery();ct.ThrowIfCancellationRequested();tx.Commit();return count;
    },ct);
    public Task CommitSnapshotAsync(Game game,string session,string engine,double confidence,IReadOnlyList<AnalysisFile> files,CancellationToken ct) => Run(db =>
    {
        using var tx=db.BeginTransaction(); using var cmd=db.CreateCommand(); cmd.Transaction=tx; Bind(cmd,session);
        db.CreateFunction<string,string,string,long,string>("gl_stable",(file,adapter,key,occurrence)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[]{GameIdentity(game.Path),file.ToUpperInvariant(),adapter,key,occurrence.ToString()})))),true);
        cmd.CommandText="WITH identities AS MATERIALIZED (SELECT Id,gl_stable(FilePath,AdapterType,gl_entry_key(EntryKey),ROW_NUMBER() OVER(PARTITION BY FilePath,AdapterType,gl_entry_key(EntryKey) ORDER BY Id)) stable FROM ScanRows WHERE Session=$session) UPDATE ScanRows SET StableRowId=identities.stable FROM identities WHERE ScanRows.Id=identities.Id";
        cmd.ExecuteNonQuery();
        cmd.CommandText="INSERT OR REPLACE INTO AnalysisFiles VALUES($session,$relative,$physical,$size,$ticks,$hash,$adapter,$slot,$failed)";
        foreach(var p in new[]{"$relative","$physical","$size","$ticks","$hash","$adapter","$slot","$failed"})cmd.Parameters.AddWithValue(p,"");
        foreach(var f in files) { ct.ThrowIfCancellationRequested();cmd.Parameters["$relative"].Value=f.RelativePath;cmd.Parameters["$physical"].Value=f.PhysicalPath;cmd.Parameters["$size"].Value=f.Size;cmd.Parameters["$ticks"].Value=f.LastWriteTicks;cmd.Parameters["$hash"].Value=f.Hash;cmd.Parameters["$adapter"].Value=f.AdapterType;cmd.Parameters["$slot"].Value=f.LocalizationSlot;cmd.Parameters["$failed"].Value=f.ScanFailed;cmd.ExecuteNonQuery(); }
        var dirs=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Path.GetFullPath(game.Path)};
        foreach(var f in files)for(var dir=Path.GetDirectoryName(f.PhysicalPath);dir!=null && (dir.Equals(Path.GetFullPath(game.Path),StringComparison.OrdinalIgnoreCase) || dir.StartsWith(Path.GetFullPath(game.Path).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase));dir=Path.GetDirectoryName(dir))dirs.Add(dir);
        cmd.CommandText="INSERT OR REPLACE INTO AnalysisDirectories VALUES($session,$directory,$ticks)";cmd.Parameters.AddWithValue("$directory","");
        foreach(var dir in dirs){cmd.Parameters["$directory"].Value=dir;cmd.Parameters["$ticks"].Value=Directory.GetLastWriteTimeUtc(dir).Ticks;cmd.ExecuteNonQuery();}
        cmd.CommandText="INSERT OR REPLACE INTO AnalysisSnapshots VALUES($identity,$root,$session,$engine,$confidence,$timestamp)";
        cmd.Parameters.AddWithValue("$identity",GameIdentity(game.Path));cmd.Parameters.AddWithValue("$root",Path.GetFullPath(game.Path));cmd.Parameters.AddWithValue("$engine",engine);cmd.Parameters.AddWithValue("$confidence",confidence);cmd.Parameters.AddWithValue("$timestamp",DateTimeOffset.Now.ToString("O"));cmd.ExecuteNonQuery();
        cmd.CommandText="INSERT OR REPLACE INTO AnalysisSessions VALUES($identity,$session)";cmd.ExecuteNonQuery();
        const string retired="SELECT Session FROM AnalysisSessions WHERE Identity=$identity AND Session<>$session ORDER BY rowid DESC LIMIT -1 OFFSET 1";
        foreach(var table in new[]{"ScanRows","ScanFiles","AnalysisFiles","AnalysisDirectories","AnalysisSessions"}){cmd.CommandText=$"DELETE FROM {table} WHERE Session IN ({retired})";cmd.ExecuteNonQuery();}
        ct.ThrowIfCancellationRequested();tx.Commit();return 0;
    },ct);
    public Task ForgetSnapshotAsync(string root,CancellationToken ct) => Run(db =>
    {
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.Parameters.AddWithValue("$identity",GameIdentity(root));
        foreach(var table in new[]{"ScanRows","ScanFiles","AnalysisFiles","AnalysisDirectories"}){cmd.CommandText=$"DELETE FROM {table} WHERE Session IN(SELECT Session FROM AnalysisSessions WHERE Identity=$identity UNION SELECT Session FROM AnalysisSnapshots WHERE Identity=$identity)";cmd.ExecuteNonQuery();}
        cmd.CommandText="DELETE FROM AnalysisUi WHERE Identity=$identity";cmd.ExecuteNonQuery();
        cmd.CommandText="DELETE FROM AnalysisSessions WHERE Identity=$identity";cmd.ExecuteNonQuery();
        cmd.CommandText="DELETE FROM AnalysisSnapshots WHERE Identity=$identity";cmd.ExecuteNonQuery();tx.Commit();return 0;
    },ct);
    public Task SetAppliedAsync(string session,bool applied,CancellationToken ct) => Run(db =>
    {
        using var cmd=db.CreateCommand();Bind(cmd,session);cmd.Parameters.AddWithValue("$applied",applied);
        cmd.CommandText="UPDATE ScanRows SET Applied=$applied WHERE Session=$session"+(applied?" AND Selected=1 AND Translation<>'' AND Status IN ('Translated','FromMemory','Manual')":"");return cmd.ExecuteNonQuery();
    },ct);
    public Task<HashSet<string>> AppliedFilesAsync(string session,bool applying,CancellationToken ct) => Run(db=>
    {
        using var cmd=db.CreateCommand();Bind(cmd,session);cmd.CommandText="SELECT DISTINCT FilePath FROM ScanRows WHERE Session=$session AND "+(applying?"Selected=1 AND Translation<>'' AND Status IN ('Translated','FromMemory','Manual')":"Applied=1");
        using var r=cmd.ExecuteReader();var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);while(r.Read())files.Add(r.GetString(0));return files;
    },ct);
    public Task RestoreOriginalsAsync(string session,string file,IReadOnlyDictionary<string,string> originals,CancellationToken ct) => Run(db=>
    {
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;Bind(cmd,session);cmd.Parameters.AddWithValue("$file",file);cmd.Parameters.AddWithValue("$key","");cmd.Parameters.AddWithValue("$original","");
        cmd.CommandText="UPDATE ScanRows SET Translation=CASE WHEN Original<>$original THEN Original ELSE '' END,Status=CASE WHEN Original<>$original THEN 'Translated' ELSE 'NotTranslated' END,Applied=(Original<>$original),TranslationSource=CASE WHEN Original<>$original THEN 'Existing file' ELSE '' END,Original=$original WHERE Session=$session AND FilePath=$file AND EntryKey=$key";
        foreach(var pair in originals){ct.ThrowIfCancellationRequested();cmd.Parameters["$key"].Value=pair.Key;cmd.Parameters["$original"].Value=pair.Value;cmd.ExecuteNonQuery();}tx.Commit();return originals.Count;
    },ct);
    public Task UpdateFileStampAsync(string session,AnalysisFile file,CancellationToken ct) => Run(db=>
    {
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;Bind(cmd,session);cmd.Parameters.AddWithValue("$file",file.RelativePath);cmd.Parameters.AddWithValue("$size",file.Size);cmd.Parameters.AddWithValue("$ticks",file.LastWriteTicks);cmd.Parameters.AddWithValue("$hash",file.Hash);
        cmd.CommandText="UPDATE AnalysisFiles SET Size=$size,LastWriteTicks=$ticks,Hash=$hash WHERE Session=$session AND RelativePath=$file";cmd.ExecuteNonQuery();cmd.CommandText="UPDATE ScanFiles SET SourceHash=$hash WHERE Session=$session AND FilePath=$file";cmd.ExecuteNonQuery();tx.Commit();return 0;
    },ct);
    public Task<IReadOnlyList<(string Path,long Ticks)>> AnalysisDirectoriesAsync(string session,CancellationToken ct) => Run<IReadOnlyList<(string Path,long Ticks)>>(db=>
    {
        using var cmd=db.CreateCommand();Bind(cmd,session);cmd.CommandText="SELECT DirectoryPath,LastWriteTicks FROM AnalysisDirectories WHERE Session=$session";using var r=cmd.ExecuteReader();var dirs=new List<(string,long)>();while(r.Read())dirs.Add((r.GetString(0),r.GetInt64(1)));return dirs;
    },ct);
    public Task UpdateDirectoryStampAsync(string session,string dir,long ticks,CancellationToken ct) => Run(db=>
    {
        using var cmd=db.CreateCommand();Bind(cmd,session);cmd.Parameters.AddWithValue("$dir",dir);cmd.Parameters.AddWithValue("$ticks",ticks);cmd.CommandText="UPDATE AnalysisDirectories SET LastWriteTicks=$ticks WHERE Session=$session AND DirectoryPath=$dir";return cmd.ExecuteNonQuery();
    },ct);
    public Task SaveUiDiscoveryAsync(string root,IReadOnlyList<UnsupportedUiCandidate> rows,CancellationToken ct) => Run(db=>
    {
        using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$id",GameIdentity(root));cmd.Parameters.AddWithValue("$payload",System.Text.Json.JsonSerializer.Serialize(rows));cmd.CommandText="INSERT OR REPLACE INTO AnalysisUi VALUES($id,$payload)";return cmd.ExecuteNonQuery();
    },ct);
    public Task<IReadOnlyList<UnsupportedUiCandidate>> LoadUiDiscoveryAsync(string root,CancellationToken ct) => Run<IReadOnlyList<UnsupportedUiCandidate>>(db=>
    {
        using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$id",GameIdentity(root));cmd.CommandText="SELECT Payload FROM AnalysisUi WHERE Identity=$id";return cmd.ExecuteScalar() is string json?System.Text.Json.JsonSerializer.Deserialize<List<UnsupportedUiCandidate>>(json)??[]:[];
    },ct);
}

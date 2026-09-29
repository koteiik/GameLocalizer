using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;
using Microsoft.Data.Sqlite;

namespace GameLocalizer.Infrastructure.Database;

/// <summary>Disk-backed scan sessions. All SQLite work runs off the WPF dispatcher.</summary>
public sealed class ScanResultRepository(string databasePath) : IDisposable
{
    public const int PageSize = 2000;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        db.Open();
        db.CreateFunction<string, string, bool>("gl_contains", (text, value) => text.Contains(value, StringComparison.OrdinalIgnoreCase), true);
        if (!initialized)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS ScanRows (
                  Id INTEGER PRIMARY KEY, Session TEXT NOT NULL, GameId TEXT NOT NULL, FilePath TEXT NOT NULL,
                  EntryKey TEXT NOT NULL, Original TEXT NOT NULL, Translation TEXT NOT NULL DEFAULT '', Context TEXT NOT NULL,
                  Confidence REAL NOT NULL, Selected INTEGER NOT NULL, Status TEXT NOT NULL DEFAULT 'Не переведено');
                CREATE INDEX IF NOT EXISTS ScanSessionId ON ScanRows(Session, Id);
                CREATE INDEX IF NOT EXISTS ScanSelected ON ScanRows(Session, Selected, Status, Id);
                CREATE INDEX IF NOT EXISTS ScanFile ON ScanRows(Session, FilePath);
                CREATE TABLE IF NOT EXISTS ScanFiles (Session TEXT NOT NULL, FilePath TEXT NOT NULL, SourceHash TEXT NOT NULL, PRIMARY KEY(Session, FilePath));
                """;
            cmd.ExecuteNonQuery(); initialized = true;
        }
        return db;
    }
    private async Task<T> Run<T>(Func<SqliteConnection, T> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => { ct.ThrowIfCancellationRequested(); using var db = Open(); return action(db); }, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private static void Bind(SqliteCommand cmd, string session) => cmd.Parameters.AddWithValue("$session", session);
    public Task AppendAsync(string session, string gameId, ScanBatch batch, CancellationToken ct) => Run(db =>
    {
        if (batch.Entries.Count == 0) return 0;
        using var transaction = db.BeginTransaction();
        using (var file = db.CreateCommand())
        {
            file.Transaction = transaction;
            file.CommandText = "INSERT OR IGNORE INTO ScanFiles VALUES ($session,$file,$hash)";
            Bind(file, session); file.Parameters.AddWithValue("$file", batch.Entries[0].FilePath); file.Parameters.AddWithValue("$hash", batch.SourceHash ?? throw new InvalidDataException("Missing scan hash")); file.ExecuteNonQuery();
        }
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ScanRows(Session,GameId,FilePath,EntryKey,Original,Context,Confidence,Selected) VALUES($session,$game,$file,$key,$original,$context,$confidence,$selected)";
        Bind(command, session); command.Parameters.AddWithValue("$game", gameId);
        foreach (var parameter in new[] { "$file", "$key", "$original", "$context", "$confidence", "$selected" }) command.Parameters.Add(new SqliteParameter(parameter, ""));
        command.Prepare();
        foreach (var entry in batch.Entries)
        {
            ct.ThrowIfCancellationRequested();
            command.Parameters["$file"].Value = entry.FilePath; command.Parameters["$key"].Value = entry.Key;
            command.Parameters["$original"].Value = entry.Original; command.Parameters["$context"].Value = entry.Context;
            command.Parameters["$confidence"].Value = entry.Confidence; command.Parameters["$selected"].Value = entry.Selected ? 1 : 0;
            command.ExecuteNonQuery();
        }
        ct.ThrowIfCancellationRequested(); transaction.Commit(); return batch.Entries.Count;
    }, ct);

    private static string Where(SqliteCommand command, string session, ScanQuery query)
    {
        Bind(command, session); command.Parameters.AddWithValue("$min", query.MinimumConfidence);
        command.Parameters.AddWithValue("$search", query.Search); command.Parameters.AddWithValue("$file", query.File); command.Parameters.AddWithValue("$status", query.Status);
        return "Session=$session AND Confidence >= $min AND ($status='Все' OR Status=$status) AND ($file='' OR gl_contains(FilePath,$file)) AND ($search='' OR gl_contains(Original,$search) OR gl_contains(Translation,$search) OR gl_contains(EntryKey,$search))";
    }
    private static ScanRow Read(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDouble(6), r.GetBoolean(7), r.GetString(8));
    private const string Columns = "Id,FilePath,EntryKey,Original,Translation,Context,Confidence,Selected,Status";
    public Task<ScanPage> QueryAsync(string session, ScanQuery query, int page, CancellationToken ct) => Run(db =>
    {
        using var command = db.CreateCommand(); var where = Where(command, session, query);
        using var registration = ct.Register(command.Cancel);
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(Selected),0) FROM ScanRows WHERE Session=$session";
        long total, selected;
        using (var reader = command.ExecuteReader()) { reader.Read(); total = reader.GetInt64(0); selected = reader.GetInt64(1); }
        command.CommandText = "SELECT COUNT(*) FROM ScanRows WHERE " + where;
        var matching = (long)command.ExecuteScalar()!;
        var sort = query.Sort switch { ScanSort.Key => "EntryKey", ScanSort.Original => "Original", ScanSort.Translation => "Translation", ScanSort.FilePath => "FilePath", ScanSort.Confidence => "Confidence", ScanSort.Status => "Status", _ => "Id" };
        command.CommandText = $"SELECT {Columns} FROM ScanRows WHERE {where} ORDER BY {sort} {(query.Descending ? "DESC" : "ASC")},Id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", PageSize); command.Parameters.AddWithValue("$offset", (long)Math.Max(0, page) * PageSize);
        using var rows = command.ExecuteReader(); var result = new List<ScanRow>();
        while (rows.Read()) { ct.ThrowIfCancellationRequested(); result.Add(Read(rows)); }
        return new ScanPage(result, total, matching, selected);
    }, ct);

    public Task SaveEditsAsync(string session, IReadOnlyList<ScanEdit> edits, CancellationToken ct) => Run(db =>
    {
        using var transaction = db.BeginTransaction();
        using var read = db.CreateCommand(); read.CommandText = "SELECT Original FROM ScanRows WHERE Session=$session AND Id=$id"; Bind(read, session); read.Parameters.AddWithValue("$id", 0L);
        read.Transaction = transaction;
        using var write = db.CreateCommand(); write.CommandText = "UPDATE ScanRows SET Translation=$translation,Selected=$selected,Status=$status WHERE Session=$session AND Id=$id";
        write.Transaction = transaction;
        Bind(write, session); foreach (var parameter in new[] { "$translation", "$selected", "$status", "$id" }) write.Parameters.AddWithValue(parameter, "");
        foreach (var edit in edits)
        {
            ct.ThrowIfCancellationRequested(); read.Parameters["$id"].Value = edit.Id;
            if (read.ExecuteScalar() is not string original) continue;
            var status = string.IsNullOrWhiteSpace(edit.Translation) ? "Не переведено" : new TranslationValidator().Validate(original, edit.Translation, out _) ? "Готово" : "Validation Error";
            write.Parameters["$translation"].Value = edit.Translation; write.Parameters["$selected"].Value = edit.Selected ? 1 : 0; write.Parameters["$status"].Value = status; write.Parameters["$id"].Value = edit.Id;
            write.ExecuteNonQuery();
        }
        ct.ThrowIfCancellationRequested(); transaction.Commit(); return edits.Count;
    }, ct);

    public Task<IReadOnlyList<ScanRow>> ReadSelectedAsync(string session, long afterId, bool onlyUntranslated, CancellationToken ct) => Run<IReadOnlyList<ScanRow>>(db =>
    {
        using var cmd = db.CreateCommand(); Bind(cmd, session); cmd.Parameters.AddWithValue("$id", afterId);
        cmd.CommandText = $"SELECT {Columns} FROM ScanRows WHERE Session=$session AND Selected=1 AND Id>$id {(onlyUntranslated ? "AND Status='Не переведено'" : "")} ORDER BY Id LIMIT 1000";
        using var r = cmd.ExecuteReader(); var result = new List<ScanRow>(); while (r.Read()) { ct.ThrowIfCancellationRequested(); result.Add(Read(r)); } return result;
    }, ct);
    public Task<IReadOnlyList<ScannedFile>> SelectedFilesAsync(string session, CancellationToken ct) => Run<IReadOnlyList<ScannedFile>>(db =>
    {
        using var cmd = db.CreateCommand(); Bind(cmd, session);
        cmd.CommandText = "SELECT COUNT(*) FROM ScanRows WHERE Session=$session AND Selected=1 AND Status<>'Готово'";
        if ((long)cmd.ExecuteScalar()! != 0) throw new InvalidDataException("Выбранные строки содержат пустой перевод или Validation Error (включая другие страницы).");
        cmd.CommandText = "SELECT f.FilePath,f.SourceHash FROM ScanFiles f WHERE f.Session=$session AND EXISTS(SELECT 1 FROM ScanRows r WHERE r.Session=f.Session AND r.FilePath=f.FilePath AND r.Selected=1)";
        using var r = cmd.ExecuteReader(); var result = new List<ScannedFile>(); while (r.Read()) { ct.ThrowIfCancellationRequested(); result.Add(new(r.GetString(0), r.GetString(1))); } return result;
    }, ct);
    public Task<IReadOnlyList<ScanRow>> FileRowsAsync(string session, string file, CancellationToken ct) => Run<IReadOnlyList<ScanRow>>(db =>
    {
        using var cmd = db.CreateCommand(); Bind(cmd, session); cmd.Parameters.AddWithValue("$file", file);
        cmd.CommandText = $"SELECT {Columns} FROM ScanRows WHERE Session=$session AND FilePath=$file AND Selected=1 ORDER BY Id";
        using var r = cmd.ExecuteReader(); var result = new List<ScanRow>(); while (r.Read()) { ct.ThrowIfCancellationRequested(); result.Add(Read(r)); } return result;
    }, ct);
    public void Dispose()
    {
        gate.Dispose();
        // This database is an ephemeral per-process cache, never translation memory.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(databasePath + suffix); } catch (IOException) { }
    }
}

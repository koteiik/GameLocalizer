using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
namespace GameLocalizer.Infrastructure.Database;

public sealed partial class TranslationMemoryService(string databasePath) : ITranslationMemoryService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    public static string Hash(string text) => TextFiles.Hash(Encoding.UTF8.GetBytes(text));
    public static string Identity(MemoryKey key, bool manual = false) => Hash(JsonSerializer.Serialize(new[] { key.SourceText, key.SourceLanguage, key.TargetLanguage, key.GameId, key.Context,
        manual ? "Manual" : key.Provider, manual ? "" : key.Model, manual ? "" : key.ModelVersion, manual ? "" : key.GlossaryVersion, manual ? "" : key.Category }));
    private async Task<T> Run<T>(Func<SqliteConnection, T> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()); db.Open();
                if (!initialized) { BackupBeforeMigration(db); Initialize(db); initialized = true; }
                return action(db);
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private void BackupBeforeMigration(SqliteConnection db)
    {
        using var check = db.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        if ((long)check.ExecuteScalar()! == 0) return;
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='MemoryMigrations'";
        if ((long)check.ExecuteScalar()! != 0)
        {
            check.CommandText = "SELECT COUNT(*) FROM MemoryMigrations WHERE Version=2";
            if ((long)check.ExecuteScalar()! != 0) return;
        }
        // SQLite's backup API includes committed WAL pages; copying memory.db alone would not.
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath + ".migration-v2-" + Guid.NewGuid().ToString("N") + ".bak", Pooling = false }.ToString());
        backup.Open(); db.BackupDatabase(backup);
    }
    private static void Initialize(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS TranslationMemory (
              Id INTEGER PRIMARY KEY, GameId TEXT NOT NULL, GameName TEXT NOT NULL, SourceText TEXT NOT NULL,
              SourceHash TEXT NOT NULL UNIQUE, TranslatedText TEXT NOT NULL, SourceLanguage TEXT NOT NULL, TargetLanguage TEXT NOT NULL,
              Context TEXT NOT NULL, FilePath TEXT NOT NULL, Key TEXT NOT NULL, Category TEXT NOT NULL,
              TranslationProvider TEXT NOT NULL, TranslationModel TEXT NOT NULL, TranslationModelVersion TEXT NOT NULL,
              GlossaryVersion TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, LastUsedAt TEXT NOT NULL, IsManual INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS MemoryLookup ON TranslationMemory(GameId,SourceLanguage,TargetLanguage,Context,IsManual);
            CREATE TABLE IF NOT EXISTS MemoryMigrations (Version INTEGER PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM MemoryMigrations WHERE Version=2";
        if ((long)command.ExecuteScalar()! != 0) return;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Memory'";
        var old = (long)command.ExecuteScalar()! != 0;
        using var transaction = db.BeginTransaction(); command.Transaction = transaction;
        if (old)
        {
            command.CommandText = "SELECT SourceText,TranslatedText,SourceLanguage,TargetLanguage,GameId,GameName,FilePath,Key,Context,SourceHash,Provider,CreatedAt,UpdatedAt FROM Memory";
            var entries = new List<MemoryEntry>();
            using (var reader = command.ExecuteReader()) while (reader.Read())
                entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), DateTimeOffset.Parse(reader.GetString(11)), DateTimeOffset.Parse(reader.GetString(12)), TranslationModel: reader.GetString(10), TranslationModelVersion: "1"));
            foreach (var entry in entries) Save(db, entry, transaction);
        }
        command.CommandText = "INSERT INTO MemoryMigrations VALUES(2)"; command.ExecuteNonQuery(); transaction.Commit();
    }
    public Task<CachedTranslation?> FindAsync(MemoryKey key, CancellationToken ct) => Run<CachedTranslation?>(db =>
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id,TranslatedText,IsManual FROM TranslationMemory WHERE SourceHash IN ($hash,$manual) ORDER BY IsManual DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$hash", Identity(key)); cmd.Parameters.AddWithValue("$manual", Identity(key, true));
        long id; CachedTranslation result;
        using (var r = cmd.ExecuteReader()) { if (!r.Read()) return null; id = r.GetInt64(0); result = new(r.GetString(1), r.GetBoolean(2)); }
        cmd.CommandText = "UPDATE TranslationMemory SET LastUsedAt=$now WHERE Id=$id";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); return result;
    }, ct);
    public Task<IReadOnlyDictionary<string, CachedTranslation>> FindManyAsync(IReadOnlyList<MemoryKey> keys, CancellationToken ct) => Run<IReadOnlyDictionary<string, CachedTranslation>>(db =>
    {
        var result = new Dictionary<string, CachedTranslation>();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT 1 FROM TranslationMemory LIMIT 1";
        if (cmd.ExecuteScalar() == null) return result;
        cmd.CommandText = "SELECT Id,TranslatedText,IsManual FROM TranslationMemory WHERE SourceHash IN ($hash,$manual) ORDER BY IsManual DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$hash", ""); cmd.Parameters.AddWithValue("$manual", ""); cmd.Prepare();
        var used = new HashSet<long>();
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested(); var identity = Identity(key);
            cmd.Parameters["$hash"].Value = identity; cmd.Parameters["$manual"].Value = Identity(key, true);
            using var reader = cmd.ExecuteReader();
            if (reader.Read()) { used.Add(reader.GetInt64(0)); result[identity] = new(reader.GetString(1), reader.GetBoolean(2)); }
        }
        using var transaction = db.BeginTransaction();
        using var update = db.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE TranslationMemory SET LastUsedAt=$used WHERE Id=$id";
        update.Parameters.AddWithValue("$used", DateTimeOffset.UtcNow.ToString("O")); update.Parameters.AddWithValue("$id", 0L); update.Prepare();
        foreach (var id in used) { update.Parameters["$id"].Value = id; update.ExecuteNonQuery(); }
        transaction.Commit(); return result;
    }, ct);
    // Compatibility lookup for adapters/tests and the original public memory API.
    public Task<string?> FindAsync(string text, string sourceLanguage, string targetLanguage, string gameId, string context, string provider, CancellationToken ct) => Run(db =>
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT TranslatedText FROM TranslationMemory WHERE SourceText=$text AND SourceLanguage=$source AND TargetLanguage=$target AND GameId=$game AND Context=$context AND (TranslationProvider=$provider OR IsManual=1) ORDER BY IsManual DESC,UpdatedAt DESC LIMIT 1";
        foreach (var pair in new[] { ("$text", text), ("$source", sourceLanguage), ("$target", targetLanguage), ("$game", gameId), ("$context", context), ("$provider", provider) }) cmd.Parameters.AddWithValue(pair.Item1, pair.Item2);
        return cmd.ExecuteScalar() as string;
    }, ct);
    public Task SaveAsync(MemoryEntry entry, CancellationToken ct) => Run(db => { Save(db, entry); return 0; }, ct);
    private static void Save(SqliteConnection db, MemoryEntry entry, SqliteTransaction? transaction = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO TranslationMemory(GameId,GameName,SourceText,SourceHash,TranslatedText,SourceLanguage,TargetLanguage,Context,FilePath,Key,Category,TranslationProvider,TranslationModel,TranslationModelVersion,GlossaryVersion,CreatedAt,UpdatedAt,LastUsedAt,IsManual)
            VALUES($GameId,$GameName,$SourceText,$hash,$TranslatedText,$SourceLanguage,$TargetLanguage,$Context,$FilePath,$Key,$Category,$Provider,$TranslationModel,$TranslationModelVersion,$GlossaryVersion,$CreatedAt,$UpdatedAt,$used,$IsManual)
            ON CONFLICT(SourceHash) DO UPDATE SET TranslatedText=excluded.TranslatedText,UpdatedAt=excluded.UpdatedAt,LastUsedAt=excluded.LastUsedAt,FilePath=excluded.FilePath,Key=excluded.Key,Category=excluded.Category;
            """;
        foreach (var property in typeof(MemoryEntry).GetProperties())
        {
            var value = property.GetValue(entry);
            cmd.Parameters.AddWithValue("$" + property.Name, value switch { DateTimeOffset date => date.ToString("O"), bool flag => flag ? 1 : 0, _ => value?.ToString() ?? "" });
        }
        var key = new MemoryKey(entry.SourceText, entry.SourceLanguage, entry.TargetLanguage, entry.GameId, entry.Context, entry.Provider, entry.TranslationModel, entry.TranslationModelVersion, entry.GlossaryVersion, entry.Category);
        cmd.Parameters.AddWithValue("$hash", Identity(key, entry.IsManual)); cmd.Parameters.AddWithValue("$used", (entry.LastUsedAt ?? DateTimeOffset.UtcNow).ToString("O")); cmd.ExecuteNonQuery();
    }
}

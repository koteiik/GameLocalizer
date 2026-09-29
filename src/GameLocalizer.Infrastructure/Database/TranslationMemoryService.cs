using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Data.Sqlite;
using System.Text;
namespace GameLocalizer.Infrastructure.Database;

public sealed class TranslationMemoryService(string databasePath) : ITranslationMemoryService
{
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await db.OpenAsync(ct);
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Memory (
              SourceText TEXT NOT NULL, TranslatedText TEXT NOT NULL, SourceLanguage TEXT NOT NULL,
              TargetLanguage TEXT NOT NULL, GameId TEXT NOT NULL, GameName TEXT NOT NULL, FilePath TEXT NOT NULL,
              Key TEXT NOT NULL, Context TEXT NOT NULL, SourceHash TEXT NOT NULL, Provider TEXT NOT NULL,
              CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
              PRIMARY KEY(SourceHash, SourceLanguage, TargetLanguage, GameId, Context, Provider));
            """;
        await command.ExecuteNonQueryAsync(ct); return db;
    }
    public static string Hash(string text) => TextFiles.Hash(Encoding.UTF8.GetBytes(text));
    public async Task<string?> FindAsync(string text, string sourceLanguage, string targetLanguage, string gameId, string context, string provider, CancellationToken ct)
    {
        await using var db = await Open(ct); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT TranslatedText FROM Memory WHERE SourceHash=$hash AND SourceText=$text AND SourceLanguage=$source AND TargetLanguage=$target AND GameId=$game AND Context=$context AND Provider=$provider";
        foreach (var (key, value) in new[] { ("$hash", Hash(text)), ("$text", text), ("$source", sourceLanguage), ("$target", targetLanguage), ("$game", gameId), ("$context", context), ("$provider", provider) }) cmd.Parameters.AddWithValue(key, value);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
    public async Task SaveAsync(MemoryEntry entry, CancellationToken ct)
    {
        await using var db = await Open(ct); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Memory VALUES($SourceText,$TranslatedText,$SourceLanguage,$TargetLanguage,$GameId,$GameName,$FilePath,$Key,$Context,$SourceHash,$Provider,$CreatedAt,$UpdatedAt)
            ON CONFLICT DO UPDATE SET TranslatedText=excluded.TranslatedText, UpdatedAt=excluded.UpdatedAt;
            """;
        foreach (var property in typeof(MemoryEntry).GetProperties()) cmd.Parameters.AddWithValue("$" + property.Name, property.GetValue(entry)?.ToString() ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

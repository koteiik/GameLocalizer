namespace GameLocalizer.Infrastructure.Database;
public sealed record RuntimeMemoryCandidate(string Text,string Russian,bool Manual,DateTimeOffset UpdatedAt,string TranslationSource="OfflineModel");
public sealed partial class TranslationMemoryService
{
    // Runtime UI is context-independent exact matching. Return all approved candidates so conflicts are visible.
    public Task<IReadOnlyList<RuntimeMemoryCandidate>> ReadRuntimeCandidatesAsync(string gameId,CancellationToken ct) => Run<IReadOnlyList<RuntimeMemoryCandidate>>(db=>
    {
        EnsureRuntimeSourceColumn(db);
        using var command=db.CreateCommand();
        command.CommandText="SELECT SourceText,TranslatedText,IsManual,UpdatedAt,RuntimeTranslationSource FROM TranslationMemory WHERE GameId=$game AND TargetLanguage='ru' AND (IsManual=1 OR TranslationProvider='Offline')";
        command.Parameters.AddWithValue("$game",gameId);
        using var reader=command.ExecuteReader();var result=new List<RuntimeMemoryCandidate>();
        var validator=new GameLocalizer.Core.Validation.TranslationValidator();
        while(reader.Read())if(validator.Validate(reader.GetString(0),reader.GetString(1),out _))
            result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetBoolean(2),DateTimeOffset.Parse(reader.GetString(3)),reader.GetBoolean(2)?"Manual":reader.GetString(4).Length==0?"OfflineModel":reader.GetString(4)));
        return result;
    },ct);
    private static void EnsureRuntimeSourceColumn(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        using var check=db.CreateCommand();check.CommandText="PRAGMA table_info(TranslationMemory)";
        bool found=false;using(var reader=check.ExecuteReader())while(reader.Read())if(reader.GetString(1)=="RuntimeTranslationSource")found=true;
        if(!found){using var add=db.CreateCommand();add.CommandText="ALTER TABLE TranslationMemory ADD COLUMN RuntimeTranslationSource TEXT NOT NULL DEFAULT ''";add.ExecuteNonQuery();}
    }
    public Task UpdateRuntimeAutomaticAsync(string gameId,string original,string russian,string source,CancellationToken ct)=>Run(db=>
    {
        EnsureRuntimeSourceColumn(db);using var command=db.CreateCommand();
        command.CommandText="UPDATE TranslationMemory SET TranslatedText=$russian,RuntimeTranslationSource=$source,UpdatedAt=$now,LastUsedAt=$now WHERE GameId=$game AND SourceText=$original AND TargetLanguage='ru' AND IsManual=0 AND TranslationProvider='Offline'";
        command.Parameters.AddWithValue("$game",gameId);command.Parameters.AddWithValue("$original",original);command.Parameters.AddWithValue("$russian",russian);command.Parameters.AddWithValue("$source",source);command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));return command.ExecuteNonQuery();
    },ct);
}

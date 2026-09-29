using System.Text.Json;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.Database;

public sealed class TranslationJobStore(string directory)
{
    public Task SaveAsync(TranslationJob job, CancellationToken ct)
    {
        if (!Guid.TryParseExact(job.JobId, "N", out _)) throw new InvalidDataException("Invalid job ID");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, job.JobId + ".json");
        return Save();
        async Task Save()
        {
            var temporary = path + ".tmp";
            try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }), ct); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public TranslationJob? FindIncomplete(string gameId)
    {
        if (!Directory.Exists(directory)) return null;
        var latest = Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            try { return JsonSerializer.Deserialize<TranslationJob>(File.ReadAllText(path)); }
            catch (Exception e) when (e is IOException or JsonException) { return null; }
        }).Where(j => j?.GameId == gameId && !j.TestOnly)
            .OrderByDescending(j => j!.StartedAt).FirstOrDefault();
        return latest?.Status == TranslationJobStatus.Completed ? null : latest;
    }
}

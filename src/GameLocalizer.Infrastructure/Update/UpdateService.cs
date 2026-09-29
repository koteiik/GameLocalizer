using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.Update;

public sealed class SettingsService(string directory)
{
    public AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(directory, "settings.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
public sealed class UpdateService
{
    public static bool ValidRepository(string repository) => Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");
    public async Task<(string Version, string Url)?> CheckAsync(string repository, CancellationToken ct)
    {
        if (repository != ReleaseClient.Repository) return null;
        try
        {
            var release = await new ReleaseClient().LatestAsync(ct);
            return release != null && ReleaseClient.IsNewer(release, ApplicationVersion.Label) ? (release.Tag, release.PageUrl) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or InvalidDataException or FormatException) { return null; }
    }
}

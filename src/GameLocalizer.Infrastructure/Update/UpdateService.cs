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
        if (!ValidRepository(repository)) return null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("GameLocalizer/0.1.0");
            using var response = await client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            return Version.TryParse(tag.TrimStart('v'), out var version) && version > new Version(0, 1, 0)
                ? (tag, $"https://github.com/{repository}/releases/tag/{Uri.EscapeDataString(tag)}") : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { return null; }
    }
}

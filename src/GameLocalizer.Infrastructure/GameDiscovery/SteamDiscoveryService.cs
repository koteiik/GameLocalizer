using System.Text.RegularExpressions;
using Microsoft.Win32;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using Microsoft.Extensions.Logging;
namespace GameLocalizer.Infrastructure.GameDiscovery;

public sealed class SteamDiscoveryService(ILogger<SteamDiscoveryService> logger) : IGameDiscoveryService
{
    public Task<IReadOnlyList<Game>> DiscoverAsync(CancellationToken ct) => Task.Run<IReadOnlyList<Game>>(() =>
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
            foreach (var name in new[] { "SteamPath", "InstallPath" })
                if (Registry.GetValue(key, name, null) is string path) roots.Add(path);
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (var root in roots.ToArray())
        {
            ct.ThrowIfCancellationRequested(); var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (var path in ParseLibraries(File.ReadAllText(vdf))) roots.Add(path);
        }
        var games = new List<Game>();
        foreach (var root in roots)
        {
            var apps = Path.Combine(root, "steamapps"); if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var pairs = ParsePairs(File.ReadAllText(manifest));
                    if (!pairs.TryGetValue("appid", out var id) || !pairs.TryGetValue("name", out var name) || !pairs.TryGetValue("installdir", out var dir)) continue;
                    var common = Path.GetFullPath(Path.Combine(apps, "common")) + Path.DirectorySeparatorChar;
                    var path = Path.GetFullPath(Path.Combine(common, dir));
                    if (path.StartsWith(common, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) games.Add(new(id, name, path, "Steam", root));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { logger.LogWarning("Steam manifest skipped: {Type}", e.GetType().Name); }
            }
        }
        logger.LogInformation("Discovered {Count} Steam games", games.Count);
        return games.DistinctBy(g => g.Path, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Name).ToArray();
    }, ct);
    public static Dictionary<string, string> ParsePairs(string text) => Regex.Matches(text, "\"([^\"]+)\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"").GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Replace("\\\\", "\\"), StringComparer.OrdinalIgnoreCase);
    public static IEnumerable<string> ParseLibraries(string text) => Regex.Matches(text, "\"(?:path|[0-9]+)\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"").Select(m => m.Groups[1].Value.Replace("\\\\", "\\")).Where(Path.IsPathRooted);
}

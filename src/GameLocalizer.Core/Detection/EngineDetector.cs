using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Core.Detection;

public sealed class EngineDetector : IEngineDetector
{
    public EngineDetection Detect(string directory, CancellationToken cancellationToken)
    {
        var files = SafeTree.Enumerate(directory, cancellationToken, 4, 20000).Select(x => Path.GetRelativePath(directory, x)).ToArray();
        var candidates = new List<EngineDetection>();
        void Check(EngineType type, Func<string, bool> match)
        {
            var evidence = files.Where(match).Take(12).ToArray();
            if (evidence.Length > 0) candidates.Add(new(type, Math.Min(.99, .7 + evidence.Length * .08), evidence));
        }
        Check(EngineType.Unity, s => s.EndsWith("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase) || s.EndsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase) || s.Split(Path.DirectorySeparatorChar).Any(p => p.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)));
        Check(EngineType.Unreal, s => s.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".locres", StringComparison.OrdinalIgnoreCase) || s.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("Paks", StringComparison.OrdinalIgnoreCase)) || s.Equals("Engine", StringComparison.OrdinalIgnoreCase));
        Check(EngineType.Godot, s => s.EndsWith(".pck", StringComparison.OrdinalIgnoreCase) || s.EndsWith("project.godot", StringComparison.OrdinalIgnoreCase));
        Check(EngineType.RenPy, s => s.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase) || s.EndsWith("renpy", StringComparison.OrdinalIgnoreCase));
        Check(EngineType.RpgMaker, s => s.EndsWith("Game.rpgproject", StringComparison.OrdinalIgnoreCase) || s.EndsWith("rmmz_core.js", StringComparison.OrdinalIgnoreCase) || s.EndsWith("rpg_core.js", StringComparison.OrdinalIgnoreCase));
        return candidates.OrderByDescending(c => c.Confidence).FirstOrDefault() ?? new(EngineType.Unknown, 0, []);
    }
}

public static class SafeTree
{
    public static IEnumerable<string> Enumerate(string root, CancellationToken ct, int maxDepth = 24, int maxEntries = 100000)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); int seen = 0;
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(current.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (++seen > maxEntries) throw new IOException("Превышен лимит количества файлов; выберите меньшую папку.");
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); } catch (IOException) { continue; }
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || Path.GetFileName(entry).Equals("GameLocalizer_Backup", StringComparison.OrdinalIgnoreCase)) continue;
                yield return entry;
                if (attributes.HasFlag(FileAttributes.Directory) && current.Depth < maxDepth) pending.Push((entry, current.Depth + 1));
            }
        }
    }
}

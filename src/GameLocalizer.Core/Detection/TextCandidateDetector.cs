using System.Text.RegularExpressions;
namespace GameLocalizer.Core.Detection;

public sealed class TextCandidateDetector
{
    public double Score(string value, int repetitions = 1, bool isConfiguration = false)
    {
        var s = value.Trim();
        if (s.Length < 2 || s.Length > 4000 || !s.Any(char.IsLetter)) return 0;
        if (isConfiguration && new[] { "true", "false", "null", "yes", "no" }.Contains(s, StringComparer.OrdinalIgnoreCase)) return 0;
        if (Regex.IsMatch(s, @"^(?:Direct3D\w*|Renderer|Vendor|VRAM|GfxDevice|MonoManager|ReloadAssembly|Initialized input|touch support|UnloadTime|FPS|driver info)(?:\b|:)", RegexOptions.IgnoreCase)) return .01;
        if (Regex.IsMatch(s, @"^(https?://|[A-Za-z]:\\)|[/\\].*[/\\]|^[\da-fA-F-]{32,36}$|\b\w+\.\w+\.\w+\b|^m_[A-Za-z]|\.(png|dds|wav|ogg|dll|exe|prefab|asset)$", RegexOptions.IgnoreCase)) return .02;
        if (Regex.IsMatch(s, @"^[\w]+_[\w]+$|^[a-z]+[A-Z]\w*$|^[A-Z][a-z]+[A-Z]\w*$|^[\w]+[/\\][\w/\\.]+$")) return .08;
        var words = Regex.Matches(s, @"\p{L}+").Count;
        var score = words > 1 ? .78 : .52;
        if (char.IsUpper(s[0])) score += .10;
        if (Regex.IsMatch(s, @"[.!?]$")) score += .08;
        if (s.Count(c => "_=|@#^".Contains(c)) > s.Length / 8) score -= .3;
        if (repetitions > 20) score -= .1;
        return Math.Clamp(score, 0, 1);
    }
}

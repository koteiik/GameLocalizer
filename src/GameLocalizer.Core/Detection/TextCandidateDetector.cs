using System.Text.RegularExpressions;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Core.Detection;

public sealed class TextCandidateDetector
{
    public double Score(string value, int repetitions = 1, bool isConfiguration = false, ResourceKind source = ResourceKind.PossibleTextResource, string context = "", string path = "", string key = "")
    {
        var s = value.Trim();
        if (!ResourceClassifier.IsExtractable(source)) return 0;
        if (path.Length != 0 && !ResourceClassifier.IsExtractable(new ResourceClassifier().Classify(path))) return 0;
        isConfiguration |= ResourceClassifier.IsConfiguration(path);
        var configKey = context + " " + key;
        var configurationContext = (isConfiguration || source == ResourceKind.PossibleTextResource && Regex.IsMatch(configKey, @"(?:^|[:./\s_-])(?:font|fontname|encoding|version|regex|regexp|shortcut|hotkey|plugin|enabled|enum|options)(?:$|[:./\s_-])", RegexOptions.IgnoreCase)) && !ResourceClassifier.HasLocalizationDirectory(path);
        if (s.Length < 2 || s.Length > 4000 || !s.Any(char.IsLetter)) return 0;
        if (configurationContext && new[] { "true", "false", "null", "yes", "no" }.Contains(s, StringComparer.OrdinalIgnoreCase)) return 0;
        if (configurationContext && (new[] { "Times New Roman", "Arial", "Segoe UI", "UTF-8", "VERSION" }.Contains(s, StringComparer.OrdinalIgnoreCase) ||
            Regex.IsMatch(s, @"^\[UTILITY\]|\\u[0-9a-f]{4}|;|^(?:v?\d+\.)+\d+|\b(?:Ctrl|Control|Alt|Shift|Shortcut)[+_ -]|^\^|\$$|\\[dwsb]|\(\?", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(configKey, @"(?:^|[:./\s_-])(?:font|fontname|encoding|version|regex|regexp|shortcut|hotkey|plugin|enabled|enum)(?:$|[:./\s_-])", RegexOptions.IgnoreCase))) return .02;
        if (Regex.IsMatch(s, @"\b(?:UnityEngine\.|UnityEditor\.|System\.|Microsoft\.|Mono\.|Il2Cpp\.|Assembly-CSharp\b|mscorlib\b|netstandard\b)|^[\p{L}_][\w]*(?:\.[\p{L}_][\w]*)+(?:\([^)]*\))?$", RegexOptions.IgnoreCase)) return .01;
        if (context is "summary" or "param" or "returns" or "remarks" or "schema" or "$schema") return .05;
        if (Regex.IsMatch(s, @"^(?:Direct3D\w*|Renderer|Vendor|VRAM|GfxDevice|MonoManager|ReloadAssembly|Initialized input|touch support|UnloadTime|FPS|driver info)(?:\b|:)", RegexOptions.IgnoreCase)) return .01;
        if (Regex.IsMatch(s, @"^(https?://|[A-Za-z]:\\)|[/\\].*[/\\]|^[\da-fA-F-]{32,36}$|\b\w+\.\w+\.\w+\b|^m_[A-Za-z]|\.(png|dds|wav|ogg|dll|exe|prefab|asset)$", RegexOptions.IgnoreCase)) return .02;
        if (Regex.IsMatch(s, @"^[\w]+_[\w]+$|^[a-z]+[A-Z]\w*$|^[A-Z][a-z]+[A-Z]\w*$|^[\w]+[/\\][\w/\\.]+$")) return .08;
        if (s.Length > 3 && s.All(c => char.IsUpper(c) || c == '_' || char.IsDigit(c))) return .15;
        var words = Regex.Matches(s, @"\p{L}+").Count;
        var score = words > 1 ? .78 : .52;
        if (char.IsUpper(s[0])) score += .10;
        if (Regex.IsMatch(s, @"[.!?]$")) score += .08;
        if (s.Count(c => "_=|@#^".Contains(c)) > s.Length / 8) score -= .3;
        if (repetitions > 20) score -= .1;
        if (new[] { "Continue", "Play", "Settings", "Options", "Quit", "Exit", "Save", "Load", "Back", "Cancel", "Yes", "No", "Resume", "Inventory", "Map", "Help", "Start", "Attack", "Jump", "Run" }.Contains(s, StringComparer.OrdinalIgnoreCase)) score = Math.Max(score, .90);
        // Source evidence boosts natural text only; paths never rescue identifiers or diagnostics.
        if (source != ResourceKind.PossibleTextResource && score >= .6) score += .05;
        if (configurationContext) score -= .20;
        return Math.Clamp(score, 0, 1);
    }
}

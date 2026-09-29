using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;

namespace GameLocalizer.Core.Localization;

/// <summary>Only RHS spans are writable. Keys, delimiters, comments and physical lines remain opaque original text.</summary>
public sealed class BepInExLocalizationAdapter : SpanAdapter
{
    public override string Name => "BepInEx / XUnity key=value";
    public static bool TranslationPath(string path) => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
        (ResourceClassifier.HasLocalizationDirectory(path) || path.Replace('\\', '/').Split('/').SkipLast(1).Contains("XUnity.AutoTranslator", StringComparer.OrdinalIgnoreCase));
    public static bool AuxiliaryFile(string path) => TranslationPath(path) &&
        (new[] { "_Preprocessors.txt", "_Postprocessors.txt", "_Substitutions.txt" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) ||
         Path.GetFileName(path).EndsWith("resizer.txt", StringComparison.OrdinalIgnoreCase));
    public override bool CanHandle(string path) => TranslationPath(path) && !AuxiliaryFile(path);
    // Plain dialogue TXT files without pair syntax continue using the ordinary text adapter.
    public bool MatchesContent(string text) => Lines(text).Any(line => line.Text.Contains('=')) || Lines(text).All(line => string.IsNullOrWhiteSpace(line.Text) || Comment(line.Text.TrimStart()));
    private static bool Comment(string line) => line.StartsWith('#') || line.StartsWith(';') || line.StartsWith("//", StringComparison.Ordinal);
    private static IEnumerable<(int Number, int Start, string Text)> Lines(string text)
    {
        int start = 0, line = 1;
        while (start < text.Length)
        {
            var end = start; while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            yield return (line++, start, text[start..end]);
            if (end < text.Length && text[end++] == '\r' && end < text.Length && text[end] == '\n') end++;
            start = end;
        }
    }
    private static int Separator(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') return -1;
            if (line[i] == '=') return i;
        }
        return -1;
    }
    private static int CommentStart(string line, int start)
    {
        for (var i = start; i + 1 < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '/' && line[i + 1] == '/') return i;
        }
        return line.Length;
    }
    protected override IReadOnlyList<TextSpan> Parse(string text)
    {
        var result = new List<TextSpan>();
        foreach (var line in Lines(text))
        {
            if (Comment(line.Text.TrimStart())) continue;
            var separator = Separator(line.Text); if (separator < 0) continue;
            var key = line.Text[..separator].Trim(' ', '\t');
            if (key.Length == 0 || key.StartsWith("r:", StringComparison.Ordinal) || key.StartsWith("sr:", StringComparison.Ordinal)) continue;
            var start = separator + 1; var end = CommentStart(line.Text, start);
            while (start < end && line.Text[start] is ' ' or '\t') start++;
            while (end > start && line.Text[end - 1] is ' ' or '\t') end--;
            if (start == end) continue; // No fallback to translating the key when the RHS is empty.
            var value = line.Text[start..end];
            result.Add(new(LocalizationEntryId.KeyValue(line.Number, key), value, line.Start + start, end - start, translated =>
            {
                if (translated.IndexOfAny(['\r', '\n']) >= 0 || translated != translated.Trim(' ', '\t') || CommentStart(translated, 0) != translated.Length ||
                    !new TranslationValidator().Validate(value, translated, out _)) throw new InvalidDataException("Недопустимое значение key=value: изменены escapes, комментарии или границы строки.");
                return translated;
            }));
        }
        return result;
    }
    public override IReadOnlyList<TextEntry> Extract(string text) => Parse(text).Select(s =>
        new TextEntry(LocalizationEntryId.DisplayKey(s.Key), s.Value, "XUnity key-value localization", s.Key)).ToArray();
}

public static class LocalizationAdapterSelector
{
    public static ILocalizationAdapter Select(IEnumerable<ILocalizationAdapter> adapters, string path, string text)
    {
        var all = adapters.ToArray();
        return all.OfType<BepInExLocalizationAdapter>().FirstOrDefault(a => a.CanHandle(path) && a.MatchesContent(text)) as ILocalizationAdapter
            ?? all.First(a => a is not BepInExLocalizationAdapter && a.CanHandle(path));
    }
}

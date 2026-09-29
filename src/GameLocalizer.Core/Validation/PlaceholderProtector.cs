using System.Text.RegularExpressions;
namespace GameLocalizer.Core.Validation;

public sealed class PlaceholderProtector
{
    private static readonly Regex Tokens = new(@"\{\{[^{}]+\}\}|\{[^{}]+\}|%(?:\d+\$)?[-+0 #]*\d*(?:\.\d+)?[sdifouxXeEgGc%]|\\[nrt]|[\n\r\t]|</?[^>\r\n]+>|\[/?[A-Za-z][^\]\r\n]*\]", RegexOptions.Compiled);
    public IReadOnlyList<string> Extract(string text) => Tokens.Matches(text).Select(m => m.Value).ToArray();
    public (string Text, IReadOnlyDictionary<string, string> Tokens) Protect(string text)
    {
        var map = new Dictionary<string, string>();
        var prefix = "__GL_" + Guid.NewGuid().ToString("N") + "_";
        var result = Tokens.Replace(text, m => { var key = prefix + map.Count + "__"; map[key] = m.Value; return key; });
        return (result, map);
    }
    public string Restore(string text, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var (key, value) in tokens)
        {
            if (Regex.Matches(text, Regex.Escape(key)).Count != 1) throw new InvalidDataException("Protected token changed or missing.");
            text = text.Replace(key, value, StringComparison.Ordinal);
        }
        return text;
    }
}

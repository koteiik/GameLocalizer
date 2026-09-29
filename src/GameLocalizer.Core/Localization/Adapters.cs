using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;

namespace GameLocalizer.Core.Localization;

public record TextSpan(string Key, string Value, int Start, int Length, Func<string, string> Encode);
public abstract class SpanAdapter : ILocalizationAdapter
{
    public abstract string Name { get; }
    public abstract bool CanHandle(string path);
    protected abstract IReadOnlyList<TextSpan> Parse(string text);
    public virtual IReadOnlyList<TextEntry> Extract(string text) => Parse(text).Select(s => new TextEntry(s.Key, s.Value)).ToArray();
    public string ApplyTranslations(string text, IReadOnlyDictionary<string, string> translations)
    {
        var spans = Parse(text);
        var keys = spans.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        if (translations.Keys.Any(k => !keys.Contains(k))) throw new InvalidDataException("Unknown translation key");
        var result = new StringBuilder(text.Length); var position = 0;
        foreach (var s in spans.OrderBy(s => s.Start))
        {
            if (!translations.TryGetValue(s.Key, out var value)) continue;
            result.Append(text, position, s.Start - position).Append(s.Encode(value));
            position = s.Start + s.Length;
        }
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }
    public virtual bool Validate(string original, string modified, IReadOnlyDictionary<string, string> translations)
    {
        try
        {
            var a = Extract(original); var b = Extract(modified);
            return ApplyTranslations(original, translations) == modified && a.Count == b.Count && a.Zip(b).All(p =>
                p.First.Id == p.Second.Id && p.First.Key == p.Second.Key && p.Second.Text == translations.GetValueOrDefault(p.First.Id, p.First.Text));
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidDataException or XmlException) { return false; }
    }
}

public sealed class JsonLocalizationAdapter : SpanAdapter
{
    public override string Name => "JSON";
    public override bool CanHandle(string path) => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
    protected override IReadOnlyList<TextSpan> Parse(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var document = JsonDocument.Parse(bytes); // Reject invalid documents before extracting offsets.
        var reader = new Utf8JsonReader(bytes);
        var result = new List<TextSpan>();
        var key = ""; var index = 0; var byteOffset = 0; var charOffset = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName) key = reader.GetString()!;
            if (reader.TokenType != JsonTokenType.String) continue;
            var start = charOffset + Encoding.UTF8.GetCharCount(bytes.AsSpan(byteOffset, (int)reader.TokenStartIndex - byteOffset));
            var length = Encoding.UTF8.GetCharCount(bytes.AsSpan((int)reader.TokenStartIndex, (int)(reader.BytesConsumed - reader.TokenStartIndex)));
            byteOffset = (int)reader.BytesConsumed; charOffset = start + length;
            result.Add(new($"{index++}:{key}", reader.GetString()!, start, length, s => JsonSerializer.Serialize(s)));
        }
        return result;
    }
}

public sealed class XmlLocalizationAdapter : ILocalizationAdapter
{
    public string Name => "XML";
    public bool CanHandle(string path) => Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase);
    private static XDocument Parse(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8_000_000 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static IEnumerable<XText> Nodes(XDocument doc) => doc.DescendantNodes().OfType<XText>().Where(x => !string.IsNullOrWhiteSpace(x.Value));
    public IReadOnlyList<TextEntry> Extract(string text) => Nodes(Parse(text)).Select((n, i) => new TextEntry(i.ToString(), n.Value, n.Parent?.Name.ToString() ?? "")).ToArray();
    public string ApplyTranslations(string text, IReadOnlyDictionary<string, string> translations)
    {
        var doc = Parse(text); var nodes = Nodes(doc).ToArray();
        foreach (var (key, value) in translations)
        {
            if (!int.TryParse(key, out var i) || i < 0 || i >= nodes.Length) throw new InvalidDataException("Unknown XML key");
            nodes[i].Value = value;
        }
        return (doc.Declaration is null ? "" : doc.Declaration + "\n") + doc.ToString(SaveOptions.DisableFormatting);
    }
    public bool Validate(string original, string modified, IReadOnlyDictionary<string, string> translations)
    {
        try { return XNode.DeepEquals(Parse(ApplyTranslations(original, translations)), Parse(modified)); }
        catch (Exception e) when (e is XmlException or InvalidDataException) { return false; }
    }
}

public sealed class CsvLocalizationAdapter(char delimiter = ',') : SpanAdapter
{
    public override string Name => delimiter == '\t' ? "TSV" : "CSV";
    public override bool CanHandle(string path) => Path.GetExtension(path).Equals(delimiter == '\t' ? ".tsv" : ".csv", StringComparison.OrdinalIgnoreCase);
    protected override IReadOnlyList<TextSpan> Parse(string text)
    {
        var spans = new List<TextSpan>(); int i = 0, row = 0, col = 0;
        while (i < text.Length)
        {
            int start = i; var value = new StringBuilder();
            if (text[i] == '"')
            {
                i++; var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        i++;
                        if (i < text.Length && text[i] == '"') { value.Append('"'); i++; }
                        else { closed = true; break; }
                    }
                    else value.Append(text[i++]);
                }
                if (!closed || (i < text.Length && text[i] != delimiter && text[i] != '\r' && text[i] != '\n')) throw new FormatException("Malformed CSV");
            }
            else
            {
                while (i < text.Length && text[i] != delimiter && text[i] != '\r' && text[i] != '\n')
                { if (text[i] == '"') throw new FormatException("Malformed CSV quote"); value.Append(text[i++]); }
            }
            // First row is the header; first column contains immutable IDs.
            if (row > 0 && col > 0) spans.Add(new($"{row}:{col}", value.ToString(), start, i - start, s => "\"" + s.Replace("\"", "\"\"") + "\""));
            if (i == text.Length) break;
            if (text[i] == delimiter) { col++; i++; }
            else { if (text[i++] == '\r' && i < text.Length && text[i] == '\n') i++; row++; col = 0; }
        }
        return spans;
    }
}

public sealed class IniLocalizationAdapter : SpanAdapter
{
    public override string Name => "INI";
    public override bool CanHandle(string path) => new[] { ".ini", ".lang", ".locale", ".loc", ".strings" }.Contains(Path.GetExtension(path).ToLowerInvariant());
    protected override IReadOnlyList<TextSpan> Parse(string text)
    {
        var result = new List<TextSpan>(); var index = 0;
        foreach (Match m in Regex.Matches(text, @"(?m)^[ \t]*([^;#\[\r\n=]+?)\s*=[ \t]*([^\r\n]*)"))
        {
            var g = m.Groups[2]; var raw = g.Value; var end = raw.Length;
            bool quoted = raw.StartsWith('"');
            if (quoted)
            {
                var match = Regex.Match(raw, "^\"((?:\\\\.|[^\"\\\\])*)\"");
                if (!match.Success) throw new FormatException("Invalid INI quoted value");
                var content = match.Groups[1];
                result.Add(new($"{index++}:{m.Groups[1].Value.Trim()}", JsonSerializer.Deserialize<string>(match.Value)!, g.Index, match.Length, s => JsonSerializer.Serialize(s)));
            }
            else
            {
                var comment = Regex.Match(raw, @"\s[;#]"); if (comment.Success) end = comment.Index;
                var value = raw[..end].TrimEnd();
                result.Add(new($"{index++}:{m.Groups[1].Value.Trim()}", value, g.Index, value.Length, s =>
                { if (s.IndexOfAny(['\r', '\n', ';', '#', '"']) >= 0 || s != s.Trim()) throw new InvalidDataException("INI value requires unsupported quoting"); return s; }));
            }
        }
        return result;
    }
}

public sealed class PlainTextLocalizationAdapter : SpanAdapter
{
    public override string Name => "TXT";
    public override bool CanHandle(string path) => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);
    protected override IReadOnlyList<TextSpan> Parse(string text) => Regex.Matches(text, @"[^\r\n]+").Select((m, i) =>
        new TextSpan(i.ToString(), m.Value, m.Index, m.Length, s => { if (s.Contains('\n') || s.Contains('\r')) throw new InvalidDataException("New lines are not allowed"); return s; })).ToArray();
}

public sealed class PoLocalizationAdapter : SpanAdapter
{
    public override string Name => "PO (singular)";
    public override bool CanHandle(string path) => Path.GetExtension(path).Equals(".po", StringComparison.OrdinalIgnoreCase);
    protected override IReadOnlyList<TextSpan> Parse(string text)
    {
        var spans = new List<TextSpan>(); var index = 0;
        const string q = "\"(?:\\\\.|[^\"\\\\])*\"";
        var blocks = Regex.Matches(text, @"(?m)^msgid (?<id>" + q + @"(?:\r?\n" + q + @")*)\r?\nmsgstr (?<str>" + q + @"(?:\r?\n" + q + @")*)");
        string Decode(string s) => string.Concat(Regex.Matches(s, q).Select(m => JsonSerializer.Deserialize<string>(m.Value)));
        foreach (Match block in blocks)
        {
            var source = Decode(block.Groups["id"].Value);
            if (source.Length == 0) continue; // Metadata header.
            var g = block.Groups["str"]; var translated = Decode(g.Value);
            // Extraction uses msgid; msgstr is the only writable span.
            spans.Add(new($"{index++}:{source}", source, g.Index, g.Length, s => JsonSerializer.Serialize(s)));
        }
        return spans;
    }
    // PO extraction intentionally keeps source msgid after application.
    public override bool Validate(string original, string modified, IReadOnlyDictionary<string, string> translations)
        => ApplyTranslations(original, translations) == modified;
}

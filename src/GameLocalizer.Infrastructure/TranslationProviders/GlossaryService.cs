using System.Text;
using System.Text.Json;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Validation;
using GameLocalizer.Infrastructure.Database;
using Microsoft.VisualBasic.FileIO;
namespace GameLocalizer.Infrastructure.TranslationProviders;

public sealed class GlossaryService
{
    private readonly string? storagePath;
    private IReadOnlyList<GlossaryEntry> entries = [];
    public GlossaryService(string? storagePath = null)
    {
        this.storagePath = storagePath;
        if (storagePath != null && File.Exists(storagePath)) entries = Read(storagePath);
    }
    public IReadOnlyList<GlossaryEntry> Entries => entries;
    public string Version => entries.Count == 0 ? "" : TranslationMemoryService.Hash(JsonSerializer.Serialize(entries.OrderBy(e => e.Original, StringComparer.Ordinal).ThenBy(e => e.Category, StringComparer.Ordinal)));
    public string Context(string context, string category) => entries.Any(e => !string.IsNullOrEmpty(e.Category)) ? context + "\nCategory=" + category : context;
    // Exact whole-string terms only: no blind replacement inside inflected Russian sentences.
    public string? Match(string text, string category) => entries.FirstOrDefault(e => (string.IsNullOrEmpty(e.Category) || e.Category == category || e.Category == "Names") &&
        text.Equals(e.Original, e.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))?.Russian;
    public void Save(IReadOnlyList<GlossaryEntry> values)
    {
        Validate(values);
        if (storagePath != null) Write(storagePath, values);
        entries = values.ToArray();
    }
    public void Import(string path) => Save(Read(path));
    public void Export(string path) => Write(path, entries);
    private static void Validate(IReadOnlyList<GlossaryEntry> values)
    {
        if (values.Count > 10000) throw new InvalidDataException("Не более 10000 записей glossary");
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value.Original) || string.IsNullOrWhiteSpace(value.Russian) || value.Original.Length > 4000 || value.Russian.Length > 8000) throw new InvalidDataException("Пустая или слишком длинная запись glossary");
            if (!new TranslationValidator().Validate(value.Original, value.Russian, out var error)) throw new InvalidDataException(error);
        }
        if (values.GroupBy(v => (v.Original.ToUpperInvariant(), v.Category ?? "")).Any(g => g.Count() > 1)) throw new InvalidDataException("Повторяющиеся записи glossary");
    }
    private static IReadOnlyList<GlossaryEntry> Read(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Glossary превышает 8 MiB");
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var values = JsonSerializer.Deserialize<List<GlossaryEntry>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            Validate(values); return values;
        }
        using var parser = new TextFieldParser(path, Encoding.UTF8) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(","); var header = parser.ReadFields();
        if (header == null || !header.SequenceEqual(new[] { "Original", "Russian", "CaseSensitive", "Category" })) throw new InvalidDataException("CSV: ожидается Original,Russian,CaseSensitive,Category");
        var result = new List<GlossaryEntry>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields == null || fields.Length != 4 || !bool.TryParse(fields[2], out var sensitive)) throw new InvalidDataException("Некорректная CSV запись glossary");
            result.Add(new(fields[0], fields[1], sensitive, string.IsNullOrEmpty(fields[3]) ? null : fields[3]));
        }
        Validate(result); return result;
    }
    private static void Write(string path, IReadOnlyList<GlossaryEntry> values)
    {
        var destination = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var text = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? "Original,Russian,CaseSensitive,Category\n" + string.Join("\n", values.Select(e => string.Join(",", new[] { e.Original, e.Russian, e.CaseSensitive.ToString(), e.Category ?? "" }.Select(Quote))))
            : JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

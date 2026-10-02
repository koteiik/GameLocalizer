using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GameLocalizer.Infrastructure.Runtime;

public sealed record RuntimeUiExportRow(string Text, string Scene, string Object, string Hierarchy,
    string Component, string Assembly, long SeenCount, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
    string Match, string Source);
public sealed record RuntimeUiExportResult(int Count, string JsonPath, string TextPath);

/// <summary>Exports the imported snapshot without reading captures or touching analysis data.</summary>
public static class RuntimeUiExport
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GameLocalizer", "Exports");

    public static string SuggestedFileName(string gameName, DateTimeOffset timestamp)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(gameName.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = "Game";
        if (name.Length > 100) name = name[..100];
        return $"GameLocalizer_RuntimeUI_{name}_{timestamp:yyyyMMdd_HHmmss_fff}.json";
    }

    public static RuntimeUiExportRow[] Snapshot(IEnumerable<RuntimeUiEntry> entries) => entries.Select(e =>
        new RuntimeUiExportRow(e.Text, e.Scene, e.Object, e.Hierarchy, e.Component, e.Assembly,
            e.SeenCount, e.FirstSeen, e.LastSeen, e.MatchStatus, e.Source)).ToArray();

    public static RuntimeUiExportResult Write(string selectedPath, IReadOnlyList<RuntimeUiExportRow> rows)
    {
        if (rows.Count == 0) throw new InvalidOperationException("Сначала импортируйте строки Runtime UI.");
        var path = Path.GetFullPath(selectedPath);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".json" or ".txt"))
            throw new ArgumentException("Выберите файл JSON или TXT.", nameof(selectedPath));
        var jsonPath = Path.ChangeExtension(path, ".json");
        var textPath = Path.ChangeExtension(path, ".txt");
        // Prepare both representations before writing either destination.
        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.AppendLine($"Text: {row.Text}").AppendLine($"Scene: {row.Scene}")
                .AppendLine($"Object: {row.Object}").AppendLine($"Hierarchy: {row.Hierarchy}")
                .AppendLine($"Component: {row.Component}").AppendLine($"Assembly: {row.Assembly}")
                .AppendLine($"SeenCount: {row.SeenCount.ToString(CultureInfo.InvariantCulture)}")
                .AppendLine($"FirstSeen: {row.FirstSeen:O}").AppendLine($"LastSeen: {row.LastSeen:O}")
                .AppendLine($"Match: {row.Match}").AppendLine($"Source: {row.Source}")
                .AppendLine().AppendLine("---").AppendLine();
        }
        WriteAtomic(jsonPath, json);
        WriteAtomic(textPath, text.ToString());
        return new(rows.Count, jsonPath, textPath);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class RuntimeUiDataFolder
{
    public static bool TryOpen(string gameRoot, Action<string> openDirectory, out string message)
    {
        try
        {
            var folder = RuntimeCollectorService.DataDirectory(gameRoot);
            if (!Directory.Exists(folder))
            {
                message = "Папка данных Runtime UI Collector ещё не существует. Запустите игру с установленным сборщиком, затем импортируйте строки.";
                return false;
            }
            openDirectory(folder);
            message = folder;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            message = "Не удалось открыть папку данных Runtime UI Collector: " + e.Message;
            return false;
        }
    }
}

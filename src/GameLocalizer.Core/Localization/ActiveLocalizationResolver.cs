using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;

namespace GameLocalizer.Core.Localization;

public record ActiveLocalization(string PhysicalSourceFile, string LocalizationSlot, string AdapterType,
    string Evidence)
{
    public string ConfiguredSlot { get; init; } = "";
}

/// <summary>Resolves an extracted resource, never synthesizes a locale from the target language.
/// Configuration alone cannot prove which fallback/layer supplies a displayed string.</summary>
public sealed class ActiveLocalizationResolver
{
    public static string SlotFromPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        for (var i = 0; i + 1 < parts.Length; i++)
            if (new[] { "Translation", "Translations", "Localization", "Locales", "Languages" }
                .Contains(parts[i], StringComparer.OrdinalIgnoreCase))
            {
                var candidate = parts[i + 1];
                if (candidate.Length is >= 2 and <= 16 && candidate.All(c => char.IsLetter(c) || c is '-' or '_') &&
                    !candidate.Equals("Text", StringComparison.OrdinalIgnoreCase)) return candidate;
            }
        return "Не определён";
    }

    public ActiveLocalization Resolve(string physicalFile, string text, IEnumerable<ILocalizationAdapter> adapters, string configuration = "")
    {
        var adapter = LocalizationAdapterSelector.Select(adapters, physicalFile, text);
        if (!adapter.CanHandle(physicalFile) || adapter.Extract(text).Count == 0)
            throw new InvalidDataException("Не подтверждён поддерживаемый localization resource.");
        var language = configuration.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => !l.StartsWith('#') && !l.StartsWith(';'))
            .Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals("Language", StringComparison.OrdinalIgnoreCase))?[1].Trim() ?? "";
        var slot = SlotFromPath(physicalFile);
        return new(Path.GetFullPath(physicalFile), slot, adapter.Name,
            "Физический файл извлечённых значений; слот из структуры каталогов. " +
            (language.Length == 0 ? "" : $"Config Language={language}; " + (language.Equals(slot, StringComparison.OrdinalIgnoreCase) ? "совпадает с файлом. " : "отличается: возможна fallback/translation layer. ")) +
            "Активность в игре требует проверки пользователем.") { ConfiguredSlot = language };
    }
}

public sealed class FontCompatibilityService
{
    public const string Warning = "Некоторые игры могут не содержать кириллические символы в используемом шрифте.";
}

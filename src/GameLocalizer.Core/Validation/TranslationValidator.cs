namespace GameLocalizer.Core.Validation;

public sealed class TranslationValidator
{
    public bool Validate(string original, string translation, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(translation)) { error = "Пустой перевод"; return false; }
        foreach (var delimiter in "{}<>")
            if (original.Count(c => c == delimiter) != translation.Count(c => c == delimiter))
            { error = "Изменены границы placeholders или markup"; return false; }
        var protector = new PlaceholderProtector();
        var a = protector.Extract(original); var b = protector.Extract(translation);
        if (!a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal)))
        { error = "Изменены placeholders или markup"; return false; }
        // Preserve tag ordering as well as multiplicity; prevents crossing/nesting changes.
        bool Tag(string s) => s.StartsWith('<') || s.StartsWith('[');
        if (!a.Where(Tag).SequenceEqual(b.Where(Tag))) { error = "Нарушен порядок markup"; return false; }
        return true;
    }
}

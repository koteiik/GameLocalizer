using GameLocalizer.Core.Models;
namespace GameLocalizer.Core.Translation;
public sealed class TranslationBatchBuilder
{
    public IEnumerable<IReadOnlyList<TranslationItem>> Build(IReadOnlyList<TranslationItem> items, int size, int maximumCharacters = 6000)
    {
        var batch = new List<TranslationItem>(); var characters = 0;
        foreach (var item in items)
        {
            if (batch.Count != 0 && (batch.Count >= Math.Clamp(size, 1, 40) || characters + item.Text.Length > maximumCharacters))
            { yield return batch.ToArray(); batch.Clear(); characters = 0; }
            batch.Add(item); characters += item.Text.Length;
        }
        if (batch.Count != 0) yield return batch.ToArray();
    }
}

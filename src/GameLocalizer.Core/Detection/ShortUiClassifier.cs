using System.Text.RegularExpressions;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Core.Detection;
public static class ShortUiClassifier
{
    private static readonly HashSet<string> Vocabulary = new("Chat Back Next Previous Close Open Buy Sell Give Take Use Equip Unequip Save Load Settings Options Exit Yes No OK Cancel Apply Reset Advice Talk Item Items Map Status Start Stop Delete Confirm Audio Video Controls Inventory Profile Shop Continue Play Quit Resume Help Attack Jump Run".Split(' '), StringComparer.OrdinalIgnoreCase);
    public static bool HasUiContext(string path) => Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"(?:^|[ _.-])(menu|ui|items?|options|settings|main|character maker|housing|shop)(?:$|[ _.-])", RegexOptions.IgnoreCase);
    public static bool IsShortNatural(string text) => text.Trim().Length is >= 2 and <= 48 && Regex.IsMatch(text.Trim(), @"^\p{L}+(?:[ '\-]\p{L}+){0,5}[!?]?$", RegexOptions.CultureInvariant);
    public static bool HasVocabulary(string text) => Regex.Matches(text, @"\p{L}+").Cast<Match>().Any(m => Vocabulary.Contains(m.Value));
    public static bool IsCandidate(string text, ResourceKind kind, string path, double score) => score >= .35 && IsShortNatural(text) &&
        (kind is ResourceKind.LocalizationCandidate or ResourceKind.UIResource || HasUiContext(path)) && HasVocabulary(text);
}

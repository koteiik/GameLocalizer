namespace GameLocalizer.Core.Models;

public enum TextCategory { UI, Dialogue, Subtitle, Localization, Possible, Technical, Names, Quest, Item, Story, ShortUI, UnsupportedUI }
public record ScanEntry(string FilePath, string Key, string Original, string Context, double Confidence, bool Selected, TextCategory Category = TextCategory.Possible)
{
    public string PhysicalSourceFile { get; init; } = "";
    public string LocalizationSlot { get; init; } = "";
    public string AdapterType { get; init; } = "";
}
public record ScanProgress(long FilesVisited, long Candidates, long Processed, long SkippedFiles, long Selected = 0);
public record ScanBatch(Resource Resource, string? SourceHash, IReadOnlyList<ScanEntry> Entries, ScanProgress Progress);
public record ScanRow(long Id, string FilePath, string Key, string Original, string Translation, string Context,
    double Confidence, bool Selected, string Status, TextCategory Category = TextCategory.Possible)
{
    public string StableRowId { get; init; } = "";
    public bool ManualEdit { get; init; }
    public bool Applied { get; init; }
    public string TranslationSource { get; init; } = "";
    public string PhysicalSourceFile { get; init; } = "";
    public string LocalizationSlot { get; init; } = "";
    public string AdapterType { get; init; } = "";
    public string SourceText => Original;
    public string TranslatedText => Translation;
    public string DisplayKey => LocalizationEntryId.DisplayKey(Key);
}
public record ScanEdit(long Id, string Translation, bool Selected, TranslationStatus? Status = null);
public enum ScanSort { Id, Original, Translation, FilePath, Key, Confidence, Status }
public record ScanQuery(string Search = "", string File = "", string Status = "Все", double MinimumConfidence = 0,
    ScanSort Sort = ScanSort.Id, bool Descending = false, string Filter = "Все");
public record ScanPage(IReadOnlyList<ScanRow> Rows, long Total, long Matching, long Selected, long UserText = 0, long Doubtful = 0, long Technical = 0, long Ui = 0, long ShortUi = 0, long UiSelected = 0, long MissedUi = 0);
public record ScannedFile(string FilePath, string SourceHash);
public record ApplySelectionSummary(long SelectedEntries, IReadOnlyList<ScanRow> EmptyTranslationRows, long AppliedEntries, long ValidationErrorEntries)
{
    public long SkippedEmptyTranslations => EmptyTranslationRows.Count;
    public string Description => $"Выбрано: {SelectedEntries:N0}\nБудет применено: {AppliedEntries:N0}\nПустых пропущено: {SkippedEmptyTranslations:N0}\nValidation errors: {ValidationErrorEntries:N0}";
}
public record UnsupportedUiCandidate(string Text, string SourceFile, string ProbableResourceType, string MatchKind = "Candidate")
{
    public bool Writable => false;
    public string SupportStatus => "Unsupported · Источник пока не поддерживается для записи.";
}

namespace GameLocalizer.Core.Models;

public enum TextCategory { UI, Dialogue, Subtitle, Localization, Possible, Technical }
public record ScanEntry(string FilePath, string Key, string Original, string Context, double Confidence, bool Selected, TextCategory Category = TextCategory.Possible);
public record ScanProgress(long FilesVisited, long Candidates, long Processed, long SkippedFiles, long Selected = 0);
public record ScanBatch(Resource Resource, string? SourceHash, IReadOnlyList<ScanEntry> Entries, ScanProgress Progress);
public record ScanRow(long Id, string FilePath, string Key, string Original, string Translation, string Context,
    double Confidence, bool Selected, string Status, TextCategory Category = TextCategory.Possible);
public record ScanEdit(long Id, string Translation, bool Selected);
public enum ScanSort { Id, Original, Translation, FilePath, Key, Confidence, Status }
public record ScanQuery(string Search = "", string File = "", string Status = "Все", double MinimumConfidence = 0,
    ScanSort Sort = ScanSort.Id, bool Descending = false, string Filter = "Все");
public record ScanPage(IReadOnlyList<ScanRow> Rows, long Total, long Matching, long Selected, long UserText = 0, long Doubtful = 0, long Technical = 0);
public record ScannedFile(string FilePath, string SourceHash);

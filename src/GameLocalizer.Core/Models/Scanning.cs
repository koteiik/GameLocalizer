namespace GameLocalizer.Core.Models;

public record ScanEntry(string FilePath, string Key, string Original, string Context, double Confidence, bool Selected);
public record ScanProgress(long FilesVisited, long Candidates, long Processed, long SkippedFiles, long Selected = 0);
public record ScanBatch(Resource Resource, string? SourceHash, IReadOnlyList<ScanEntry> Entries, ScanProgress Progress);
public record ScanRow(long Id, string FilePath, string Key, string Original, string Translation, string Context,
    double Confidence, bool Selected, string Status);
public record ScanEdit(long Id, string Translation, bool Selected);
public enum ScanSort { Id, Original, Translation, FilePath, Key, Confidence, Status }
public record ScanQuery(string Search = "", string File = "", string Status = "Все", double MinimumConfidence = 0,
    ScanSort Sort = ScanSort.Id, bool Descending = false);
public record ScanPage(IReadOnlyList<ScanRow> Rows, long Total, long Matching, long Selected);
public record ScannedFile(string FilePath, string SourceHash);

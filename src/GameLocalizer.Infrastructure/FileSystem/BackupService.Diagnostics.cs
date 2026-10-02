using System.Text.Json;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Detection;

namespace GameLocalizer.Infrastructure.FileSystem;

public sealed class ApplyDiagnosticReport
{
    public long SelectedEntries { get; set; }
    public long AppliedEntries => EntriesVerifiedOnDisk;
    public long ValidationErrorEntries { get; set; }
    public List<object> SkippedEmptyRows { get; set; } = [];
    public int SkippedEmptyTranslations => SkippedEmptyRows.Count;
    public bool VerboseTrace { get; set; }
    public string Stage { get; set; } = "Preparation";
    public string Status { get; set; } = "FAILED";
    public string Error { get; set; } = "";
    public string Root { get; set; } = "";
    public string ActiveLocalizationSource { get; set; } = "UNKNOWN";
    public string ActiveLocalizationMismatch { get; set; } = "UNKNOWN";
    public List<object> ActiveLocalizationDetection { get; set; } = [];
    public List<ApplyFileDiagnostic> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int FilesExpectedToChange => Files.Count;
    public int FilesActuallyChanged => Files.Count(f => f.BeforeHash != f.AfterHash && f.AfterHash.Length > 0);
    public int EntriesExpected => Files.Sum(f => f.Entries.Count(e => e.Selected));
    public int EntriesVerifiedOnDisk => Files.Sum(f => f.Entries.Count(e => e.Selected && e.ValidationStatus == "PASS"));
    public int BackupsVerified => Files.Count(f => f.BackupMatchesOriginal);
    public int DuplicateKeyConflicts => Files.Sum(f => f.Entries.Count(e => e.CompetingFiles.Count > 0));
    public int PotentialOverrideConflicts => DuplicateKeyConflicts;
    public string Summary => $"Apply status: {Status}\nПрименено: {AppliedEntries} строк. Пропущено пустых: {SkippedEmptyTranslations}.\nSelectedEntries: {SelectedEntries}\nValidationErrorEntries: {ValidationErrorEntries}\nFiles expected to change: {FilesExpectedToChange}\nFiles actually changed: {FilesActuallyChanged}\nEntries expected: {EntriesExpected}\nEntries verified on disk: {EntriesVerifiedOnDisk}\nBackup verified: {BackupsVerified}/{FilesExpectedToChange}\nActive localization mismatch: {ActiveLocalizationMismatch}\nDuplicate key conflicts: {DuplicateKeyConflicts}\nPotential override conflicts: {PotentialOverrideConflicts}\n{Error}\n{string.Join("\n", Files.Where(f => f.Error.Length > 0).Select(f => f.PhysicalTargetFile + ": " + f.Error))}";
}
public sealed class ApplyFileDiagnostic
{
    public string PhysicalSourceFile { get; set; } = "";
    public string PhysicalTargetFile { get; set; } = "";
    public string SamePhysicalFile => PhysicalSourceFile.Equals(PhysicalTargetFile, StringComparison.OrdinalIgnoreCase) ? "YES" : "NO";
    public bool ExistsBefore { get; set; }
    public long SizeBefore { get; set; }
    public long SizeAfter { get; set; }
    public string BeforeHash { get; set; } = "";
    public string AfterHash { get; set; } = "";
    public DateTime LastWriteBefore { get; set; }
    public DateTime LastWriteAfter { get; set; }
    public string Encoding { get; set; } = "";
    public string BOM { get; set; } = "";
    public string LineEndings { get; set; } = "";
    public string AdapterType { get; set; } = "";
    public string LocalizationSlot { get; set; } = "";
    public bool ParseSuccess { get; set; }
    public string BackupPath { get; set; } = "";
    public bool BackupExists { get; set; }
    public string BackupSHA256 { get; set; } = "";
    public string OriginalSHA256 { get; set; } = "";
    public bool BackupMatchesOriginal { get; set; }
    public string Error { get; set; } = "";
    public int ChangedEntries => Entries.Count(e => e.Original != e.Russian && e.ValidationStatus == "PASS");
    public int UnchangedEntries => Entries.Count(e => e.Original == e.Actual);
    public List<ApplyEntryDiagnostic> Entries { get; set; } = [];
}
public sealed class ApplyEntryDiagnostic
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string Original { get; set; } = "";
    public string Russian { get; set; } = "";
    public string Actual { get; set; } = "";
    public string PhysicalSourceFile { get; set; } = "";
    public string PhysicalTargetFile { get; set; } = "";
    public string AdapterType { get; set; } = "";
    public string LocalizationSlot { get; set; } = "";
    public string Category { get; set; } = "Unknown";
    public bool Selected { get; set; }
    public string ValidationStatus { get; set; } = "NOT VERIFIED";
    public List<object> CompetingFiles { get; set; } = [];
    public string PotentialOverrideConflict => CompetingFiles.Count > 0 ? "YES" : "NO";
}

public sealed partial class BackupService
{
    public bool DiagnosticApplyTrace { get; set; } = true;
    public string? LastDiagnosticReportPath { get; private set; }
    public ApplyDiagnosticReport? LastDiagnosticReport { get; private set; }
    private static readonly ILocalizationAdapter[] DiagnosticAdapters = [new BepInExLocalizationAdapter(), new JsonLocalizationAdapter(), new XmlLocalizationAdapter(), new CsvLocalizationAdapter(), new CsvLocalizationAdapter('\t'), new IniLocalizationAdapter(), new PoLocalizationAdapter(), new PlainTextLocalizationAdapter()];
    public async Task ApplyAsync(string root, IReadOnlyList<FileChange> changes, CancellationToken ct, ApplySelectionSummary? selection = null)
    {
        if (changes.All(c => c.AdapterType.Length == 0)) { await ApplyCoreAsync(root, changes, ct); return; }
        var report = new ApplyDiagnosticReport { Root = Path.GetFullPath(root), VerboseTrace = DiagnosticApplyTrace };
        AddSelection(report, selection);
        LastDiagnosticReport = report; LastDiagnosticReportPath = null;
        var snapshots = new Dictionary<string, (TextFile Snapshot, ILocalizationAdapter Adapter, Dictionary<string, string> Expected)>();
        try
        {
            await DetectActiveAsync(report, changes, ct);
            foreach (var change in changes)
            {
                var path = Resolve(root, change.RelativePath);
                var file = new ApplyFileDiagnostic { PhysicalSourceFile = path, PhysicalTargetFile = path, AdapterType = change.AdapterType, LocalizationSlot = change.LocalizationSlot, ExistsBefore = File.Exists(path) };
                report.Files.Add(file);
                if (!file.ExistsBefore) throw new IOException("Target path missing: " + path);
                var snapshot = await TextFiles.ReadAsync(path, ct);
                file.BeforeHash = snapshot.Hash; file.SizeBefore = new FileInfo(path).Length; file.LastWriteBefore = File.GetLastWriteTimeUtc(path);
                file.Encoding = snapshot.Encoding.WebName; file.BOM = Convert.ToHexString(snapshot.Preamble);
                var endings = new List<string>(); if (snapshot.Text.Contains("\r\n")) endings.Add("CRLF");
                var remainder = snapshot.Text.Replace("\r\n", ""); if (remainder.Contains('\n')) endings.Add("LF"); if (remainder.Contains('\r')) endings.Add("CR");
                file.LineEndings = endings.Count == 0 ? "None" : string.Join("+", endings);
                var adapter = LocalizationAdapterSelector.Select(DiagnosticAdapters, path, snapshot.Text);
                var expectedText = snapshot.Encoding.GetString(change.Content.AsSpan(snapshot.Preamble.Length));
                var expected = adapter.Extract(expectedText).ToDictionary(e => e.Id, e => e.Text);
                foreach (var entry in adapter.Extract(snapshot.Text))
                {
                    var value = expected.GetValueOrDefault(entry.Id, entry.Text);
                    file.Entries.Add(new() { Id = entry.Id, Key = entry.Key, Original = entry.Text, Russian = value, Selected = change.SelectedEntryIds.Count > 0 ? change.SelectedEntryIds.Contains(entry.Id) : value != entry.Text, PhysicalSourceFile = path, PhysicalTargetFile = path, AdapterType = adapter.Name, LocalizationSlot = change.LocalizationSlot, Category = change.EntryCategories.GetValueOrDefault(entry.Id, "Unknown") });
                }
                var selected = file.Entries.Where(e => e.Selected).ToDictionary(e => e.Id, e => e.Russian);
                snapshots.Add(path, (snapshot, adapter, selected));

            }
            report.Stage = "Write";
            var changed = changes.Where(c => c.ExpectedHash != TextFiles.Hash(c.Content)).ToArray();
            if(changed.Length > 0) await ApplyCoreAsync(root, changed, ct);
            report.Stage = "Verification";
            await VerifyOwnershipAsync(root,changes.ToDictionary(c => c.RelativePath,c => TextFiles.Hash(c.Content)),ct);
        }
        catch (Exception e) { report.Error = e.Message; throw; }
        finally
        {
            foreach (var file in report.Files)
            {
                try
                {
                    if (!snapshots.TryGetValue(file.PhysicalTargetFile, out var source)) continue;
                    var disk = await TextFiles.ReadAsync(file.PhysicalTargetFile, CancellationToken.None);
                    file.AfterHash = disk.Hash; file.SizeAfter = new FileInfo(file.PhysicalTargetFile).Length; file.LastWriteAfter = File.GetLastWriteTimeUtc(file.PhysicalTargetFile);
                    var actual = source.Adapter.Extract(disk.Text).ToDictionary(e => e.Id, e => e.Text);
                    file.ParseSuccess = source.Adapter.Validate(source.Snapshot.Text, disk.Text, source.Expected);
                    foreach (var entry in file.Entries) { entry.Actual = actual.GetValueOrDefault(entry.Id, "<MISSING>"); entry.ValidationStatus = actual.ContainsKey(entry.Id) && entry.Actual == entry.Russian ? "PASS" : "FAIL"; }
                    var manifestPath = Path.Combine(root, "GameLocalizer_Backup", "manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
                        foreach (var record in manifest.RootElement.GetProperty("Files").EnumerateArray())
                        {
                            if (!Resolve(root, record.GetProperty("RelativePath").GetString()!).Equals(file.PhysicalTargetFile, StringComparison.OrdinalIgnoreCase)) continue;
                            file.BackupPath = Path.Combine(root, "GameLocalizer_Backup", record.GetProperty("ObjectName").GetString()!);
                            file.OriginalSHA256 = record.GetProperty("OriginalHash").GetString()!;
                            file.BackupExists = File.Exists(file.BackupPath);
                            if (file.BackupExists) file.BackupSHA256 = TextFiles.Hash(await File.ReadAllBytesAsync(file.BackupPath));
                            file.BackupMatchesOriginal = file.BackupExists && file.BackupSHA256 == file.OriginalSHA256;
                        }
                    }
                    if (!file.ParseSuccess || file.Entries.Any(e => e.Selected && e.ValidationStatus != "PASS") || !file.BackupMatchesOriginal || file.AfterHash != TextFiles.Hash(changes.First(c => Resolve(root, c.RelativePath) == file.PhysicalTargetFile).Content))
                        file.Error = "Disk/parse/value/backup/output verification failed";
                }
                catch (Exception e) { file.Error = e.Message; }
            }
            if (selection == null) report.SelectedEntries = report.EntriesExpected;
            await DetectDuplicatesAsync(report);
            report.Status = report.Error.Length == 0 && report.Files.Count == changes.Count && report.Files.All(f => f.Error.Length == 0 && f.ParseSuccess) ? "SUCCESS" : report.FilesActuallyChanged > 0 ? "PARTIAL" : "FAILED";
            if(report.Status == "SUCCESS") report.Stage = "Completed";
            await SaveDiagnosticReportAsync(report);
        }
        if (report.Status != "SUCCESS") throw new IOException(report.Summary);
    }
    private static void AddSelection(ApplyDiagnosticReport report, ApplySelectionSummary? selection)
    {
        if (selection == null) return;
        report.SelectedEntries = selection.SelectedEntries; report.ValidationErrorEntries = selection.ValidationErrorEntries;
        report.SkippedEmptyRows = selection.EmptyTranslationRows.Select(r => (object)new { r.Id, Key = r.DisplayKey, r.Original, File = r.FilePath, Reason = "EmptyTranslation" }).ToList();
    }
    public async Task RecordNoWriteApplyAsync(string root, ApplySelectionSummary selection)
    {
        var report = new ApplyDiagnosticReport { Root = Path.GetFullPath(root), Status = "NO_CHANGES" }; AddSelection(report, selection);
        LastDiagnosticReport = report; await SaveDiagnosticReportAsync(report);
    }
    public async Task RecordPreparationFailureAsync(string root, Exception error, ApplySelectionSummary? selection = null)
    {
        var report = new ApplyDiagnosticReport { Root = Path.GetFullPath(root), Error = "Apply preparation failed: " + error.Message, VerboseTrace = DiagnosticApplyTrace };
        AddSelection(report, selection);
        LastDiagnosticReport = report;
        await SaveDiagnosticReportAsync(report);
    }
    private async Task SaveDiagnosticReportAsync(ApplyDiagnosticReport report)
    {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLocalizer", "Logs", "ApplyDiagnostics");
            Directory.CreateDirectory(directory);
            var name = "ApplyDiagnostic_" + string.Concat(Path.GetFileName(report.Root).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..8];
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), json);
            LastDiagnosticReportPath = Path.Combine(directory, name + ".txt");

        var details = new System.Text.StringBuilder(report.Summary);
        details.AppendLine("\nActive Localization Detection").AppendLine("Active localization source: " + report.ActiveLocalizationSource);
        foreach (var file in report.Files)
        {
            details.AppendLine("\nExtracted from: " + file.PhysicalSourceFile).AppendLine("Written to: " + file.PhysicalTargetFile)
                .AppendLine("Same physical file: " + file.SamePhysicalFile).AppendLine("SHA256 before: " + file.BeforeHash).AppendLine("SHA256 after: " + file.AfterHash)
                .AppendLine("SHA256 changed: " + (file.BeforeHash != file.AfterHash ? "YES" : "NO"))
                .AppendLine("Backup path: " + file.BackupPath).AppendLine("Backup exists: " + file.BackupExists).AppendLine("Backup SHA256: " + file.BackupSHA256)
                .AppendLine("Original SHA256: " + file.OriginalSHA256).AppendLine("Backup matches original: " + (file.BackupMatchesOriginal ? "YES" : "NO"))
                .AppendLine("Parse success: " + file.ParseSuccess).AppendLine("Error: " + file.Error);
            foreach (var entry in file.Entries.Where(e => report.VerboseTrace || e.Selected))
                details.AppendLine("\nKey: " + entry.Key).AppendLine("Expected: " + entry.Key + "=" + entry.Russian)
                    .AppendLine("Actual after disk read: " + entry.Key + "=" + entry.Actual).AppendLine("Status: " + entry.ValidationStatus)
                    .AppendLine("Duplicate key detected: " + (entry.CompetingFiles.Count > 0 ? "YES" : "NO"))
                    .AppendLine("Potential override conflict: " + entry.PotentialOverrideConflict);
        }
        details.AppendLine("\nStructured diagnostics:").Append(json);
        await File.WriteAllTextAsync(LastDiagnosticReportPath!, details.ToString());
    }
    private static async Task DetectActiveAsync(ApplyDiagnosticReport report, IReadOnlyList<FileChange> changes, CancellationToken ct, ApplySelectionSummary? selection = null)
    {
        var engine = new EngineDetector().Detect(report.Root, ct);
        report.ActiveLocalizationDetection.Add(new { DetectedEngine = engine.EngineType.ToString(), engine.Confidence, Evidence = engine.DetectedEvidence, BepInExFound = Directory.Exists(Path.Combine(report.Root, "BepInEx")), XUnityFound = Directory.Exists(Path.Combine(report.Root, "BepInEx/plugins/XUnity.AutoTranslator")) });
        var configs = new[] { "BepInEx/config/AutoTranslatorConfig.ini", "BepInEx/AutoTranslatorConfig.ini", "AutoTranslatorConfig.ini" };
        var resolvedPaths = new List<string>();
        foreach (var relative in configs)
        {
            var path = Resolve(report.Root, relative); if (!File.Exists(path)) continue;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (await TextFiles.ReadAsync(path, ct)).Text.Split(['\r', '\n']))
            {
                var trimmed = line.Trim(); if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
                var pair = trimmed.Split('=', 2); if (pair.Length == 2) values[pair[0].Trim()] = pair[1].Trim();
            }
            var language = values.GetValueOrDefault("Language", ""); var template = values.GetValueOrDefault("Directory", "");
            var basePath = relative.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) ? Path.Combine(report.Root, "BepInEx") : report.Root;
            string? resolved = null;
            if (language.Length > 0 && template.Length > 0 && !template.Replace("{Lang}", language).Contains('{'))
                resolved = Path.GetFullPath(Path.Combine(basePath, template.Replace("{Lang}", language).Replace('/', Path.DirectorySeparatorChar)));
            if (resolved != null) resolvedPaths.Add(resolved);
            report.ActiveLocalizationDetection.Add(new { DetectedEngine = Directory.Exists(Path.Combine(report.Root, "BepInEx")) ? "Unity (BepInEx evidence)" : "UNKNOWN", DetectedAdapter = "BepInEx / XUnity", DetectedLocalizationSystem = "XUnity AutoTranslator config", ConfigPath = path, Language = language, FromLanguage = values.GetValueOrDefault("FromLanguage", "UNKNOWN"), Directory = template, OutputFile = values.GetValueOrDefault("OutputFile", "UNKNOWN"), ResolvedTargetDirectory = resolved ?? "UNKNOWN", DetectedActiveSlot = language, Confidence = "Configuration evidence only; runtime source UNKNOWN", Evidence = "AutoTranslatorConfig.ini FOUND; runtime loading not observed" });
        }
        foreach (var translationRoot in new[] { Path.Combine(report.Root, "BepInEx/Translation"), Path.Combine(report.Root, "Translation") })
        {
            if (!Directory.Exists(translationRoot)) continue;
            var folders = SafeTree.Enumerate(translationRoot, ct).Where(Directory.Exists).Where(p => Path.GetDirectoryName(p) == translationRoot).ToArray();
            report.ActiveLocalizationDetection.Add(new { TranslationRoot = translationRoot, EnExists = Directory.Exists(Path.Combine(translationRoot, "en")), RuExists = Directory.Exists(Path.Combine(translationRoot, "ru")), ExistingTranslationFolders = folders.Select(p => new { Path = p, TranslationTxtCount = SafeTree.Enumerate(p, ct).Count(f => File.Exists(f) && Path.GetExtension(f).Equals(".txt", StringComparison.OrdinalIgnoreCase)) }).ToArray() });
        }
        if (resolvedPaths.Count == 1)
        {
            report.ActiveLocalizationMismatch = changes.Any(c => BepInExLocalizationAdapter.TranslationPath(Resolve(report.Root, c.RelativePath)) && !Resolve(report.Root, c.RelativePath).StartsWith(resolvedPaths[0].TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ? "YES" : "NO";
            if (report.ActiveLocalizationMismatch == "YES") report.Warnings.Add("ACTIVE PATH MISMATCH: modified resource differs from configured localization directory.");
        }
    }
    private static async Task DetectDuplicatesAsync(ApplyDiagnosticReport report)
    {
        try
        {
            var adapter = new BepInExLocalizationAdapter();
            foreach (var path in SafeTree.Enumerate(report.Root, CancellationToken.None).Where(p => File.Exists(p) && adapter.CanHandle(p)))
            {
                try
                {
                    var entries = adapter.Extract((await TextFiles.ReadAsync(path, CancellationToken.None)).Text);
                    foreach (var file in report.Files)
                        foreach (var entry in file.Entries.Where(e => e.Selected))
                            foreach (var other in entries.Where(e => e.Key == entry.Key && (!path.Equals(file.PhysicalTargetFile, StringComparison.OrdinalIgnoreCase) || e.Id != entry.Id)))
                                entry.CompetingFiles.Add(new { File = path, other.Id, Value = other.Text });
                }
                catch (Exception e) { report.Warnings.Add("Duplicate check incomplete: " + path + ": " + e.Message); }
            }
        }
        catch (Exception e) { report.Warnings.Add("Duplicate check incomplete: " + e.Message); }
    }
}

using System.Text;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.FileSystem;
public record UiResourceSearchHit(string Text, string SourceFile, string ProbableResourceType, bool Writable, string MatchKind)
{
    public string SupportStatus => Writable ? "Supported" : "Unsupported · Источник пока не поддерживается для записи.";
}
/// <summary>Read-only raw-string evidence. Compressed bundles and serialized object types are not parsed.</summary>
public sealed class UiResourceDiscovery
{
    private readonly IEnumerable<ILocalizationAdapter> adapters;
    private readonly ScanDiagnosticLog diagnostics;
    private readonly Func<string, CancellationToken, Func<string, bool>, Task<IReadOnlyList<string>>>? candidateReader;
    public UiResourceDiscovery(IEnumerable<ILocalizationAdapter> adapters) : this(adapters, null, null) { }
    public UiResourceDiscovery(IEnumerable<ILocalizationAdapter> adapters, ScanDiagnosticLog? diagnostics, Func<string, CancellationToken, Func<string, bool>, Task<IReadOnlyList<string>>>? candidateReader = null)
    { this.adapters = adapters; this.diagnostics = diagnostics ?? new(); this.candidateReader = candidateReader; }
    public UnityDiscoveryStatistics Statistics { get; private set; } = new();
    public string? LastDiagnosticLogPath => diagnostics.LastLogPath;
    private Task<IReadOnlyList<string>> StringsAsync(string path, CancellationToken ct, Func<string,bool> accept) => candidateReader?.Invoke(path,ct,accept) ?? BinaryStringCandidates.ReadAsync(path,ct,accept,Statistics);
    private void RecordFailure(string path, string stage, Exception error)
    {
        Statistics.Errors.Add(new(path,stage,error.Message)); Statistics.Resources.Add(new(path,"Unsupported / discovery error")); diagnostics.Record(path,stage,error);
    }
    public const int ResultLimit = 2000;
    public string Limitations => "Read-only: bounded string candidates (max 1024 bytes/run, 64 MiB/file), UTF-8 validation and ASCII UTF-16LE. Binary bundle containers/compression are Unsupported and not decoded. Serialized type unverified. NO match does not prove absence. Results limited to 2000.";
    private static bool UnityResource(string path) => Path.GetExtension(path).ToLowerInvariant() is ".assets" or ".bundle" ||
        path.Replace('\\', '/').Split('/').Any(p => p.Equals("AssetBundle", StringComparison.OrdinalIgnoreCase) || p.Equals("AssetBundles", StringComparison.OrdinalIgnoreCase));
    public async Task<IReadOnlyList<UnsupportedUiCandidate>> DiscoverAsync(string root, CancellationToken ct)
    {
        Statistics = new();
        var result = new List<UnsupportedUiCandidate>();
        foreach (var file in SafeTree.Enumerate(root, ct).Where(p => File.Exists(p) && UnityResource(p)))
        {
            try
            {
                Statistics.ResourcesExamined++;
                foreach (var text in await StringsAsync(file, ct, text => ShortUiClassifier.IsShortNatural(text) && ShortUiClassifier.HasVocabulary(text) && new TextCandidateDetector().Score(text, source: ResourceKind.UIResource) >= .85))
                {
                    result.Add(new(text, file, "Unity raw string candidate · TextAsset/MonoBehaviour/table type unverified")); if (result.Count >= ResultLimit) return result;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { RecordFailure(file, "Unity read-only discovery / bounded candidate extraction", e); }
        }
        return result;
    }
    public async Task<IReadOnlyList<UiResourceSearchHit>> SearchAsync(string root, string term, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];
        Statistics = new(); term = term.Trim(); var result = new List<UiResourceSearchHit>();
        foreach (var resource in new ResourceScanner(adapters).Enumerate(root, ct).Where(r => r.Editable))
        {
            try
            {
                var snapshot = await TextFiles.ReadAsync(resource.Path, ct); var adapter = LocalizationAdapterSelector.Select(adapters, resource.Path, snapshot.Text);
                foreach (var entry in adapter.Extract(snapshot.Text).Where(e => e.Text.Contains(term, StringComparison.OrdinalIgnoreCase) || e.Key.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new(entry.Text, resource.Path, adapter.Name, true, entry.Text.Contains(term, StringComparison.Ordinal) || entry.Key.Contains(term, StringComparison.Ordinal) ? "Exact" : "Case-insensitive")); if (result.Count >= ResultLimit) return result;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException or System.Xml.XmlException or DecoderFallbackException) { RecordFailure(resource.Path, "Supported text resource search", e); }
        }
        foreach (var file in SafeTree.Enumerate(root, ct).Where(p => File.Exists(p) && UnityResource(p)))
        {
            try
            {
                Statistics.ResourcesExamined++;
                foreach (var text in await StringsAsync(file, ct, value => value.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new(text, file, "Unity raw string candidate · serialized type unverified", false, text.Contains(term, StringComparison.Ordinal) ? "Exact" : "Case-insensitive")); if (result.Count >= ResultLimit) return result;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { RecordFailure(file, "Unity read-only discovery / bounded candidate extraction", e); }
        }
        return result;
    }
}

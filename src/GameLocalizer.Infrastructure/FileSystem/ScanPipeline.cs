using System.Runtime.CompilerServices;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.Core.Localization;
using Microsoft.Extensions.Logging;

namespace GameLocalizer.Infrastructure.FileSystem;

public sealed class ScanPipeline(ResourceScanner scanner, IEnumerable<ILocalizationAdapter> adapters, ILogger<ScanPipeline> logger)
{
    public const int BatchSize = 1000;
    public async IAsyncEnumerable<ScanBatch> ScanAsync(string root, [EnumeratorCancellation] CancellationToken ct)
    {
        long files = 0, processed = 0, candidates = 0, skipped = 0, selected = 0;
        var detector = new TextCandidateDetector();
        // Enumeration/extraction is consumed on a worker by the UI; only one bounded file is parsed at once.
        foreach (var resource in scanner.Enumerate(root, ct))
        {
            ct.ThrowIfCancellationRequested(); files++;
            TextFile? snapshot = null; IReadOnlyList<TextEntry> entries = [];
            var scannedResource = resource;
            if (resource.Editable)
            {
                try
                {
                    snapshot = await TextFiles.ReadAsync(resource.Path, ct);
                    var adapter = LocalizationAdapterSelector.Select(adapters, resource.Path, snapshot.Text);
                    scannedResource = resource with { Format = adapter.Name, Kind = adapter is BepInExLocalizationAdapter ? ResourceKind.LocalizationCandidate : resource.Kind };
                    entries = adapter.Extract(snapshot.Text);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException or System.Xml.XmlException or System.Text.DecoderFallbackException)
                { skipped++; logger.LogWarning("Skipped scan resource {File}: {Type}", Path.GetFileName(resource.Path), e.GetType().Name); }
            }
            var frequencies = entries.GroupBy(e => e.Text).ToDictionary(g => g.Key, g => g.Count());
            var relative = Path.GetRelativePath(root, resource.Path);
            var isConfiguration = ResourceClassifier.IsConfiguration(relative);
            var buffer = new List<ScanEntry>(BatchSize);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested(); processed++;
                var sourcePath = ResourceClassifier.IsModPath(root) || BepInExLocalizationAdapter.TranslationPath(resource.Path) ? resource.Path : relative;
                var confidence = detector.Score(entry.Text, frequencies[entry.Text], isConfiguration, scannedResource.Kind, entry.Context, sourcePath, entry.Key);
                // Retain rejected strings for the explicit technical audit filter, never for translation.
                if (!string.IsNullOrWhiteSpace(entry.Text))
                {
                    candidates++;
                    var category = confidence < .35 ? TextCategory.Technical : ResourceClassifier.Category(scannedResource.Kind);
                    var autoSelected = ResourceClassifier.CanAutoSelect(confidence, category);
                    if (autoSelected) selected++;
                    buffer.Add(new(relative, entry.Id, entry.Text, entry.Context, confidence, autoSelected, category));
                }
                if (processed % BatchSize != 0) continue;
                yield return new(scannedResource, snapshot?.Hash, buffer.ToArray(), new(files, candidates, processed, skipped, selected));
                buffer.Clear();
            }
            yield return new(scannedResource, snapshot?.Hash, buffer.ToArray(), new(files, candidates, processed, skipped, selected));
        }
    }
}

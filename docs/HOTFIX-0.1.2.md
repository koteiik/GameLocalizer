# Hotfix v0.1.2 validation

## Failure and change

API documentation sentences in Unity XML were scored as ordinary English. The scan now classifies the source before extracting text, prunes runtime directories, recognizes documentation XML and rejects qualified symbols regardless of localization path priority. English phrase beginnings are not globally blacklisted: real dialogue beginning with Returns, Gets and The maximum stays eligible.

Automatic selection requires confidence >=0.85. Values from 0.60 to 0.84 are shown unselected; lower nontechnical confidence is available through Doubtful. Technical audit rows cannot be selected by the view model or persisted edits, and selected-row queries exclude them. Preview includes source Category and dataset-wide filters/counters. Excluded directories are represented by their roots in the resource catalog; their contents are not read.

## Synthetic result

The committed UnitySynthetic fixture contains exactly 24 source text values:

| Source | Technical values excluded from selection | Player-facing selected | Doubtful, unselected |
|---|---:|---:|---:|
| Managed API XML | 4 | 0 | 0 |
| Mono runtime TXT | 1 | 0 | 0 |
| Documentation XML outside Managed | 2 | 0 | 0 |
| Localization JSON | 5 | 3 | 0 |
| Dialogue CSV | 0 | 4 | 0 |
| Subtitles TXT | 0 | 3 | 0 |
| notes.txt | 0 | 0 | 2 |
| **Total** | **12** | **10** | **2** |

Seven technical values are excluded with their source before extraction. Five are detected in an eligible JSON resource and retained only as nonselectable technical audit rows. Therefore the application counters correctly show 17 extracted / 10 player-facing / 2 doubtful / 5 technical / 10 selected, not 24 extracted. The regression test independently counts the known fixture's excluded values to verify the total of 12; production scanning does not read excluded files to invent counts.

## Local checks

- .NET 8 Release build: PASS, no warnings/errors.
- Windows tests: 154 passed, 0 failed, 0 skipped; includes the existing >100,000-row paging/cancellation, backup/restore, translation memory and WPF selection regressions.
- Synthetic test validates all five filters, source categories, documentation structure, assembly adjacency, runtime pruning (including selecting Managed as root), conservative selection and persisted-edit protection.
- Self-contained win-x64 publish: PASS; GameLocalizer.exe version 0.1.2.0 and both .NET/WindowsDesktop runtimes present.
- Actual published WPF window inspected on Windows: v0.1.2 title, synthetic scan counters, Category column, normal preview and Technical filter. All five technical checkboxes were disabled. Bulk-selection protection is also verified by the WPF view-model regression test.

The classifier remains heuristic. Real text in excluded directories needs a specialized adapter; unknown formats and protected archives remain unsupported. The provider remains Mock.

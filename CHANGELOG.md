# Changelog

## [Unreleased]

### Added
### Changed
### Fixed

## [0.2.2]

### Added
- Stable semantic-version checks against the official GitHub Releases API, nonmodal update banner, About checks and in-app release notes.
- Cancellable streamed ZIP download with byte progress, mandatory GitHub SHA256 digest and strict asset/redirect validation.
- Separate self-contained updater, per-file package manifest, traversal/link protection, installation backup, staged directory replacement and startup rollback.
- Durable recovery journal, one-time installation-specific update notice, free-space/write-access checks and busy-operation blocking.
- SQLite backup before legacy Translation Memory migration; preserve downloaded models, user data and existing offline translation architecture.
- Updater unit/regression tests and documented recovery procedure. Release ZIP includes Updater/ and update-manifest.json.

## [0.2.1]

### Fixed
- Classify BepInEx, MelonLoader, Mods, modloader and translator/launcher configurations as ModInfrastructure or ToolConfiguration, never automatic translation sources.
- Continue traversing mod directories to retain known localization formats under explicit Translation/Translations/Localization/Language/Text directories.
- Reject fonts, escape/regex values, option lists, shortcuts and tool metadata in configuration context; lower confidence for INI outside explicit localization directories.
- AutoSelect now requires confidence >=85% AND UI/Dialogue/Subtitle/Localization/Quest/Item/Story source category. Possible remains visible but unselected.
- Include high-confidence Possible rows in doubtful counters/filters; preserve explicit manual selection and the v0.2.0 offline translation architecture.
- Add synthetic regression coverage for mod configuration exclusions, localization exceptions, root selection and automatic translation boundaries.

## [0.2.0]

### Added
- Real offline EN→RU OPUS-MT INT8 translation and separate SHA256-verified model management.
- Isolated ONNX Runtime worker, DirectML attempt, CPU fallback, adaptive batches and automatic unload.
- Persistent model/version/glossary-aware translation memory with protected manual overrides.
- Preflight counts, test 20 rows, resumable job metadata, explicit retranslation and cancellation.
- Whole-string CSV/JSON glossary and Names category, protected placeholders and markup.
- Hardware/device/batch/cache settings and application-plus-worker RAM reporting.
- Offline regression tests and an opt-in real-model smoke tool; CI never downloads weights.

### Fixed
- Native GPU runtime crashes cannot terminate WPF.
- Model download progress uses a one-way WPF binding.

## [0.1.2]

### Fixed
- Prune Unity Managed, Mono, IL2CPP, Plugins/Native and runtime metadata directories before localization traversal.
- Detect .NET/Unity XML documentation by structure and assembly adjacency; exclude it before extracting text.
- Reject qualified symbols, serialization identifiers, API-context summaries, diagnostics and assembly/package metadata.
- Require confidence >=85% for automatic selection; localization paths cannot promote technical identifiers.
- Prevent technical audit rows from being selected, translated or applied, including through persisted edits.

### Added
- Dialogue, Subtitle, UI, TechnicalDocumentation, AssemblyMetadata and EngineRuntime resource categories.
- Preview Category column and global All/Selected/High confidence/Doubtful/Technical filters alongside existing status filters.
- Global total/player-text/doubtful/technical/selected counters with documented semantics.
- Synthetic Unity fixture and regression tests for context-aware filtering, runtime pruning and WPF selection/filter behavior.

## [0.1.1]

### Fixed
- Repeated game selection after engine/status updates: replaced mutable record equality with stable reference identity.
- Explicit two-way selection; refresh preserves selected object references, and stale asynchronous results cannot update another game.
- Removed the 50,000-row exception and the aggregate filesystem entry ceiling from normal scanning.
- Excluded logs, technical resource files, configuration boolean/null flags and engine diagnostics by default.
- Removed quadratic JSON offset calculation and repeated whole-string replacement work in structured adapters.

### Added
- Streaming batches of at most 1,000 entries into a temporary SQLite ScanResultRepository.
- 2,000-row pages with global search/filter/sort, recycling virtualization and found/shown/selected counters.
- Full-dataset translation/application with off-page edit persistence, source hash validation and disk-staged output.
- Synthetic 120,005-row sample generator and Windows/WPF regression tests.

## [0.1.0]

### Added
- Initial MVP
- Steam discovery, manual folders, engine evidence and bounded text scanning.
- Structured resource adapters and editable WPF preview.
- Offline Mock translation, placeholder validation and SQLite memory.
- Durable original backups, safe file replacement and restore.
- Windows build and tagged release workflows.

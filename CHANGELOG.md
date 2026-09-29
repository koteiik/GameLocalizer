# Changelog

## [Unreleased]

### Added
### Changed
### Fixed

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

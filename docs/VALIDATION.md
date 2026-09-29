# Local validation — v0.1.0

- Windows x64, .NET SDK 8.0.425.
- Release build: passed, zero warnings/errors.
- Unit/integration tests: 53 passed, zero skipped/failed.
- Self-contained win-x64 publish: passed.
- ZIP inspection: 481 entries, executable and runtime libraries present.
- Published executable launched successfully; dark WPF interface visually inspected.
- Manual folder dialog successfully added the synthetic sample directory and selected it in the UI.
- Steam discovery found 18 installed entries on the test machine (no game files modified).
- Synthetic Demo UI run: engine identified as Unity, 20 rows extracted, Mock preview populated, seven files backed up and applied.
- SHA256 comparison confirmed preview did not alter files and UI Restore returned all seven original files to exactly their original bytes.
- Automated safety checks cover placeholders/markup, six adapters, immutable originals across repeated applies, stale source hashes, corrupted backups, external changes on restore, traversal/executable rejection, cancellation, encoding, contextual SQLite persistence and batch deduplication.

GitHub repository creation, remote Actions, tag and release are pending GitHub authentication. This document does not claim those checks passed. ZIP/package and logs are local artifacts excluded from Git.

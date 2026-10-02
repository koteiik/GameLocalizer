# Portable application updates

Starting with **v0.2.2**, GameLocalizer checks the latest stable release of `koteiik/GameLocalizer` when the startup setting is enabled. **О программе → Проверить обновления** also checks manually. A banner offers **Обновить**, **Что нового** and **Позже**. Release notes are displayed as plain text inside the application.

Install v0.2.2 manually once if upgrading from v0.2.1 or earlier: those versions do not include this updater. Extract the complete ZIP into a writable application folder outside `%LOCALAPPDATA%/GameLocalizer`. Subsequent releases can be installed with the update button. No administrator elevation or installer is used.

## Download and validation

Portable updates select `GameLocalizer-Portable-x64.zip` (legacy `GameLocalizer-win-x64.zip` is also accepted); installed updates select `GameLocalizer-Setup-x64.exe` or the legacy installer name. The release must have a stable canonical `vX.Y.Z` tag and be newer than the running version. Drafts and prereleases are ignored. The remote asset URL must match the official repository exactly; only HTTPS is allowed. GitHub's signed `release-assets.githubusercontent.com/github-production-release-asset/` delivery redirect is explicitly allowed. No arbitrary CDN or update feed is accepted.

The ZIP is streamed into `%LOCALAPPDATA%/GameLocalizer/Updates/vX.Y.Z/`, with progress in MB and cancellation. Its size and SHA256 must match the GitHub API asset digest. Missing digest blocks installation. Failed/cancelled downloads remove their partial file. The installer rechecks the digest using the same locked file handle used for extraction.

The ZIP manifest lists every payload file's path, length and SHA256. Validation rejects traversal, absolute paths, streams, links, duplicate Windows paths, unexpected files, mismatched versions and missing executable/runtime files. Limits are 512 MiB compressed, 2 GiB expanded and 10,000 entries. Models and user-data paths are forbidden in packages. SHA256 checks integrity against the official GitHub release; binaries are currently unsigned.

## Installation and recovery

1. Finish scan/translation/apply/restore/model download. Update is disabled while these operations run. Save manual edits, persist settings and unload the local model before handoff.
2. Check write access, free space and the complete payload before closing. Copy the self-contained `Updater/` directory to `Updates/Runner/<transaction>/`.
3. The isolated updater checks PID and start time, waits for the old process to exit, and rejects another running instance in the same installation.
4. Copy the installation to `Updates/Backup/<old-version>/<transaction>/`. Build a sibling staging directory from the old installation and overlay the verified new payload. Unknown portable files retain their bytes.
5. Rename the old installation to `<installation>.gl-previous-<transaction>`, then rename staging to the original installation path. Durable JSON records each phase in `Updates/Transactions/`.
6. Start the new EXE and wait up to 60 seconds for its version/path-checked startup acknowledgement and two additional seconds of process survival. Show the update notice once for this installation. Failure restores the previous directory and attempts to restart it. A failed new directory is retained for diagnosis.

The two directory renames are **not a single power-loss-atomic operation**. If Windows or the updater is terminated between them, original bytes remain in the previous directory and external backup. After restarting Windows, run the matching saved runner from a terminal:

```powershell
& "$env:LOCALAPPDATA\GameLocalizer\Updates\Runner\<transaction>\GameLocalizer.Updater.exe" --recover "$env:LOCALAPPDATA\GameLocalizer\Updates\Transactions\<transaction>.json"
```

Use the transaction ID from the interrupted operation; close other instances first. Recovery is idempotent and completed transactions are not rolled back. If recovery cannot rename a locked directory, the error is shown and the backup remains available. Startup confirmation detects immediate startup failure, not every possible later application error.

Backups, staged payloads and runner copies are deliberately retained; allow roughly two old installations plus two expanded packages in the data volume and one staged installation in the installation volume. Old recovery directories may be removed manually after confirming the update and retaining at least the latest backup. No automatic backup garbage collection is included.

## Data preservation

The installer rejects overlap between its installation and `%LOCALAPPDATA%/GameLocalizer`. It does not replace memory.db, Models, Settings, Glossary, logs, translation jobs or game backup directories. Normal application startup may append logs/create a scan cache. No model is downloaded or removed by an update. Before upgrading an older Translation Memory schema, SQLite's backup API creates `memory.db.migration-v2-<id>.bak`; this includes committed WAL data. v0.2.2 keeps the v0.2.0/v0.2.1 memory schema and offline model architecture.

## Building a release package

```powershell
dotnet publish src/GameLocalizer.UI -c Release -r win-x64 --self-contained true -o artifacts/publish
./scripts/Package.ps1 -PublishDirectory artifacts/publish -Version 0.2.2 -ZipPath artifacts/GameLocalizer-Portable-x64.zip
```

The publish target includes the model host and self-contained updater. The packaging script adds documentation, creates the per-file manifest and ZIP. GitHub's release workflow uses the same script. The API supplies the final ZIP digest after upload.

See [distribution layout and migration](INSTALLATION-LAYOUT.md) for internal paths and the one-time manual transition required by already published clients.

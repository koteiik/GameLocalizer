# Installation layout

The Windows x64 self-contained publish, installer and portable package share this layout:

```text
GameLocalizer.exe
app/
  GameLocalizer.dll
  GameLocalizer.ModelHost.exe
  native and managed runtime dependencies
  RuntimeCollector/GameLocalizer.RuntimeCollector.dll
  Updater/GameLocalizer.Updater.exe
  README.md, LICENSE, THIRD_PARTY_NOTICES.md
  update-manifest.json (portable)
  install-files.txt, installation.ini, installer-payload.json, unins000.* (installer)
```

The SDK `CreateAppHost` task builds the root executable with `app/GameLocalizer.dll` as its managed entry point. Its working directory is irrelevant. WPF, SQLite and ONNX retain standard .NET probing from the managed base directory, `app/`. ModelHost stays isolated and updater is copied outside the installation before a transaction. Collector binaries are copied into the selected game's plugin directory by the existing installation service.

Single-file publishing is not enabled. [Microsoft's deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) describes native-library extraction when bundling native dependencies. ModelHost and updater would still require separate processes and payloads. Keeping the ordinary self-contained runtime inside `app/` avoids a new extraction lifecycle.

PDBs, native import libraries and `createdump.exe` are removed from production publish output. Models, databases, settings, glossary, logs, artwork and runtime dictionaries remain under `%LOCALAPPDATA%/GameLocalizer`; no data migration is performed.

Run `./scripts/dev-install.ps1 -Launch` from the repository root. It builds, tests, publishes and copies only manifest-owned application files, retaining a backup. Its local installation manifest lives inside `app/`; it is not included in distribution packages.

The installer reads both legacy root manifests and current `app/` manifests. Portable updater validates both package layouts, keeps unknown files and backs up the complete previous installation; obsolete files are removed only when the previous package manifest lists them and their hashes still match. Rollback restores the previous layout.

Future public releases contain only `GameLocalizer-Setup-x64.exe`, `GameLocalizer-Portable-x64.zip` and `SHA256SUMS.txt`. Installer inventory and validation reports remain internal CI artifacts. This client accepts both legacy and current official asset names. Already published v0.5.0 clients cannot recognize future renamed assets: install the next installer manually once, or extract the portable package into a fresh directory. No release or tag is created by this maintenance change.

# GameLocalizer

Open-source Windows application for creating Russian localizations for PC games.

**Project is in early development.** Version 0.1.0 is a conservative text-resource MVP. It does not automatically translate arbitrary games. The bundled Mock provider is a demonstration dictionary, not an AI translator.

## Features
- Steam library discovery and manual folder selection.
- Engine evidence and confidence; bounded resource scanning with cancellation.
- Editable preview with search, sorting, status/file/confidence filters and selection.
- Structured JSON/XML/CSV/TSV/INI/singular PO/plain-text adapters.
- Placeholder protection and validation before applying translations.
- Contextual batches, SQLite translation memory, immutable original backups, SHA256 validation and restore.
- Russian WPF interface, local logs, optional GitHub release checks.

## Installation
Windows 10/11 x64. Once a release is published: **Releases → Latest → GameLocalizer-win-x64.zip**. Extract all files and run `GameLocalizer.exe`. The ZIP contains the .NET runtime; Visual Studio and a separate runtime installation are not required. This early build is unsigned.

## How It Works
1. Select a discovered Steam game or choose **Добавить игру вручную**.
2. **Анализировать** identifies the engine and lists resources, including formats needing future adapters.
3. **Найти текст** extracts likely player-visible strings. No files are modified.
4. **Перевести (Mock)** translates selected empty rows. Edit the Russian column; double-click a cell to edit it.
5. Review selection, placeholders and errors. Filters hide rows but do not change their selection. **Выбрать видимые / Снять видимые** operate only on visible rows.
6. Close the game, then **Применить**. All selected rows, including hidden rows, must have valid translations.
7. **Восстановить** returns every backed-up file to its original bytes. Keep the backup folder.

Try a **copy** of `samples/SampleGames/Demo` first. Samples are synthetic and contain no commercial game assets.

## Supported Launchers
Steam (registry, default installation and VDF libraries); manual folders. Epic, GOG and Xbox are future integrations through `IGameDiscoveryService`.

## Supported Engines
Unity, Unreal, Godot, Ren'Py and RPG Maker are recognized heuristically; unsupported or ambiguous games may appear as Unknown. Detection does not imply engine archive support. The MVP operates on accessible text files only.

## Supported Formats
| Format | Scope |
|---|---|
| JSON | String values, nested objects and arrays; keys/numbers/booleans preserved |
| XML | Text nodes and CDATA; attributes are never translated; DTD prohibited |
| CSV / TSV | Header row and first ID column preserved; remaining cells, including quoted multiline fields |
| INI / .lang / .locale / .loc / .strings | Only INI-style `key=value` syntax; sections/comments retained |
| PO | Singular msgid/msgstr and multiline strings; headers/context metadata untouched; plural entries not translated |
| TXT | Nonempty lines; original line endings preserved |
| YAML / YML / RPY | Detected only; no write adapter in v0.1 |

UTF-8 (with/without BOM), UTF-16 and UTF-32 with BOM are supported. Invalid UTF-8/unknown legacy encodings are skipped rather than guessed. XML serialization may normalize its declaration/formatting while preserving parsed structure. XML attributes containing localizable text require a future schema-aware adapter. Binary archives and executables are never translated.

## Translation Providers
Mock is implemented and offline. A small dictionary returns Russian text; unknown strings get `[ДЕМО]` followed by the original text. These are **not real translations** and require editing. `ITranslationProvider` accepts ID-preserving contextual batches and cannot write game files. OpenAI/DeepL can be implemented independently; they are not offered as working providers. No API keys are requested or stored in v0.1.

Translation memory lives in `%LOCALAPPDATA%/GameLocalizer/memory.db` and scopes exact matches by game, source/target language, context and provider. It stores extracted text, not whole game files. Manual edits are applied directly; v0.1 does not add them to provider memory.

## Safety
- No DRM/anti-cheat bypass, process injection, network interception, executable patching or protected archive modification.
- Always close the game before applying/restoring. A changed file hash aborts the operation.
- Original bytes live in `GameLocalizer_Backup` inside the game folder. Originals are never overwritten. Manifest records paths, hashes and UTC timestamps.
- New bytes are written to a unique temporary file, flushed, verified and atomically replaced on the same volume. A durable manifest is written before replacement.
- Operations are serialized per game. Reparse points and paths outside the game root are rejected.
- Cancellation/crash may leave a subset of files applied; each is recoverable through Restore. Restore preflights every backup and refuses files modified by another program.
- Do not delete backups. After an external game update, restore before updating where possible; stale originals are not silently applied to a new game version.
- Logs are stored in `%LOCALAPPDATA%/GameLocalizer/logs`; no credentials or translated text are logged. Paths may reveal personal information: inspect logs before sharing.

## Known Limitations
Heuristic text selection can miss text or include configuration strings. Review every selected row. Limits: 4 MiB per text file, 100,000 filesystem entries, scan depth 24, 50,000 preview rows. Permissions and game-specific parsers may prevent editing. CSV assumes a header and ID column. This is not a universal game localizer. No OCR, overlay, engine binary adapters, installer or automatic update installation. Repository links/update checks require an actual `owner/GameLocalizer` in Settings until a published repository is configured.

## Roadmap
See [ROADMAP.md](ROADMAP.md). The roadmap is guidance, not a commitment.

## Building From Source
Install the .NET 8 SDK on Windows; Visual Studio is optional.

```powershell
dotnet restore GameLocalizer.sln
dotnet build GameLocalizer.sln -c Release --no-restore
dotnet test GameLocalizer.sln -c Release --no-build
dotnet publish src/GameLocalizer.UI -c Release -r win-x64 --self-contained true -o artifacts/publish
Compress-Archive -Path artifacts/publish/* -DestinationPath artifacts/GameLocalizer-win-x64.zip -Force
```

GitHub Actions builds/tests on pushes and pull requests. Pushing a `v*` tag also publishes a self-contained win-x64 ZIP and creates a GitHub Release only after tests pass.

## Contributing
Read [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). MIT licensed. Submit only artificial test fixtures, never game assets, credentials or personal databases.

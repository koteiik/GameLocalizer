# GameLocalizer

Open-source Windows application for creating Russian localizations for PC games.

**Project is in early development.** Version 0.2.0 adds real offline English → Russian translation with a separately downloaded OPUS-MT INT8 model, persistent translation memory and resumable jobs. Review every translation before applying it.

[Repository](https://github.com/koteiik/GameLocalizer) · [Releases and downloads](https://github.com/koteiik/GameLocalizer/releases) · [Report a problem](https://github.com/koteiik/GameLocalizer/issues)

## Features
- Steam library discovery and manual folder selection.
- Engine evidence and confidence; bounded resource scanning with cancellation.
- Editable 2,000-row pages with full-dataset search, sorting, status/file/confidence filters and selection.
- Streaming batches and a temporary SQLite scan cache; no 50,000-row aggregate limit.
- Unity runtime directories, assembly metadata, API documentation XML, logs and engine diagnostics excluded before translation.
- Source categories, conservative automatic selection (confidence ≥85%), doubtful/technical audit filters and global counters.
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
4. In **Настройки**, select **Offline** and download the model once (108.5 MiB). Click **Перевести**, review the preflight counts and choose **Тест 20 строк** or start the job. Double-click a Russian cell to edit it; manual edits persist and override machine translations.
5. Review selection, placeholders and errors. Use **← Назад / Далее →** to change pages. Search, filters and the sorting selector operate on the entire database. **Выбрать страницу / Снять страницу** change only the loaded page; other pages keep their selection. Category identifies the source. Automatic selection requires confidence ≥85%; 60–84% is visible but unselected. **Все** hides technical rows and confidence <60%; **Сомнительные** shows nontechnical rows below 85%, and **Технические** shows rejected values found inside otherwise eligible resources. These two audit filters ignore the confidence slider. Technical rows cannot be selected. Status, search and file filters still apply.
6. Close the game, then **Применить**. Translation and application process all selected rows across all pages, including rows hidden by filters. Every selected translation must be valid. Manual edits are flushed to the scan cache before paging or applying.
7. **Восстановить** returns every backed-up file to its original bytes. Keep the backup folder.

Try a **copy** of `samples/SampleGames/Demo` first. For a large scan, run `samples/SampleGames/LargeSynthetic/Generate.ps1` to create 120,005 rows in `artifacts/large-synthetic`. Use `samples/SampleGames/UnitySynthetic` for the Unity filtering regression fixture (12 technical values excluded from selection, 10 player-facing values selected, 2 uncertain words unselected). Samples are synthetic and contain no commercial game assets.

Counters cover extracted values across all pages: **Пользовательский текст** means high confidence (≥85%), **Сомнительные** means lower-confidence nontechnical values, and **Технические** means rejected values. Their sum equals **Найдено всего**. Excluded runtime directories are never traversed and documentation XML is not extracted; their unknown string counts are not invented or included. See the resource catalog for excluded sources.

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
**Offline** uses the specialized OPUS-MT EN→RU Marian model through ONNX Runtime in a private local worker process. Auto/GPU attempts DirectML and falls back to CPU. No CUDA, API key, account or inference server is needed. Only initial model download and optional update checks need the Internet. Model files are SHA256 checked and excluded from the application ZIP. Existing installations retain their provider setting: switch from Mock to Offline after upgrading.

Translation memory in `%LOCALAPPDATA%/GameLocalizer/memory.db` persists across restarts and scopes machine matches by game, languages, context, provider, model/version, glossary version and category. Manual edits always win, including explicit retranslation. Unchanged cached text never loads the model. Cancellation saves completed batches; scan again and resume remaining rows. The model unloads after a job by default. A whole-string CSV/JSON glossary includes a Names category; it does not replace words inside sentences.

Mock remains a demonstration dictionary (`[ДЕМО]` for unknown phrases), not a real translator. No OCR, overlay, game monitoring or background retranslation is included. See [Offline Translation](docs/OFFLINE-TRANSLATION.md) for setup, model sources, lifecycle, smoke checks and limitations. INT8 greedy output needs review; GPU compatibility is driver-dependent and CPU fallback was used on the tested machine.

**Перевод выполняется один раз и сохраняется. Повторный запуск игры не запускает модель и не расходует ресурсы на повторный перевод.**

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
Heuristic text selection can miss text or include configuration strings. Review every selected row. **Scan limits and display limits are separate:** scanning has no aggregate row limit; parsing retains a 4 MiB per-file safety limit and directory depth 24. The UI holds at most 2,000 preview rows and 2,000 prioritized catalog entries. The full scan result is queried from a per-process SQLite cache under `%LOCALAPPDATA%/GameLocalizer/scans`, removed on normal exit; scan previews are temporary, not a saved project. Translation memory and backups remain persistent. A crash can leave an unused scan cache. Logs, binaries and known technical files are listed but not extracted. Managed/Mono/IL2CPP/assembly/metadata directories and Unity *_Data Plugins/Native directories are pruned; the catalog shows excluded directory roots, not their contents. XML beside assemblies and XML with doc/members/member API structure are excluded. Games storing real dialogue in excluded directories need a future specialized adapter. Heuristics cannot prove a string is player-visible. CSV assumes a header and ID column. This is not a universal game localizer. No OCR, overlay, engine binary adapters, installer or automatic update installation. Repository links and optional update checks use `koteiik/GameLocalizer` by default; forks can change this in Settings.

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

# Architecture and recovery

Core contains models, service contracts, engine/text detection, localization adapters and validation. Infrastructure implements discovery, bounded file access, durable backups, SQLite memory, translation orchestration, logging/settings and release checks. UI contains WPF Views, ViewModels and commands; MainWindow code-behind only initializes the view.

`IGameDiscoveryService`, `IEngineDetector`, `ILocalizationAdapter` and `ITranslationProvider` are dependency-injected extension points. A future UnityPlugin/UnrealPlugin/RenPyPlugin/GodotPlugin/RPGMakerPlugin can register implementations without modifying Core. v0.1 does not load arbitrary external assemblies or promise a stable binary plugin ABI.

Analysis → resource classification → adapter extraction → heuristic score → editable preview → grouped translation with protected placeholders → validation → immutable original backup → journal → verified temporary write → atomic replacement.

Provider requests contain only selected strings, IDs and context. Provider implementations have no file-write dependency. SQLite exact-match lookup uses SHA256 plus source equality and contextual keys. Batch duplicates are consolidated. Future real API providers must implement `ITranslationProvider`, retrieve credentials through `ISecretStore` backed by Windows Credential Manager or DPAPI, and never log secrets. No external API provider/secret persistence is needed by Mock.

The recovery manifest is committed before game file replacement and retains every hash written by GameLocalizer. A crash between journal and replacement leaves either the original or a known version. Originals are independent immutable objects with checked SHA256. Restore preserves the backup objects for repeated recovery. Multi-file operations are recoverable, not globally atomic. Failure/cancellation can leave some files applied; Restore covers all journaled files. External changes cause refusal rather than data loss. Users should close games and launchers during modifications; there is no process locking or filesystem adversary guarantee.

Future Live Translate: `IScreenCaptureService` → text regions → `ITextRecognitionService` → translation memory/provider → `IOverlayService`. No OCR dependencies, game hooks or overlays are shipped in v0.1.

# UI coverage and empty Apply

ShortUI recognizes ordinary short labels with UI vocabulary in supported localization/UI resources and UI filename contexts. Boosts run only after technical guards. BepInEx Translation/Text sources provide evidence; no translations are hardcoded. Scan counters and filters include UI, Short UI, Localization, missed UI and selected empty translations.

Unsupported Unity UI discovery is read-only. It walks .assets, .bundle and AssetBundle(s) trees, streams UTF-8/ASCII and ASCII UTF-16LE raw strings, and displays candidates with unverified serialized types. It does not decompress bundles or parse Unity serialized objects. TextAsset/MonoBehaviour/table identification is a possible source, not a proven type. NO results never prove that runtime or compressed UI is absent. Results are capped at 2000 and these limits are displayed in the diagnostic tabs. Exact/case-insensitive term search also parses existing supported writable resources. No binary writer was added.

Apply scans the entire selected dataset for null/empty/Unicode-whitespace translations. The modal choices are Да, Показать and Нет. Да excludes empty rows only from the current Apply; Показать opens a paginated empty-selected view; Нет cancels without altering selection/filter. All-empty input generates a NO_CHANGES report without backups/writes. Genuine validation errors remain blocking and have a FAILED diagnostic report. Empty values are never written or saved to memory; existing good memory is retained.

After Apply, Preview retains selected rows and manually cleared values. A new text analysis is required before another Apply because source hashes have changed; this follows the existing safety requirement. The summary and TXT/JSON report include SelectedEntries, AppliedEntries, SkippedEmptyTranslations, ValidationErrorEntries and per-empty-row reasons. Staging, adapter validation, path safety, backup hashes and disk reread remain in place.

Regression cases use synthetic AI Shoujo-style Japanese keys with Chat, Give Advice and Give Item. They do not establish where a particular installed game's compressed or runtime-generated UI is stored.

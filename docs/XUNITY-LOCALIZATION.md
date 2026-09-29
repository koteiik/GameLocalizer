# BepInEx / XUnity key-value localization

Version 0.2.3 recognizes `.txt` pair files in Translation/Translations and other explicit localization directories, including BepInEx and XUnity.AutoTranslator trees. Plain dialogue TXT files without pair syntax retain their line-based adapter. Mod configuration, preprocessors, postprocessors, substitutions and resizer files remain excluded.

`ありがとう = Thank you.` appears in Preview and Test 20 as **Key: ありがとう**, **Original: Thank you.**, **Russian: Спасибо.** Only the RHS is translated. The local locator includes the physical line and key, with the file path stored separately; duplicate keys are supported. Providers receive opaque batch IDs and an empty Key field. The key is not included in translation context.

Apply replaces only the value span. Original key bytes, separator, indentation, spaces/tabs around the separator and value, comments, BOM, supported encoding, line endings and missing final newline are preserved. Manual edits and retranslation use the same immutable locator. Restore returns the backed-up original bytes.

The separator is the first unescaped `=`. Odd backslash runs escape it; even runs do not. Additional `=` characters belong to the value (`Formula=A=B+C`). This deliberate first-separator behavior follows GameLocalizer's format contract; the upstream parser can be stricter about unescaped additional equals. Escape spellings remain raw in Preview and are protected during translation: `\=`, `\\`, `\n`, `\r`, `\t`, `\uXXXX`, `\/`, and legacy `%3D`. Do not replace them with real newlines in manual edits.

Full-line `#`, `;`, and `//` comments and unescaped inline `//` comments are retained. Hash/semicolon characters inside values are ordinary text. Directives beginning with `#`, regex (`r:`) and splitter (`sr:`) rules, empty keys and empty values are not translated. Mixed pair files never fall back to translating an unparsed line. Manual values introducing a real line break, unescaped comment, changed escapes, or boundary whitespace fail validation before any game file is written.

Translation Memory stores the value as SourceText and keeps the key separately as metadata. Different keys with the same value reuse a cached translation when game, context, languages, provider/model, glossary and category match. Old whole-line cache entries cannot match the new value/context. No database migration or model redownload is required; rescan after upgrading.

Try a copy of `samples/SampleGames/BepInExSynthetic`. All samples are artificial. Format reference: [XUnity.AutoTranslator](https://github.com/bbepis/XUnity.AutoTranslator), including its [TextHelper parser](https://github.com/bbepis/XUnity.AutoTranslator/blob/master/src/XUnity.AutoTranslator.Plugin.Core/Utilities/TextHelper.cs).

Optional real-model validation (after building the worker):

```powershell
dotnet run --project tools/GameLocalizer.Smoke -c Release -- artifacts/offline-smoke --key-value --host artifacts/publish/GameLocalizer.ModelHost.exe
```

The smoke test downloads the verified model only if missing, then blocks model-manager HTTP, translates the four synthetic examples, checks cache reuse, applies to its own fixture and restores byte-exact originals. CI uses deterministic spy providers and never downloads model weights.

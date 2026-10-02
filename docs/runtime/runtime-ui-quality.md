# Runtime UI Translation Quality v1

All classification and translation run in GameLocalizer before gameplay. The collector, hooks, BepInEx loading, and runtime exact dictionary lookup are unchanged.

In Runtime UI, import the captured entries. Review Original, Russian, Context, ContextConfidence, TranslationSource, SeenCount, Scene, Hierarchy, and Status. Long captured Help/Tutorial entries have the category `Runtime Help / Tutorial`.

Use **UI glossary** to add, edit, disable, or remove user terms. Save changes explicitly. Context is optional; supported contexts are listed in the editor. Context-specific entries take precedence over context-free entries within the same glossary. User terms override built-in terms. The built-in glossary is viewable from the editor; override its terms by adding user entries.

User glossary is UTF-8 JSON at `%LOCALAPPDATA%\GameLocalizer\Glossary\runtime-ui.json`. Export transfers it between installations. Import validates the entire file before replacing the user glossary. Invalid imports leave the existing file intact.

Priority: manual, user glossary, built-in UI glossary, translation memory, existing approved translation, offline model. Matching uses the whole original string; there are no substring replacements. Context uses Scene, Object, Hierarchy, Component, neighbor labels and existing category when available. Unknown context is marked Low and does not enable contextual glossary guesses.

Select rows and choose **Перевести заново с UI-контекстом**. Manual rows are excluded. Preview shows old/new Russian, context, and source. Select proposals to apply, or keep the old translations. Preview does not write runtime translation state or new translation memory. Applied proposals are saved as approved, so older automatic memory does not undo an accepted correction.

The suspicious-short filter selects model translations of originals up to 20 characters, seen at least three times, with no current glossary match.

Help/Tutorial follows the ordinary offline sentence pipeline, with line-by-line processing that retains original line endings, blank lines, indentation, bullets, numbered-list prefixes, and trailing whitespace. The existing placeholder protector and validator preserve placeholders and rich-text tags; invalid model output is rejected.

After corrections, choose **Обновить Runtime словарь** and restart the game. The existing owned dictionary copy is replaced with the regenerated master. Conflicting translations of the same exact original are excluded until resolved; the runtime plugin still accepts one final Russian value per original. Editing Russian manually resolves duplicate contexts using the existing workflow.

The runtime plugin requires neither GameLocalizer nor ModelHost during gameplay and has no network/model dependency. Gameplay verification remains a separate real-game check; unit tests and captured-entry review do not claim a new gameplay run.

Local build/test/install uses `dev-install.ps1 -Launch`, targeting `E:\ProjectAI\GameLocalizer`. Public version, GitHub Releases, and tags are unchanged.

## Reprocessing existing translations

**Пересчитать Runtime переводы** loads the union of current captures and persisted runtime identities, including entries no longer present in the latest capture. Stored Manual and UserGlossary entries remain intact. Preview includes changed Russian values and provenance changes, with Old Source and New Source columns. Cancel does not apply the preview.

After **Применить все**, the command saves the authoritative state, updates automatic TM values and their provenance, fully rebuilds the dictionary, and installs it in the existing owned plugin folder. It rereads both files and verifies every entry, count, and SHA-256. Ordinary dictionary-only stale output is excluded from authoritative entries; explicitly authored placeholder templates are retained.

The real AI-Shoujo title entry responsible for `Погрузчик` was `Uploader` (`Title`, `TMPro.TextMeshProUGUI`), while exact `Loader` was absent from the captured/state/installed data. The local migration uses the existing user glossary to correct that actual MainMenu label to `Загрузка`, preserving existing user overrides. No capture or hook changes are involved.

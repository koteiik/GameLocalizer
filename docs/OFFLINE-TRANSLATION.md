# Offline Translation — v0.2.0

GameLocalizer translates accessible game text before playing. It does not watch running games. Apply writes reviewed translations using immutable backups; Restore recovers originals. Preview and translation never modify game files.

## Setup

1. Open Settings → Offline Translation → Models and select Offline.
2. Download the model once: 113,720,273 bytes (108.5 MiB). The manager verifies every file against the SHA256 manifest before inference. Failed partial downloads are not installed. Already verified files are reused on retry.
3. Choose Auto, GPU or CPU, and batch size. Leave memory cache disabled for automatic unload.
4. Find text, select rows, then Translate. Preflight shows cached/manual/new counts, approximate token volume, model and device policy. Test 20 selects across available categories; inspect the result before the full job.
5. Review/edit the Russian column, then Apply with the game closed. You can close GameLocalizer afterwards.

Existing installations preserve their provider setting: switch from Mock to Offline explicitly. Model deletion does not delete translation memory. Downloads and optional GitHub update checks use the Internet; translation does not. No account, API key, paid service or whole-file upload is involved.

## Model and runtime

- Specialized [Helsinki-NLP/opus-mt-en-ru Marian translation model](https://huggingface.co/Helsinki-NLP/opus-mt-en-ru), Apache-2.0.
- [Xenova INT8 ONNX conversion](https://huggingface.co/Xenova/opus-mt-en-ru), pinned revision `e050376e960175e8ddc5cff85025dc8436cddd68`. Exact file sizes and SHA256 are embedded in `src/GameLocalizer.Infrastructure/Models/opus-mt-en-ru.json`.
- ONNX Runtime 1.24.4, CPU or [DirectML](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html); SentencePiece tokenizer from Microsoft.ML.Tokenizers 2.0.0. No Python/CUDA/Ollama installation is needed.
- One private `GameLocalizer.ModelHost.exe` process receives extracted string batches through redirected local pipes. Native GPU crashes cannot terminate WPF. A failed GPU load falls back to CPU; memory pressure reduces batch size, then GPU failure falls back to CPU. The UI reports actual device after loading.
- Model weights stay outside the release ZIP under `%LOCALAPPDATA%/GameLocalizer/Models`. Budget at least 4 GB system RAM and 1 GB free storage. CPU is the portable fallback; GPU compatibility depends on drivers and quantized operators.

## Persistence and lifecycle

SQLite memory stores source/translation, languages, game/name, file/key/context, source hash, category, provider/model/version/glossary version, timestamps and manual flag. Exact machine cache identity includes source, languages, game, context, provider, model, model version, glossary version and category. File/key remain provenance; equal source/context can be reused across files in the same game. Manual identity intentionally excludes model/provider/glossary/category, so a machine job cannot overwrite human corrections.

Scanning hydrates persistent translations into the temporary preview. Completed batches are committed before cancellation returns. Job metadata in `%LOCALAPPDATA%/GameLocalizer/jobs` records counts, device, model and terminal state. After restart, scan the game and Translate to resume remaining selected rows. A new job also reuses memory; ignoring machine memory requires the explicit Retranslate choice. Manual rows still win. Changed source strings are separate identities. Back up `memory.db` separately from game backups if moving computers.

The runtime loads only when an explicit Translate/Test job has uncached text. Cached-only jobs never load it. Default job completion, cancellation and app exit kill/dispose the worker; cancellation forces unload even with memory cache enabled. Resource display includes application plus worker private RAM. GPU utilization is explicitly unavailable; WMI adapter RAM is only driver-reported information, not a reliable live usage counter.

Statuses: NotTranslated, Queued, Translating, Translated, FromMemory, Manual, ValidationError, Cancelled, Failed. Invalid placeholders/markup never pass Apply. A failed row can be selected and retried. No background inference starts when launching a game.

## Glossary and validation

Import/export JSON or CSV with `Original`, `Russian`, `CaseSensitive`, `Category`. Optional Category narrows matches; Names applies consistently across categories. v0.2 uses exact **whole-string** matches. It deliberately does not replace arbitrary words within sentences: that would break Russian inflection. Add complete source phrases when a name needs controlled rendering inside dialogue.

Placeholders and markup are protected before inference. Plain spans between protected tokens are translated and reassembled; a validator checks token identity/count and markup. This guarantees token preservation but reduces sentence context across markup. The specialized model is batched, not prompted like a chat LLM, and does not use adjacent dialogue as document context.

## Known limitations

English → Russian only; greedy INT8 decoding is not professional localization. Review fluency, style and short ambiguous UI words (for example Continue may become «Продолжайте»). No gender/character consistency beyond exact glossary/manual memory, beam search or automatic inflection. Source input above 255 model tokens or output exceeding the bounded generation limit fails visibly instead of silently truncating. Existing scanner/format limits still apply. GPU is best-effort: the tested machine required CPU fallback after a native DirectML load failure. This is not evidence that accelerated GPU inference works on every driver.

## Verification

CI uses fake runtimes/downloads: it never fetches weights. Opt-in real smoke on Windows:

```powershell
dotnet run --project tools/GameLocalizer.Smoke -c Release -r win-x64 -- artifacts/offline-smoke
dotnet run --project tools/GameLocalizer.Smoke -c Release -r win-x64 -- artifacts/offline-smoke --replay
dotnet run --project tools/GameLocalizer.Smoke -c Release -r win-x64 -- artifacts/offline-smoke --gpu
```

The tool downloads/verifies the pinned model once, blocks HTTP during inference, translates Hello / Continue / Settings / New Game / dialogue and placeholders, recreates the memory service, checks zero inference on repeat, checks unloading, applies to synthetic JSON and restores exact original bytes. `--replay` runs a fresh process against the last saved identity and asserts zero model loads/calls. `--gpu` tests GPU attempt and fallback, not guaranteed acceleration.

**Перевод выполняется один раз и сохраняется. Повторный запуск игры не запускает модель и не расходует ресурсы на повторный перевод.**

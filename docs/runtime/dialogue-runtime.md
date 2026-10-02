# Dialogue Runtime Application v1

Translations, glossary, Translation Memory, dictionary generation and the exact dictionary lookup are unchanged. The plugin consumes the same ready dictionary.

The actual AI-Shoujo `Assembly-CSharp.dll` was inspected locally. `AIProject.CaptionScript.CaptionSystem.SetText(string, bool)` first writes its full input to a UniRx string reactive property, sends that same input to `HyphenationJpn.SetText(string)`, and starts `TypefaceAnimatorEx.Play()`. `HyphenationJpn.UpdateText(string)` formats the string and writes it again to `UnityEngine.UI.Text.text`. `TypefaceAnimatorEx` derives from `BaseMeshEffect`; the game's reveal animation remains intact.

Exact dictionary replacement now runs at the full-line source methods before reactive emission, formatting, and animation. Hooks are limited to these verified signatures: CaptionSystem.SetText(string, bool), HyphenationJpn.SetText(string), and HyphenationJpn.UpdateText(string). The Unity Text postfix guards known full English lines in ADVWindow/CommandList labels when another writer runs after the prefix. Unknown strings and intermediate prefixes are left unchanged; dynamic names use only the existing safe placeholder mechanism.

There is no new Update/LateUpdate polling or permanent global scan. Dialogue scope is weakly cached per component. Ordinary menu/TMP replacement stays on its existing path.

## DEV lifecycle trace

Use the Runtime UI checkbox «Диагностика диалогов» while the game is closed. It updates `BepInEx/plugins/GameLocalizerCollector/collector.config`:

```ini
GameId=2745fb3d2161624460c143a4
DialogueTraceEnabled=false
```

The default is false; legacy files containing only the GameId still work. The flag applies on the next game startup. The old BepInEx Developer flag is no longer used. Enable diagnostics, reproduce a dialogue manually, then close the game and disable diagnostics. Runtime UI shows the newest trace path and provides «Открыть лог диалогов»; before the first trace it shows the expected filename pattern.

When enabled, the plugin writes UTF-8 JSONL under `%LOCALAPPDATA%/GameLocalizer/RuntimeCollector/<game-id>/dialogue-trace-*.jsonl`. Events contain timestamp, scene, hierarchy, object, component, scripts attached to the component/parents, previous text, incoming text, resulting text, source/caller method, stack trace, stage, replacement and overwrite-attempt flags. Trace is capped at 10,000 events per session. Source, incoming setter, corrected post-set override and final setter stages distinguish an English write attempt from the Russian final result. With tracing disabled, no stacks, script enumeration or trace file writes are performed.

## Verification scope

Static evidence is from the real managed game DLL. Capture evidence identifies the four sample labels as UnityEngine.UI.Text, with dialogue reply under ADVWindow and interaction choices under CommandList. Synthetic tests verify repeated writes, a writer after the prefix, full-line interception before reveal/formatting, safe dynamic templates, unknown/partial strings, menu regression and disabled trace. They do not assert that a specific controller or third-party plugin was observed overwriting text in live gameplay.

XUnity.AutoTranslator is installed in this game. Its presence is not proof that it caused an overwrite. The real caller and attached-script list require a DEV trace from actual gameplay. The local update deploys the plugin but does not launch gameplay automatically.

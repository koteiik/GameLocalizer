# Local development workflow

Do not create GitHub Releases, preview releases, tags, upload release assets, or run release workflows unless the user explicitly requests publication ("Опубликуй релиз").

Use the existing checkout. Local application target: `E:\ProjectAI\GameLocalizer`. Keep persistent data in `%LOCALAPPDATA%\GameLocalizer`; never remove models, settings, translation memory, jobs, or game backups during updates. Never delete the install directory as a whole.

Run `./scripts/dev-install.ps1` from the repository root for Release build, tests, local publish and manifest-controlled installation. Keep local artifacts under `artifacts/dev`. Do not bump the public version for routine local changes.

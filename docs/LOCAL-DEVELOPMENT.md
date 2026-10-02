# Local development

Run from the existing checkout:

```powershell
.\dev-install.ps1
# Optionally launch after installation:
.\dev-install.ps1 -Launch
```

The script selects the existing SDK under `%LOCALAPPDATA%\GameLocalizerTools\dotnet` when available, or uses `dotnet` from PATH. Override with `-Dotnet <path>` if needed.

Release build and tests must succeed before the self-contained win-x64 staging publish. Output and TRX results are retained under `artifacts/dev`. The script gracefully closes all GameLocalizer UI copies, then closes its helpers before installation; it stops if a UI copy cannot close.

The only user testing copy is `E:\ProjectAI\GameLocalizer\GameLocalizer.exe`. Source and installed EXE/DLL SHA256 hashes and last-write timestamps must match. Verification evidence is saved as `artifacts/dev/<timestamp>-verification.json`. With `-Launch`, the script launches that absolute installed path and verifies every running GameLocalizer process using `MainModule.FileName`. Copies in bin/publish/artifacts are build artifacts only. A hash, timestamp or runtime path mismatch fails the workflow.

The target is `E:\ProjectAI\GameLocalizer`. Updates copy and verify application files, then remove obsolete files from the previous application manifest. The first update uses the existing `install-files.txt`; subsequent updates use `dev-install-files.txt`. Installer registration and uninstall metadata are preserved. A backup of replaced application files is retained next to the install directory, with automatic restoration on a copy/update failure.

Persistent data remains in `%LOCALAPPDATA%\GameLocalizer`. The script rejects data paths and linked paths and never deletes the installation directory or modifies game files. No release workflow, GitHub release, tag, ZIP upload or public version change is performed.

The original icon PNG is in `assets/source`; the transparent seven-size ICO is in `src/GameLocalizer.UI/Assets`. WPF windows, the application executable and Inno Setup share that icon. Windows may cache an older Explorer or shortcut icon.

# Installer validation and release gate

Stable v0.3.0 is blocked until actual installer lifecycle tests and Defender scanning pass. Unit tests alone do not satisfy this gate. `preview-v0.3.0` is not production validation.

## Repeatable lifecycle

Run `scripts/Test-Installer.ps1 -Phase All` on a disposable Windows machine. The `Installer E2E` workflow runs the same script on a fresh GitHub-hosted Windows Server 2022 VM. This is separate from interactive Windows 11 testing; neither proves compatibility with every Windows configuration.

The fixture uses its own AppId and `%LOCALAPPDATA%\GameLocalizer-InstallerQA`; production user data is never reused. It tests actual Inno Setup binaries, shortcuts, registration, model download and inference, SQLite persistence, stale owned-file removal, unknown-file preservation, downgrade rejection, installed update handoff, startup confirmation, failed-update rollback, uninstall preservation, reinstall, and explicitly requested data removal. QA version numbers are synthetic builds of the same source, not historical binary compatibility evidence.

`/DELETEUSERDATA` is explicit unattended consent to delete the fixed application data directory. Without it, silent uninstall preserves data. Interactive uninstall asks separately, with No as the default. Reparse points cause data deletion to be refused. External game backups are never an uninstall target.

## Update recovery

The app verifies the official release SHA256, saves state, and launches its maintenance worker from `GameLocalizer\Updates\InstalledRunner\<id>` before exiting. Inno remains the installation engine. No PowerShell/cmd, scheduled task, or autostart is used at runtime.

The worker snapshots application files and HKCU uninstall metadata under `Updates\InstalledBackup`, flushes a transaction journal, launches Inno, verifies the payload manifest, and requires the new application to acknowledge startup. A failed installation/startup restores the previous files and metadata and restarts the previous app. Backups remain available. Cancellation/power loss before the snapshot is complete cannot modify the installed application.

After an interrupted worker or power loss, close GameLocalizer and run the retained `Updates\InstalledRunner\<id>\GameLocalizer.Updater.exe --installed-recover "<full path to Updates\InstalledTransactions\<id>.json>"`. Recovery refuses completed transactions, missing/corrupt backups, links, or an active application. Do not delete the recovery folder until the update is known to work. Disk exhaustion or hardware damage can still prevent recovery; it is reported rather than silently discarded.

## Write and execution boundaries

Payload writes stay in the chosen installation directory; persistent models, settings, memory, logs, downloads and recovery files stay under the application data directory. Normal Windows installer integration also writes **HKCU uninstall registration, selected Start Menu/Desktop shortcuts, and standard Inno temporary extraction files**. Consequently an absolute claim of “no writes outside two directories” would be inaccurate. The official Inno bootstrap extracts its setup/uninstall engine to Windows temporary storage; no custom downloaded bootstrap EXE is used.

Setup requests `lowest` privileges. It neither configures Defender/SmartScreen nor creates exclusions, tasks, services or startup entries. Installer update downloads accept exact GameLocalizer release assets from the official repository with digest validation and restricted GitHub download redirects. Runtime never fetches arbitrary executable URLs.

## Manifest and signing

`installer-payload.json` contains a deterministic sorted list of payload paths, sizes and SHA256 hashes, including generated installation metadata. It excludes itself to avoid a recursive hash. `installer-manifest.json` includes the payload manifest digest and the final Setup digest. `SHA256SUMS.txt` covers Setup, portable ZIP and external manifest. Run `Finalize-InstallerRelease.ps1` **after signing**, since signing changes bytes.

This is a reproducible payload inventory, **not a claim of byte-identical Setup builds across timestamps/toolchains**. .NET and the pinned official Inno compiler versions must be retained to investigate differences. Standard LZMA2 compression is used; no UPX, obfuscator or custom self-modifying bootstrapper is used.

`Sign-Release.ps1` is build tooling only. Protected GitHub secrets `CODE_SIGNING_PFX_BASE64` and `CODE_SIGNING_PFX_PASSWORD` may supply a trusted CA-issued code-signing certificate; self-signed certificates are rejected. It signs only project-owned binaries and Setup, timestamps SHA256 signatures, and verifies them. Hardware/cloud certificate providers can replace this step. No certificate means **UNSIGNED**, never a fabricated production signature. Signing does not guarantee SmartScreen reputation or antivirus acceptance.

## Antivirus evidence

Use the installed Microsoft Defender `MpCmdRun.exe -Scan -ScanType 3 -File <Setup> -DisableRemediation` and retain the exact hash, engine/signature versions, output and exit code. This read-only custom scan does not disable protection. A clean result applies only to those bytes and signatures. A VirusTotal detection must not be declared false positive without independent investigation. No SmartScreen bypass is part of testing instructions.

References: [Microsoft Defender CLI](https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus), [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool).

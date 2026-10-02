# Apply diagnostics (local development)

The existing compatible Apply write/backup journal remains unchanged. Apply calls carrying an adapter (the application pipeline) now collect read-only evidence and verify the installed physical target after writing. Legacy low-level backup/creation calls without adapter metadata retain their existing behavior.

Reports are saved as UTF-8 TXT and JSON in `%LOCALAPPDATA%\GameLocalizer\Logs\ApplyDiagnostics`. Use **Открыть отчёт** after Apply, including failures. **Diagnostic Apply Trace** defaults to ON; OFF reduces the readable per-entry section to selected entries. Hash, parser, value, backup checks and conflict detection remain enabled.

SUCCESS means physical writing and validation succeeded. It does not prove the game loaded this file. ActiveLocalizationSource stays UNKNOWN without runtime evidence. A single supported XUnity configuration resolves its Directory relative to BepInEx (or game root for root configuration), substitutes {Lang}, and reports ACTIVE PATH MISMATCH when an XUnity target is outside that directory. Multiple configs or unresolved variables leave mismatch UNKNOWN. Competing keys are potential override conflicts, not a claim about runtime loading priority.

Reports contain original and translated text. No language configuration, locale folder, executable, DLL or font is patched by diagnostics. Backups remain preserved. The report verifies the original retained by the existing backup journal, which can precede the current Apply on repeated writes.

Regression coverage includes Japanese key/Russian value, disk reread, SHA256, BOM/CRLF, backup hashes, parser failure, unchanged-output rejection, missing targets, configuration mismatch and unknown source, duplicate values, partial writes, and both report formats. Runtime game behavior still requires reproducing Apply for the affected game and inspecting its report.

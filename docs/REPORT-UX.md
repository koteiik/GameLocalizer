# Main screen and report UX

The Report action finds the latest persisted TXT/JSON report by the selected game's physical Root stored in JSON. It refreshes on game selection and when Apply returns or fails. A missing report disables the action with a tooltip that remains available on disabled buttons. An externally deleted report is handled in the status bar without opening a missing file.

The toolbar wraps workflow, Apply/Restore/Cleanup, and diagnostics as whole groups. Secondary actions use a subdued blue style. Preview prioritizes Key/Original/Russian with star widths; service fields use fixed widths. The file cell displays a filename and its tooltip shows the physical path. Text cells use ellipsis and full-value tooltips; the Russian editor preserves the complete value. Column virtualization is disabled for the nine fixed columns to prevent missing cells during deferred star-width layout; row virtualization and paging remain enabled.

Engine evidence and detailed status/progress remain available in tooltips. The existing translation, Apply, Restore and Cleanup implementation is unchanged by this UX task.

Verification: regression tests use a real diagnostic Apply report, a fresh ViewModel, game switching, and external deletion. WPF layout tests measure and render the screen at 1366x768, 1600x900 and 1920x1080 (96 DPI, with allowance for window chrome). Rendered PNGs are saved under artifacts/dev/ui-diagnostics and inspected. These checks cover layout and bindings; they do not automate the native file association used to open TXT reports.

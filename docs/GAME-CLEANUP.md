# Game cleanup

`Восстановить` restores original bytes and retains backup objects, the ownership manifest,
created files and directories. `Убрать` first prepares a plan and requests confirmation.
It restores and verifies originals, deletes confirmed created files whose SHA256 matches,
and removes only recorded directories that are empty. Externally edited created files
are kept by default; explicit force approval is bound to the observed current hash.
Pre-existing game/mod folders are never inferred to be owned from their names.

The manifest retains `Files` for compatibility and exposes `ModifiedFiles`, with original
hashes, latest applied hashes, backup object names and known apply hashes. New files have
individual confirmed ownership flags. Directory ownership is recorded only after an
exclusive Windows directory creation succeeds. New file creation never replaces a file
that appeared concurrently. An unconfirmed creation after a crash blocks destructive
cleanup rather than assuming ownership.

Cleanup commits `CleanupInterrupted` before changing game files. Selecting that game
after restarting offers to resume. Restore and deletion are idempotent. A separate
retirement journal and `Finalizing` state permit recovery while deleting backup objects.
Backups are retired individually after verification; foreign backup contents are retained.
No recursive deletion is used for game cleanup. Linked paths and traversal are rejected.

Translation memory, models, settings and jobs under LOCALAPPDATA are not accessed by
cleanup. A new scan continues to reuse translation memory. Installation and publication
use `scripts/dev-install.ps1`; local outputs are under `artifacts/dev`. No public version bump,
GitHub Release, tags or release uploads are part of this workflow.

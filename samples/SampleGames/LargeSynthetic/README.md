# Large synthetic game

Run `./Generate.ps1` in PowerShell. It creates **120,005** artificial CSV dialogue rows, Unity directory evidence, and log/config decoys in `artifacts/large-synthetic` (excluded from Git). No commercial game content is used. Existing nonempty directories are never overwritten.

Add the generated folder manually, then Find Text. Expected: 120,005 candidates, 2,000 rows on page one, 61 pages; a global search for `number 120004` finds the last row. `output_log.txt`, `Player.log` and the five INI boolean/null flags must not appear in preview. Each CSV is below the retained 4 MiB per-file limit.

The automated suite independently generates 100,005+ rows in disposable directories and checks batching, cancellation, paging, global search, selection counts and off-page edits.

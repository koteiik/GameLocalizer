# v0.1.1 regression investigation

## Selection root cause

`Game` was a record with mutable Engine/Status fields and a PropertyChanged event. Compiler-generated equality/hash included mutable instance state. WPF Selector uses hashed item identity to maintain selection; after analysis changed those fields, selecting a different item could retain the old selected item/source binding. This was reproduced before the fix using an STA WPF ListBox with a two-way SelectedItem binding: expected Second, actual First. The same test passes after Game becomes a sealed reference-identity class. Simply adding TwoWay would not have fixed the reproduced bug.

The old SelectedGame setter also silently ignored assignments while Busy. It now accepts every distinct reference, cancels the old operation and invalidates its generation. Each command captures its target game/session; engine, preview and progress callbacks verify the generation before updating the UI. Refresh matches canonical paths and preserves existing references. The ListBox has explicit TwoWay binding and does not synchronize selection with ICollectionView.CurrentItem.

## Large scan root cause

Find appended every candidate to ObservableCollection and threw an IOException at 50,000. It also retained all source file text in a snapshot dictionary. Virtualized visuals did not limit either managed collection.

The new pipeline parses one size-bounded resource at a time, classifies entries, yields batches of at most 1,000 via IAsyncEnumerable, and commits them to SQLite. The WPF collection is a page, not the dataset. Search/filter/sort are SQL queries over the entire session. Progress reports files visited, candidate rows and processed rows. Cancellation retains committed batches. Full-dataset operations use keyset batches; apply validates all files, stages output on disk and uses the existing backup/atomic-write service one file at a time.

Per-file parsing still caps inputs at 4 MiB and can materialize that one file's entries. Directory traversal uses bounded-depth lazy enumerators. JSON byte-to-character offset accounting and span replacement are linear rather than quadratic. The resource catalog is a bounded priority queue; logs/technical files cannot displace localization candidates.

## Regression coverage

- Actual WPF selection before/after mutation and repeated selection.
- ViewModel selection/preview/commands, refresh reference preservation, switching during a deliberately cancellation-ignoring detector.
- 100,005-row pipeline and 100,006-row ViewModel run with at most 2,000 loaded rows.
- Global search, case-insensitive Cyrillic search, file/confidence filters, sorting and counts.
- Paging preserves edits; translation/application include off-page rows; invalid off-page markup blocks writes.
- Cancellation, config flags, log filenames, diagnostics and resource classification.
- Existing backup/restore, parser, placeholder and translation-memory tests remain in the suite.

See GitHub Actions for the exact test and publish results for the release tag.

## Local verification

Release build and all 99 tests passed on Windows. The self-contained executable was launched and visually checked with the generated 120,005-row sample: 2,000 loaded rows, page two 2001–4000, and a global search returning the final row 120004. The progress display reported 120,010 processed entries (five INI flags excluded) and 120,005 candidates. Switching from the large scan to Demo cleared the preview; switching between two Demo folders after analysis updated the right panel correctly without restarting. Only synthetic folders were analyzed in this UI regression run.

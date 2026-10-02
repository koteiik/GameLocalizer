# Contributing

Build and test with .NET 8 on Windows before submitting a pull request. Keep Core independent of WPF, provider APIs and engine implementations. Register services and adapters through dependency injection. Add regression tests for parser and file-safety changes.

Use artificial fixtures only. Never submit commercial game files, personal logs without review, SQLite databases, API keys, tokens or credentials. Do not implement DRM or anti-cheat bypasses. Do not introduce executable modifications into the MVP.

Report reproducible issues with app/Windows versions, format, engine and launcher. Include minimal synthetic input, expected output and reviewed logs. Feature requests should explain the user scenario, scope and safe failure behavior.

Versions use semantic versioning. Maintainers create releases using a `vX.Y.Z` tag after checks pass.

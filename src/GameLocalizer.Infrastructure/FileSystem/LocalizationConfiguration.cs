namespace GameLocalizer.Infrastructure.FileSystem;

internal static class LocalizationConfiguration
{
    // Read-only evidence. Never derive a write path from Language, Directory or OutputFile.
    public static async Task<string> ReadAsync(string root, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var relative in new[] { "BepInEx/config/AutoTranslatorConfig.ini", "BepInEx/AutoTranslatorConfig.ini", "AutoTranslatorConfig.ini" })
        {
            var path = BackupService.Resolve(root, relative);
            if (!File.Exists(path)) continue;
            try { parts.Add((await TextFiles.ReadAsync(path, ct)).Text); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            { /* Unknown configuration does not authorize any other target path. */ }
        }
        return string.Join("\n", parts);
    }
}

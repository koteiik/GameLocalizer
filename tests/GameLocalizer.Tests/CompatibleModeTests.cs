using System.Text;
using System.Text.Json;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class CompatibleModeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CompatibleMode", Guid.NewGuid().ToString("N"));
    public CompatibleModeTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("\r\n", false)] [InlineData("\n", true)] [InlineData("\r\n", true)]
    public async Task SamePhysicalEnFileBackupRestoreCleanupAndUnchangedConfig(string newline, bool bom)
    {
        Assert.Equal(LocalizationApplyMode.CompatibleReplacement, new AppSettings().ApplyMode);
        const string relative = "BepInEx/Translation/en/Text/dialogue.txt";
        var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var config = Path.Combine(root, "BepInEx", "AutoTranslatorConfig.ini");
        await File.WriteAllTextAsync(config, "Language=ru\nFromLanguage=ja\nDirectory=Translation/{Lang}/Text\nOutputFile=dialogue.txt");
        var configBytes = await File.ReadAllBytesAsync(config);
        var source = "# Dialogue" + newline + "ありがとう = Thank you." + newline + "ありがとう=Thank you." + newline + "こんにちは=Hello." + newline;
        await File.WriteAllTextAsync(path, source, new UTF8Encoding(bom));
        var original = await File.ReadAllBytesAsync(path); var snapshot = await TextFiles.ReadAsync(path, default);
        var adapter = new BepInExLocalizationAdapter();
        var resolved = new ActiveLocalizationResolver().Resolve(path, snapshot.Text, [adapter], await File.ReadAllTextAsync(config));
        Assert.Equal("ru", resolved.ConfiguredSlot); Assert.Contains("fallback", resolved.Evidence);
        Assert.Equal(Path.GetFullPath(path), resolved.PhysicalSourceFile); Assert.Equal("en", resolved.LocalizationSlot);
        var translations = adapter.Extract(source).ToDictionary(e => e.Id, e => e.Text == "Hello." ? "Привет." : "Спасибо.");
        var output = adapter.ApplyTranslations(source, translations);
        var backup = new BackupService(NullLogger<BackupService>.Instance, Path.Combine(root, "state"));
        await backup.ApplyAsync(root, [new(relative, snapshot.Hash, snapshot.Encode(output)) { AdapterType = adapter.Name, LocalizationSlot = resolved.LocalizationSlot }], default);
        Assert.Equal(source.Replace("Thank you.", "Спасибо.").Replace("Hello.", "Привет."), await File.ReadAllTextAsync(path));
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(config));
        Assert.False(Directory.Exists(Path.Combine(root, "BepInEx", "Translation", "ru")));
        var manifestPath = Path.Combine(root, "GameLocalizer_Backup", "manifest.json");
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            var file = manifest.RootElement.GetProperty("ModifiedFiles")[0];
            Assert.Equal("en", file.GetProperty("LocalizationSlot").GetString());
            Assert.Equal(TextFiles.Hash(original), file.GetProperty("OriginalHash").GetString());
            Assert.Equal(bom ? "EFBBBF" : "", file.GetProperty("Bom").GetString());
            Assert.Empty(manifest.RootElement.GetProperty("CreatedFiles").EnumerateArray());
        }
        await backup.RestoreAsync(root, default); Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.True(File.Exists(manifestPath));
        await backup.ApplyAsync(root, [new(relative, snapshot.Hash, snapshot.Encode(output))], default);
        var memory = Path.Combine(root, "memory.db"); await File.WriteAllTextAsync(memory, "persistent");
        Assert.True((await backup.CleanupAsync(root, null, default)).BackupRemoved);
        Assert.Equal(original, await File.ReadAllBytesAsync(path)); Assert.False(File.Exists(manifestPath));
        Assert.Equal("persistent", await File.ReadAllTextAsync(memory));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "state", "GameManifests")));
    }

    [Fact]
    public async Task FreshSourceHashAfterUpdateRebasesOriginalAndPreservesHistory()
    {
        var path = Path.Combine(root, "dialogue.txt");
        var backup = new BackupService(NullLogger<BackupService>.Instance, Path.Combine(root, "state"));
        var first = Encoding.UTF8.GetBytes("key=First"); var updated = Encoding.UTF8.GetBytes("key=Updated");
        await File.WriteAllBytesAsync(path, first);
        await backup.ApplyAsync(root, [new("dialogue.txt", TextFiles.Hash(first), Encoding.UTF8.GetBytes("key=Первый"))], default);
        await File.WriteAllBytesAsync(path, updated);
        await Assert.ThrowsAsync<IOException>(() => backup.RestoreAsync(root, default));
        await backup.ApplyAsync(root, [new("dialogue.txt", TextFiles.Hash(updated), Encoding.UTF8.GetBytes("key=Обновлённый"))], default);
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "GameLocalizer_Backup", "manifest.json"))))
            Assert.Single(manifest.RootElement.GetProperty("Files")[0].GetProperty("History").EnumerateArray());
        await backup.RestoreAsync(root, default); Assert.Equal(updated, await File.ReadAllBytesAsync(path));
        Assert.True((await backup.CleanupAsync(root, null, default)).BackupRemoved);
        Assert.Equal(updated, await File.ReadAllBytesAsync(path));
    }
}

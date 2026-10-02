using System.Text;
using System.Text.Json.Nodes;
using GameLocalizer.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace GameLocalizer.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizerCleanupTests", Guid.NewGuid().ToString("N"));
    private readonly BackupService service = new(NullLogger<BackupService>.Instance);
    public CleanupTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string Backup => Path.Combine(root, "GameLocalizer_Backup");
    private string Write(string relative, string content)
    {
        var p = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, content); return p;
    }
    private Task Create(string path = "Translation/ru/Text/dialogue.txt") => service.ApplyAsync(root, [new(path, "", Encoding.UTF8.GetBytes("translated"))], default);
    private async Task Modify()
    {
        var p = Write("original.txt", "original");
        await service.ApplyAsync(root, [new("original.txt", TextFiles.Hash(File.ReadAllBytes(p)), Encoding.UTF8.GetBytes("translated"))], default);
    }
    [Fact] public async Task RestoreLeavesBackupAndCreatedFiles()
    {
        await Modify(); await Create(); await service.RestoreAsync(root, default);
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "original.txt")));
        Assert.True(File.Exists(Path.Combine(Backup, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(root, "Translation/ru/Text/dialogue.txt")));
    }
    [Fact] public async Task CleanupRestoresAndRemovesBackup()
    {
        await Modify(); var result = await service.CleanupAsync(root, null, default);
        Assert.Equal(1, result.Restored); Assert.True(result.BackupRemoved);
        Assert.False(Directory.Exists(Backup)); Assert.Equal("original", File.ReadAllText(Path.Combine(root, "original.txt")));
    }
    [Fact] public async Task CreatedFilesAndEmptyDirectoriesRemoved()
    {
        await Create(); var plan = await service.PlanCleanupAsync(root, default);
        Assert.Single(plan.FilesToDelete); Assert.Equal(3, plan.DirectoriesToDelete.Count);
        var result = await service.CleanupAsync(root, null, default);
        Assert.Equal(1, result.Deleted); Assert.Equal(3, result.DirectoriesDeleted); Assert.False(Directory.Exists(Path.Combine(root, "Translation")));
    }
    [Theory] [InlineData("BepInEx")] [InlineData("mods")] [InlineData("Translation")] [InlineData("plugins")]
    public async Task ExistingDirectoriesAndFilesPreserved(string folder)
    {
        var existing = Write(folder + "/manual.txt", "manual"); await Create(folder + "/created.txt");
        await service.CleanupAsync(root, null, default);
        Assert.True(Directory.Exists(Path.Combine(root, folder))); Assert.Equal("manual", File.ReadAllText(existing));
    }
    [Fact] public async Task NonEmptyCreatedDirectoryPreserved()
    {
        await Create(); var foreign = Write("Translation/ru/Text/manual.txt", "manual");
        await service.CleanupAsync(root, null, default); Assert.Equal("manual", File.ReadAllText(foreign));
    }
    [Fact] public async Task ExternalCreatedFileProtectedByDefault()
    {
        await Create("new.txt"); Write("new.txt", "user");
        var plan = await service.PlanCleanupAsync(root, default); Assert.Single(plan.Conflicts); Assert.Single(plan.SkippedFiles);
        var result = await service.CleanupAsync(root, null, default); Assert.Equal(1, result.Skipped); Assert.Equal("user", File.ReadAllText(Path.Combine(root, "new.txt")));
    }
    [Fact] public async Task ForceDeleteRequiresApprovedCurrentHash()
    {
        await Create("new.txt"); var path = Write("new.txt", "user");
        var approved = TextFiles.Hash(File.ReadAllBytes(path));
        var result = await service.CleanupAsync(root, new Dictionary<string,string> { ["new.txt"] = approved }, default);
        Assert.Equal(1, result.Deleted); Assert.False(File.Exists(path));
    }
    [Fact] public async Task StaleForceConfirmationPreservesFile()
    {
        await Create("new.txt"); Write("new.txt", "user");
        var result = await service.CleanupAsync(root, new Dictionary<string,string> { ["new.txt"] = "stale" }, default);
        Assert.Equal(1, result.Skipped);
    }
    [Theory] [InlineData("../escape.txt")] [InlineData("..\\escape.txt")] [InlineData("new.txt:stream")] [InlineData("gamelocalizer_backup/evil.txt")]
    public async Task PathTraversalRejectedBeforeMutations(string relative)
    {
        await Create("new.txt");
        var p = Path.Combine(Backup, "manifest.json"); var json = JsonNode.Parse(File.ReadAllText(p))!;
        json["CreatedFiles"]![0]!["RelativePath"] = relative; File.WriteAllText(p, json.ToJsonString());
        await Assert.ThrowsAsync<IOException>(() => service.CleanupAsync(root, null, default));
        Assert.True(File.Exists(Path.Combine(root, "new.txt"))); Assert.True(Directory.Exists(Backup));
    }
    [Fact] public async Task InterruptedCleanupCanResume()
    {
        await Modify(); await Create();
        var p = Path.Combine(Backup, "manifest.json"); var json = JsonNode.Parse(File.ReadAllText(p))!;
        json["CleanupState"] = "CleanupInterrupted"; File.WriteAllText(p, json.ToJsonString());
        Assert.True(service.IsCleanupInterrupted(root));
        await Assert.ThrowsAsync<IOException>(() => Create("second.txt"));
        await service.CleanupAsync(root, null, default); Assert.False(service.IsCleanupInterrupted(root));
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "original.txt")));
    }
    [Fact] public async Task CancellationRetainsBackup()
    {
        await Modify(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CleanupAsync(root, null, cts.Token));
        Assert.True(File.Exists(Path.Combine(Backup, "manifest.json")));
        await service.CleanupAsync(root, null, default);
    }
    [Theory] [InlineData("memory.db")] [InlineData("Models/model.onnx")] [InlineData("Settings/settings.json")] [InlineData("Glossary/user.json")] [InlineData("Logs/user.log")]
    public async Task UnownedUserDataPreserved(string relative)
    {
        var p = Write(relative, "persistent"); await Create("new.txt"); await service.CleanupAsync(root, null, default);
        Assert.Equal("persistent", File.ReadAllText(p));
    }
    [Fact] public async Task ForeignBackupFilePreserved()
    {
        await Modify(); File.WriteAllText(Path.Combine(Backup, "manual.txt"), "manual");
        var result = await service.CleanupAsync(root, null, default); Assert.False(result.BackupRemoved);
        Assert.Equal("manual", File.ReadAllText(Path.Combine(Backup, "manual.txt")));
    }
    [Fact] public async Task FinalizationRecoveryDoesNotRequireRetiredOriginal()
    {
        await Modify(); await service.RestoreAsync(root, default);
        var p = Path.Combine(Backup, "manifest.json"); var json = JsonNode.Parse(File.ReadAllText(p))!;
        var name = json["Files"]![0]!["ObjectName"]!.GetValue<string>();
        json["Files"] = new JsonArray(); json["CleanupState"] = "Finalizing"; File.WriteAllText(p, json.ToJsonString());
        File.WriteAllText(Path.Combine(Backup, "cleanup-retirement.json"), System.Text.Json.JsonSerializer.Serialize(new[] { name }));
        File.Delete(Path.Combine(Backup, name));
        Assert.True(service.IsCleanupInterrupted(root)); await service.CleanupAsync(root, null, default); Assert.False(Directory.Exists(Backup));
    }
    [Fact] public async Task UnconfirmedCreationNeverGrantsOwnership()
    {
        await Create("new.txt");
        var p = Path.Combine(Backup, "manifest.json"); var json = JsonNode.Parse(File.ReadAllText(p))!;
        json["CreatedFiles"]![0]!["CreatedByGameLocalizer"] = false;
        File.WriteAllText(p, json.ToJsonString());
        await Assert.ThrowsAsync<IOException>(() => service.CleanupAsync(root, null, default));
        Assert.True(File.Exists(Path.Combine(root, "new.txt"))); Assert.True(Directory.Exists(Backup));
    }
    [Fact] public async Task FailureAfterJournalCommitCanResume()
    {
        await Modify();
        using (var held = new FileStream(Path.Combine(root, "original.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => service.CleanupAsync(root, null, default));
        Assert.True(service.IsCleanupInterrupted(root)); Assert.True(Directory.Exists(Backup));
        await service.CleanupAsync(root, null, default);
        Assert.Equal("original", File.ReadAllText(Path.Combine(root, "original.txt")));
    }
    [Fact] public async Task RepeatedCreatedApplyNormalizesPathSeparators()
    {
        await Create("Translation/new.txt");
        await service.ApplyAsync(root, [new("Translation\\new.txt", TextFiles.Hash(Encoding.UTF8.GetBytes("translated")), Encoding.UTF8.GetBytes("second"))], default);
        var result = await service.CleanupAsync(root, null, default);
        Assert.Equal(1, result.Deleted); Assert.Equal(0, result.Restored); Assert.False(Directory.Exists(Path.Combine(root, "Translation")));
    }
    [Fact] public async Task JunctionOwnershipCannotEscapeRoot()
    {
        await Create();
        var linked = Path.Combine(root, "Translation", "ru", "Text");
        File.Delete(Path.Combine(linked, "dialogue.txt")); Directory.Delete(linked);
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); Write("outside/dialogue.txt", "external");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(linked); start.ArgumentList.Add(outside);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        try { await Assert.ThrowsAsync<IOException>(() => service.CleanupAsync(root, null, default)); Assert.Equal("external", File.ReadAllText(Path.Combine(outside, "dialogue.txt"))); }
        finally { Directory.Delete(linked); }
    }
}

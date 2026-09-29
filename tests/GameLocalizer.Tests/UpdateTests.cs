using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.UI.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizer-update-tests-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "data");
    private string Updates => Path.Combine(Data, "Updates");
    private string Install => Path.Combine(root, "app");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static AppRelease Release(byte[] bytes) => new("v0.2.2", "Release notes", bytes.Length, Hash(bytes));
    private static string Json(string tag = "v0.2.2", bool prerelease = false, string? digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string? url = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag, prerelease, draft = false, body = "Plain notes <script>ignored</script>",
        assets = new[] {
            new { name = "source.zip", size = 99, digest, browser_download_url = "https://evil.invalid/source.zip" },
            new { name = ReleaseClient.AssetName, size = 42, digest, browser_download_url = url ?? $"https://github.com/koteiik/GameLocalizer/releases/download/{tag}/{ReleaseClient.AssetName}" }
        }
    });
    [Theory]
    [InlineData("v0.2.2", "v0.2.1", true)]
    [InlineData("v0.2.2", "v0.2.2", false)]
    [InlineData("v0.2.1", "v0.2.2", false)]
    [InlineData("v0.3.0-rc.1", "v0.2.2", false)]
    [InlineData("v0.10.0", "v0.9.9", true)]
    public void NewerStableVersionOnly(string target, string current, bool expected) => Assert.Equal(expected, ReleaseClient.IsNewer(new(target, "", 1, null), current));
    [Theory]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0+build1", "1.0.0+build2", 0)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    public void SemanticComparison(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(SemanticVersion.Parse(a).CompareTo(SemanticVersion.Parse(b))));
    [Theory]
    [InlineData("v01.2.3")][InlineData("1.2")][InlineData("1.2.3-01")][InlineData("../../evil")]
    public void InvalidVersionRejected(string value) => Assert.Throws<FormatException>(() => SemanticVersion.Parse(value));
    [Fact] public void CorrectAssetSelected() { var release = ReleaseClient.ParseRelease(Json())!; Assert.True(release.CanInstall); Assert.Equal(42, release.Size); Assert.EndsWith(ReleaseClient.AssetName, release.DownloadUrl); }
    [Fact] public void ApiPrereleaseIgnored() => Assert.Null(ReleaseClient.ParseRelease(Json(prerelease: true)));
    [Fact] public void TagPrereleaseIgnored() => Assert.Null(ReleaseClient.ParseRelease(Json("v0.3.0-beta")));
    [Fact] public void MissingDigestBlocksInstall() => Assert.False(ReleaseClient.ParseRelease(Json(digest: null))!.CanInstall);
    [Theory]
    [InlineData("http://github.com/koteiik/GameLocalizer/releases/download/v0.2.2/GameLocalizer-win-x64.zip")]
    [InlineData("https://github.com/evil/GameLocalizer/releases/download/v0.2.2/GameLocalizer-win-x64.zip")]
    [InlineData("https://evil.invalid/file.zip")]
    public void RemoteUrlRejected(string url) => Assert.Throws<InvalidDataException>(() => ReleaseClient.ParseRelease(Json(url: url)));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private static ReleaseClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new HttpClient(new Handler(send)));
    [Fact]
    public async Task CheckUsesOnlyOfficialApi()
    {
        var client = Client((request, _) => { Assert.Equal("https://api.github.com/repos/koteiik/GameLocalizer/releases/latest", request.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json()) }); });
        Assert.Equal("v0.2.2", (await client.LatestAsync(default))!.Tag);
    }
    [Fact]
    public async Task DownloadAndChecksumSuccess()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic package"); var release = Release(bytes);
        var client = Client((request, _) => { Assert.Equal(release.DownloadUrl, request.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); });
        var file = await client.DownloadAsync(release, Updates, null, default);
        Assert.Equal(bytes, File.ReadAllBytes(file)); await UpdatePackage.VerifyHashAsync(file, release.Sha256!, default);
    }
    [Fact]
    public async Task ChecksumFailureDeletesPartialZip()
    {
        byte[] bytes = [1, 2, 3]; var release = Release(bytes) with { Sha256 = new string('0', 64) };
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, Updates, null, default));
        Assert.Empty(Directory.GetFiles(Updates, "*", SearchOption.AllDirectories));
    }
    private sealed class CancelStream(CancellationTokenSource cancel) : MemoryStream(new byte[100])
    {
        private bool sent;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!sent) { sent = true; buffer.Span[0] = 1; return ValueTask.FromResult(1); }
            cancel.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(0);
        }
    }
    [Fact]
    public async Task CancelledDownloadRemovesPartialAndLeavesInstallation()
    {
        Directory.CreateDirectory(Install); File.WriteAllText(Path.Combine(Install, "GameLocalizer.exe"), "old");
        using var cancellation = new CancellationTokenSource();
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelStream(cancellation)) }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(Release(new byte[100]), Updates, null, cancellation.Token));
        Assert.Empty(Directory.GetFiles(Updates, "*", SearchOption.AllDirectories)); Assert.Equal("old", File.ReadAllText(Path.Combine(Install, "GameLocalizer.exe")));
    }
    [Fact]
    public async Task ArbitraryRedirectRejected()
    {
        var client = Client((_, _) => { var result = new HttpResponseMessage(HttpStatusCode.Redirect); result.Headers.Location = new Uri("https://evil.invalid/package"); return Task.FromResult(result); });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(Release([1]), Updates, null, default));
    }
    private byte[] Package(string? omit = null, string? extra = null, bool wrongHash = false)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var files = new List<PackageFile>();
            foreach (var name in UpdatePackage.RequiredFiles.Where(f => f != omit).Concat(extra == null ? [] : new[] { extra }))
            {
                var content = Encoding.UTF8.GetBytes("new " + name); using (var stream = zip.CreateEntry(name).Open()) stream.Write(content);
                files.Add(new(name, content.Length, wrongHash ? new string('0', 64) : Hash(content)));
            }
            using var manifest = zip.CreateEntry(UpdatePackage.ManifestName).Open(); JsonSerializer.Serialize(manifest, new PackageManifest("0.2.2", files));
        }
        return memory.ToArray();
    }
    private UpdateRequest Request(byte[]? bytes = null)
    {
        bytes ??= Package(); var release = Release(bytes); var directory = Path.Combine(Updates, release.Tag); Directory.CreateDirectory(directory); Directory.CreateDirectory(Install);
        var zip = Path.Combine(directory, ReleaseClient.AssetName); File.WriteAllBytes(zip, bytes);
        File.WriteAllText(Path.Combine(Install, "GameLocalizer.exe"), "original exe"); File.WriteAllText(Path.Combine(Install, "portable-user-file.txt"), "keep this");
        return new(Guid.NewGuid().ToString("N"), zip, Install, 12345, 1, "v0.2.1", release);
    }
    private sealed class Processes(bool starts = true) : IUpdateProcesses
    {
        public int Restarts, Rollbacks, Waits;
        public Task WaitForParentAsync(UpdateRequest request, CancellationToken ct) { Waits++; ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task<bool> StartAndConfirmAsync(string install, string id, CancellationToken ct) { Restarts++; Assert.Equal("new GameLocalizer.exe", File.ReadAllText(Path.Combine(install, "GameLocalizer.exe"))); return Task.FromResult(starts); }
        public void StartPrevious(string install) { Rollbacks++; Assert.Equal("original exe", File.ReadAllText(Path.Combine(install, "GameLocalizer.exe"))); }
    }
    [Fact]
    public async Task SuccessfulUpdateRestartsAndPreservesUserModelMemoryAndGameBackups()
    {
        var request = Request(); var processes = new Processes(); var installer = new UpdateInstaller(Updates, processes);
        string[] preserved = ["memory.db", "Models/model.onnx", "Settings/preferences.json", "Glossary/names.json", "logs/test.log", "jobs/test.json", "settings.json", "glossary.json", "game/GameLocalizer_Backup/original.txt"];
        foreach (var name in preserved) { var path = Path.Combine(Data, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "private " + name); }
        await installer.InstallAsync(request, default);
        Assert.Equal(1, processes.Waits); Assert.Equal(1, processes.Restarts); Assert.Equal(0, processes.Rollbacks);
        Assert.Equal("original exe", File.ReadAllText(Path.Combine(installer.BackupPath(request), "GameLocalizer.exe")));
        Assert.Equal("keep this", File.ReadAllText(Path.Combine(Install, "portable-user-file.txt")));
        foreach (var name in preserved) Assert.Equal("private " + name, File.ReadAllText(Path.Combine(Data, name)));
        Assert.Equal(InstallPhase.Complete, JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(installer.JournalPath(request)))!.Phase);
        Assert.Null(UpdateHandoff.ConsumeNotification(Updates, Install, "v0.2.1"));
        Assert.Equal("v0.2.2", UpdateHandoff.ConsumeNotification(Updates, Install, "v0.2.2")!.Version);
        Assert.Null(UpdateHandoff.ConsumeNotification(Updates, Install, "v0.2.2"));
    }
    [Fact]
    public async Task FailedStartupRollsBackCompleteInstallation()
    {
        var request = Request(); var processes = new Processes(false); var installer = new UpdateInstaller(Updates, processes);
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(request, default));
        Assert.Equal("original exe", File.ReadAllText(Path.Combine(Install, "GameLocalizer.exe"))); Assert.Equal(1, processes.Rollbacks);
        Assert.False(File.Exists(Path.Combine(Install, "GameLocalizer.ModelHost.exe")));
        Assert.True(File.Exists(Path.Combine(installer.BackupPath(request), "GameLocalizer.exe")));
        Assert.Null(UpdateHandoff.ConsumeNotification(Updates, Install, "v0.2.2"));
    }
    [Theory]
    [InlineData(InstallPhase.MovingOld)][InlineData(InstallPhase.OldMoved)][InlineData(InstallPhase.NewMoved)][InlineData(InstallPhase.Starting)]
    public void InterruptedDirectorySwapIsRecoverable(InstallPhase phase)
    {
        var request = Request(); var installer = new UpdateInstaller(Updates, new Processes()); installer.Save(request, phase);
        Directory.Move(Install, installer.PreviousPath(request));
        if (phase >= InstallPhase.NewMoved) { Directory.CreateDirectory(Install); File.WriteAllText(Path.Combine(Install, "GameLocalizer.exe"), "partial new"); }
        installer.Recover(request); installer.Recover(request);
        Assert.Equal("original exe", File.ReadAllText(Path.Combine(Install, "GameLocalizer.exe")));
        Assert.Equal("keep this", File.ReadAllText(Path.Combine(Install, "portable-user-file.txt")));
    }
    [Theory]
    [InlineData("../escaped.exe")][InlineData("/escaped.exe")][InlineData("C:/escaped.exe")][InlineData("sub/../../escaped.exe")][InlineData("sub\\evil.exe")][InlineData("file:stream")][InlineData("CON.txt")][InlineData("Models/model.onnx")][InlineData("memory.db")][InlineData("GameLocalizer_Backup/old.txt")][InlineData("gamelocalizer.EXE")]
    public async Task UnsafeZipRejectedBeforeInstallationChanges(string extra)
    {
        var request = Request(Package(extra: extra)); var installer = new UpdateInstaller(Updates, new Processes());
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(request, default));
        Assert.Equal("original exe", File.ReadAllText(Path.Combine(Install, "GameLocalizer.exe"))); Assert.False(File.Exists(Path.Combine(root, "escaped.exe")));
    }
    [Fact]
    public async Task MissingExecutableRejected()
    {
        var request = Request(Package(omit: "GameLocalizer.exe"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateInstaller(Updates, new Processes()).InstallAsync(request, default));
        Assert.Equal("original exe", File.ReadAllText(Path.Combine(Install, "GameLocalizer.exe")));
    }
    [Fact]
    public async Task CorruptFileHashRejected()
    {
        var request = Request(Package(wrongHash: true)); await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateInstaller(Updates, new Processes()).InstallAsync(request, default));
        Assert.Equal("original exe", File.ReadAllText(Path.Combine(Install, "GameLocalizer.exe")));
    }
    [Fact]
    public void InstallationCannotContainApplicationData()
    {
        var request = Request() with { InstallDirectory = root }; Assert.Throws<IOException>(() => new UpdateInstaller(Updates, new Processes()).Validate(request));
    }
    [Theory]
    [InlineData(true, false, false)][InlineData(false, true, false)][InlineData(false, false, true)]
    public void ActiveOperationsBlockUpdate(bool application, bool model, bool update) => Assert.True(UpdateInstaller.IsOperationBlocked(application, model, update));
    [Fact]
    public async Task UpdateWhileTranslatingDisabledInViewModel()
    {
        var translating = true; var saves = 0;
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json("v99.0.0")) }));
        var vm = new UpdateViewModel(client, () => translating, () => { saves++; return Task.CompletedTask; });
        await vm.CheckAsync(default); Assert.False(vm.CanUpdate); Assert.False(vm.UpdateCommand.CanExecute(null));
        await vm.InstallAsync(); Assert.Equal(0, saves); Assert.Contains("Завершите", vm.BlockReason);
        translating = false; vm.RefreshAvailability(); Assert.True(vm.CanUpdate);
    }
    [Fact]
    public async Task DatabaseMigrationBacksUpOriginalSchemaAndDoesNotRepeat()
    {
        Directory.CreateDirectory(Data); var path = Path.Combine(Data, "memory.db");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Preserved(Value TEXT); INSERT INTO Preserved VALUES('original');"; cmd.ExecuteNonQuery();
            var service = new TranslationMemoryService(path); await service.FindAsync("hello", "en", "ru", "game", "", "Mock", default);
        }
        var backup = Assert.Single(Directory.GetFiles(Data, "*.bak"));
        using (var db = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Value FROM Preserved"; Assert.Equal("original", cmd.ExecuteScalar());
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='TranslationMemory'"; Assert.Equal(0L, cmd.ExecuteScalar());
        }
        await new TranslationMemoryService(path).FindAsync("hello", "en", "ru", "game", "", "Mock", default);
        Assert.Single(Directory.GetFiles(Data, "*.bak"));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}

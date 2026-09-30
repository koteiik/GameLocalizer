using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizer-installer-tests-" + Guid.NewGuid().ToString("N"));
    public InstallerTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private InstallationInfo Info(bool installed = true, bool writable = true) => new(root, "0.3.0", installed, writable, Path.Combine(root, "separate-data"));
    [Fact]
    public void RegisteredDirectoryAndUninstallerIdentifyInstalledMode()
    {
        File.WriteAllText(Path.Combine(root, "unins000.exe"), "fixture"); File.WriteAllText(Path.Combine(root, "installation.ini"), "fixture");
        var info = new InstallationInfoService(root + Path.DirectorySeparatorChar, () => new(root, "0.3.0")).GetInfo();
        Assert.True(info.Installed); Assert.True(info.Writable); Assert.Equal(root, info.Directory); Assert.Equal("0.3.0", info.Version);
        Assert.False(new InstallationInfoService(root, () => new(Path.Combine(root, "another"), "0.3.0")).GetInfo().Installed);
    }
    [Fact]
    public void CopiedFilesWithoutRegistrationRemainPortable()
    {
        File.WriteAllText(Path.Combine(root, "installation.ini"), "fixture"); File.WriteAllText(Path.Combine(root, "unins000.exe"), "fixture");
        Assert.False(new InstallationInfoService(root, () => null).GetInfo().Installed);
        Assert.False(new InstallationInfoService(Path.Combine(root, "missing"), () => null).GetInfo().Writable);
    }
    [Fact]
    public void RegistryWithoutUninstallerIsNotAnInstalledBuild() => Assert.False(new InstallationInfoService(root, () => new(root, "0.3.0")).GetInfo().Installed);

    private static string ReleaseJson(byte[] payload, bool installer = true, bool zip = true, string? hash = null) => JsonSerializer.Serialize(new
    {
        tag_name = "v99.0.0", draft = false, prerelease = false, body = "Synthetic installer release",
        assets = new[] { installer ? ReleaseClient.InstallerAssetName : "", zip ? ReleaseClient.AssetName : "" }.Where(n => n != "").Select(name => new
        { name, size = payload.Length, digest = "sha256:" + (hash ?? Convert.ToHexString(SHA256.HashData(payload))), browser_download_url = $"https://github.com/koteiik/GameLocalizer/releases/download/v99.0.0/{name}" })
    });
    [Theory]
    [InlineData(true, ReleaseClient.InstallerAssetName)][InlineData(false, ReleaseClient.AssetName)]
    public void ModeSelectsExactAsset(bool installed, string asset) => Assert.Equal(asset, ReleaseClient.ParseRelease(ReleaseJson([1, 2, 3]), installed)!.Asset);
    [Fact]
    public void InstalledModeNeverFallsBackToZip() => Assert.Throws<InvalidDataException>(() => ReleaseClient.ParseRelease(ReleaseJson([1], installer: false), true));
    [Fact]
    public void InstallerVersionNewerAndDowngradeRejected()
    {
        Assert.True(ReleaseClient.IsNewer(new("v0.3.0", "", 10, null, ReleaseClient.InstallerAssetName), "0.2.3"));
        Assert.False(ReleaseClient.IsNewer(new("v0.2.3", "", 10, null, ReleaseClient.InstallerAssetName), "0.3.0"));
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private sealed class InstallerSpy : IInstallerUpdateService
    {
        public int Prepared, Launched;
        public bool Fail;
        public async Task<ProcessStartInfo> PrepareAsync(AppRelease release, string setup, InstallationInfo installation, CancellationToken ct)
        {
            Prepared++; Assert.Equal(ReleaseClient.InstallerAssetName, Path.GetFileName(setup)); Assert.True(installation.Installed);
            await UpdatePackage.VerifyHashAsync(setup, release.Sha256!, ct);
            return new(setup);
        }
        public void Launch(ProcessStartInfo start) { if (Fail) throw new IOException("Simulated launch failure"); Launched++; }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UpdateCommandVerifiesInstallerAndClosesOnlyAfterSuccessfulLaunch(bool fail)
    {
        byte[] payload = [0x4d, 0x5a, 1, 2, 3]; var downloads = 0; var saved = 0; var shutdown = 0; var translating = true;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(payload)) };
            Assert.EndsWith(ReleaseClient.InstallerAssetName, request.RequestUri.AbsoluteUri); downloads++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var launcher = new InstallerSpy { Fail = fail };
        var vm = new UpdateViewModel(new(http, true), () => translating, () => { saved++; return Task.CompletedTask; }, Info(), launcher, () => shutdown++, Path.Combine(root, "Updates"));
        await vm.CheckAsync(default); Assert.False(vm.UpdateCommand.CanExecute(null)); await vm.InstallAsync(); Assert.Equal(0, downloads);
        translating = false; vm.RefreshAvailability(); Assert.True(vm.UpdateCommand.CanExecute(null));
        await vm.InstallAsync(); Assert.Equal(1, saved); Assert.Equal(1, downloads); Assert.Equal(1, launcher.Prepared);
        Assert.Equal(fail ? 0 : 1, shutdown); Assert.Equal(!fail, vm.HandoffStarted);
        // This test uses production download validation, but never executes its artificial EXE.
        File.Delete(Path.Combine(root, "Updates", "v99.0.0", ReleaseClient.InstallerAssetName));
    }
    [Fact]
    public async Task WrongInstallerChecksumNeverLaunchesOrCloses()
    {
        byte[] payload = [1, 2, 3]; var launches = new InstallerSpy(); var closed = false;
        using var http = new HttpClient(new Handler(r => new(HttpStatusCode.OK) { Content = r.RequestUri!.Host == "api.github.com" ?
            new StringContent(ReleaseJson(payload, hash: new string('0', 64))) : new ByteArrayContent(payload) }));
        var vm = new UpdateViewModel(new(http, true), () => false, () => Task.CompletedTask, Info(), launches, () => closed = true, Path.Combine(root, "Updates"));
        await vm.CheckAsync(default); await vm.InstallAsync(); Assert.Equal(0, launches.Prepared); Assert.False(closed); Assert.False(vm.HandoffStarted);
    }
    [Fact]
    public async Task ReadOnlyInstallBlocksUpdate()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson([1])) }));
        var vm = new UpdateViewModel(new(http, true), () => false, () => Task.CompletedTask, Info(writable: false));
        await vm.CheckAsync(default); Assert.False(vm.CanUpdate); Assert.Contains("записи", vm.BlockReason);
    }
    [Theory]
    [InlineData(false, false, false)][InlineData(false, true, false)][InlineData(true, true, false)][InlineData(true, false, true)]
    public void UninstallDefaultsToPreservingUserData(bool consent, bool silent, bool expected) => Assert.Equal(expected, UninstallDataPolicy.ShouldDelete(consent, silent));
    [Fact]
    public async Task InstallerPreparationRejectsPortableAndDowngrade()
    {
        var service = new InstallerUpdateService(); var release = new AppRelease("v0.2.3", "", 3, new string('a', 64), ReleaseClient.InstallerAssetName);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(release, "file.exe", Info(), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(release with { Tag = "v99.0.0" }, "file.exe", Info(installed: false), default));
    }
    [Fact]
    public void UnattendedDeleteRequiresItsOwnExplicitConsent()
    {
        Assert.False(UninstallDataPolicy.ShouldDelete(true, true));
        Assert.True(UninstallDataPolicy.ShouldDelete(false, true, true));
    }
    [Fact]
    public void RecoveryInventoryDetectsCorruptionAndPreservesUnknownFiles()
    {
        File.WriteAllText(Path.Combine(root, "unknown.txt"), "keep me");
        var inventory = InstalledUpdate.Inventory(root);
        InstalledUpdate.VerifyInventory(root, inventory);
        Assert.Contains(inventory, f => f.Path == "unknown.txt");
        File.WriteAllText(Path.Combine(root, "unknown.txt"), "changed");
        Assert.Throws<InvalidDataException>(() => InstalledUpdate.VerifyInventory(root, inventory));
    }
    [Theory]
    [InlineData("memory.db")][InlineData("settings.json")][InlineData("GameLocalizer_Backup")][InlineData("Models")]
    public void RecoveryRefusesInstallationContainingUserData(string name)
    {
        File.WriteAllText(Path.Combine(root, name), "must not be modified");
        Assert.Throws<InvalidDataException>(() => InstalledUpdate.Inventory(root));
    }
    [Fact]
    public void InterruptedJournalRemainsReadableWhenInstallationMetadataWasLost()
    {
        var updates = Path.Combine(root, "Updates");
        var supervisor = new InstalledUpdate(updates);
        var request = new InstalledUpdateRequest(Guid.NewGuid().ToString("N"), Path.Combine(updates, "v0.3.1", ReleaseClient.InstallerAssetName), Path.Combine(root, "Installation"), "0.3.0",
            new("v0.3.1", "", 10, new string('a', 64), ReleaseClient.InstallerAssetName), 1, 1);
        supervisor.Save(new(request, "Installing"));
        Assert.Equal("Installing", supervisor.Read(supervisor.JournalPath(request.Id)).Phase);
        Assert.Throws<InvalidDataException>(() => supervisor.Validate(request));
        Assert.Throws<InvalidDataException>(() => supervisor.JournalPath("../escape"));
    }
}

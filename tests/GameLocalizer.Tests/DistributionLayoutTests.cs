using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Update;
using Xunit;

namespace GameLocalizer.Tests;

public sealed class DistributionLayoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "GameLocalizer-layout-" + Guid.NewGuid().ToString("N"));
    private void FileAt(string path) { var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, "fixture"); }
    [Fact]
    public void CleanPathsResolveFromRootAndInternalBase()
    {
        FileAt("GameLocalizer.exe"); FileAt("app/GameLocalizer.dll");
        foreach (var basis in new[] { root, Path.Combine(root, "app") })
        {
            Assert.Equal(root, DistributionPaths.InstallRoot(basis));
            Assert.Equal(Path.Combine(root, "app", "GameLocalizer.ModelHost.exe"), DistributionPaths.ModelHost(basis));
            Assert.Equal(Path.Combine(root, "app", "Updater"), DistributionPaths.Updater(basis));
        }
    }
    [Fact]
    public void LegacyInstallationAndOrdinaryAppNamedFolderKeepTheirBase()
    {
        FileAt("GameLocalizer.exe"); FileAt("app/unrelated.txt");
        Assert.Equal(Path.Combine(root, "GameLocalizer.ModelHost.exe"), DistributionPaths.ModelHost(root));
        Assert.Equal(Path.Combine(root, "app"), DistributionPaths.InstallRoot(Path.Combine(root, "app")));
    }
    [Fact]
    public void InstalledRegistrationAcceptsInternalMetadata()
    {
        FileAt("GameLocalizer.exe"); FileAt("app/GameLocalizer.dll"); FileAt("app/unins000.exe"); FileAt("app/installation.ini");
        var info = new InstallationInfoService(root, () => new(root, "0.5.0")).GetInfo();
        Assert.True(info.Installed); Assert.Equal(root, info.Directory);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PackageExtractionSupportsBothLayouts(bool legacy)
    {
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "package.zip");
        var files = new List<PackageFile>();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in legacy ? UpdatePackage.LegacyRequiredFiles : UpdatePackage.RequiredFiles)
            {
                byte[] bytes = [1, 2, 3];
                using (var output = zip.CreateEntry(name).Open()) output.Write(bytes);
                files.Add(new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
            }
            using var outputManifest = zip.CreateEntry(legacy ? UpdatePackage.LegacyManifestName : UpdatePackage.ManifestName).Open();
            JsonSerializer.Serialize(outputManifest, new PackageManifest("0.5.0", files));
        }
        var destination = Path.Combine(root, "extracted");
        UpdatePackage.Extract(zipPath, destination, "0.5.0", default);
        Assert.True(File.Exists(Path.Combine(destination, legacy ? "GameLocalizer.ModelHost.exe" : "app/GameLocalizer.ModelHost.exe")));
        Assert.True(File.Exists(Path.Combine(destination, legacy ? "coreclr.dll" : "app/coreclr.dll")));
        if (!legacy) Assert.Equal(new[] { "GameLocalizer.exe" }, Directory.GetFiles(destination).Select(Path.GetFileName));
    }
    [Theory]
    [InlineData("GameLocalizer-Setup.exe", true)]
    [InlineData("GameLocalizer-win-x64.zip", false)]
    [InlineData("GameLocalizer-Setup-x64.exe", true)]
    [InlineData("GameLocalizer-Portable-x64.zip", false)]
    public void ReleaseParsingAcceptsTrustedOldAndNewAssetNames(string name, bool installed)
    {
        var json = JsonSerializer.Serialize(new { tag_name = "v0.5.0", prerelease = false, draft = false, assets = new[] {
            new { name, size = 100, digest = "sha256:" + new string('a', 64), browser_download_url = $"https://github.com/koteiik/GameLocalizer/releases/download/v0.5.0/{name}" }
        }});
        Assert.Equal(name, ReleaseClient.ParseRelease(json, installed)!.Asset);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

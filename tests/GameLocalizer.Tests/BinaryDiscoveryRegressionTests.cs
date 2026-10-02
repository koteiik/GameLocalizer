using System.Text;
using GameLocalizer.Infrastructure.FileSystem;
using Xunit;
namespace GameLocalizer.Tests;
public sealed class BinaryDiscoveryRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BinaryDiscovery", Guid.NewGuid().ToString("N"));
    public BinaryDiscoveryRegressionTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private async Task<IReadOnlyList<string>> Read(byte[] bytes, UnityDiscoveryStatistics? stats = null)
    {
        var file = Path.Combine(root, "sharedassets0.assets"); await File.WriteAllBytesAsync(file, bytes);
        var result = await BinaryStringCandidates.ReadAsync(file, default, _ => true, stats);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file)); return result;
    }
    [Theory]
    [InlineData("Give Advice")][InlineData("Дать совет")][InlineData("おしゃべり")]
    [InlineData("Menu メニュー Меню")][InlineData("Save 😀💾")]
    public async Task Utf8CandidateLanguages(string text) => Assert.Contains(text, await Read(Encoding.UTF8.GetBytes("\0" + text + "\0")));
    [Fact] public async Task MultibyteAcrossReadBoundary()
    {
        var bytes = new byte[BinaryStringCandidates.BufferBytes - 1].Concat(Encoding.UTF8.GetBytes("日本語 😀 Меню\0")).ToArray();
        Assert.Contains("日本語 😀 Меню", await Read(bytes));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task InvalidOrIncompleteRunDoesNotHideFollowingCandidate(bool incomplete)
    {
        var bytes = Encoding.UTF8.GetBytes("\0Give Advice\0").Concat(incomplete ? new byte[]{0xE3,0x81} : new byte[]{0xFF,0xFE,0,0xFF,0xFF}).ToArray();
        var stats = new UnityDiscoveryStatistics(); Assert.Contains("Give Advice", await Read(bytes, stats)); Assert.True(stats.InvalidCandidateRuns > 0);
    }
    [Fact] public async Task LargeBinaryWithReadableIslandAndOversizedRun()
    {
        var bytes = Enumerable.Repeat((byte)0xFF, 200000).Concat(new byte[]{0}).Concat(Encoding.UTF8.GetBytes("Give Item\0")).Concat(Enumerable.Repeat((byte)'A', 2000)).Concat(new byte[]{0}).ToArray();
        var result = await Read(bytes); Assert.Contains("Give Item", result); Assert.DoesNotContain(result, s => s.Length > 1024);
    }
    [Fact] public async Task LargeTextCandidatesAcrossManyChunks()
    {
        var text = string.Concat(Enumerable.Repeat("おしゃべり Меню Save 😀\n", 10000)); Assert.Contains("おしゃべり Меню Save 😀", await Read(Encoding.UTF8.GetBytes(text)));
    }
    [Fact] public async Task CompressedContainerIsExplicitlyUnsupported()
    {
        var stats = new UnityDiscoveryStatistics(); Assert.Empty(await Read(Encoding.UTF8.GetBytes("UnityFS\0Give Advice\0"), stats)); Assert.Equal(1, stats.UnsupportedContainers);
    }
    public static Task<IReadOnlyList<string>> LegacyOverflow(string path, CancellationToken ct, Func<string,bool> accept)
    {
        var decoder = Encoding.UTF8.GetDecoder(); var chars = new char[65536];
        decoder.GetChars(new byte[]{0xE3}, 0, 1, chars, 0, false);
        decoder.GetChars(Enumerable.Repeat((byte)'A',65536).ToArray(),0,65536,chars,0,false);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
    [Fact] public async Task ReproducedLegacyOverflowLoggedPerFile()
    {
        var file = Path.Combine(root,"sharedassets0.assets"); await File.WriteAllBytesAsync(file,[0]);
        var log = new ScanDiagnosticLog(Path.Combine(root,"logs")); var discovery = new UiResourceDiscovery(ScanRegressionTests.Adapters(),log,LegacyOverflow);
        Assert.Empty(await discovery.DiscoverAsync(root,default)); Assert.Single(discovery.Statistics.Errors);
        var detail = File.ReadAllText(log.LastLogPath!); Assert.Contains("sharedassets0.assets",detail); Assert.Contains("GetChars",detail); Assert.Contains("StackTrace",detail);
    }
}

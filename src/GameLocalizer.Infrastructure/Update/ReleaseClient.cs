using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameLocalizer.Infrastructure.Update;

public sealed record SemanticVersion(int Major, int Minor, int Patch, string Prerelease = "") : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string value)
    {
        var match = Regex.Match(value, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
        if (!match.Success) throw new FormatException("Некорректная semantic version");
        var pre = match.Groups[4].Value;
        if (pre.Split('.').Any(p => p.Length > 1 && p.All(char.IsDigit) && p[0] == '0')) throw new FormatException("Некорректная prerelease version");
        return new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), pre);
    }
    public int CompareTo(SemanticVersion? other)
    {
        if (other == null) return 1;
        foreach (var n in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) }) if (n != 0) return n;
        if (Prerelease == other.Prerelease) return 0;
        if (Prerelease.Length == 0) return 1; if (other.Prerelease.Length == 0) return -1;
        var a = Prerelease.Split('.'); var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var na = System.Numerics.BigInteger.TryParse(a[i], out var va); var nb = System.Numerics.BigInteger.TryParse(b[i], out var vb);
            var compared = na && nb ? va.CompareTo(vb) : na != nb ? na ? -1 : 1 : string.CompareOrdinal(a[i], b[i]);
            if (compared != 0) return compared;
        }
        return a.Length.CompareTo(b.Length);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease.Length == 0 ? "" : "-" + Prerelease);
}

public sealed record AppRelease(string Tag, string Notes, long Size, string? Sha256, string Asset = ReleaseClient.AssetName)
{
    public SemanticVersion Version => SemanticVersion.Parse(Tag);
    public string PageUrl => $"https://github.com/{ReleaseClient.Repository}/releases/tag/{Tag}";
    public string DownloadUrl => $"https://github.com/{ReleaseClient.Repository}/releases/download/{Tag}/{Asset}";
    public bool CanInstall => Size > 0 && Size <= ReleaseClient.MaximumZipBytes && Sha256 != null && Regex.IsMatch(Sha256, "^[a-fA-F0-9]{64}$");
}
public record UpdateDownloadProgress(long Received, long Total);

/// <summary>Only the official repository supplies executable updates. No URL is accepted from release notes.</summary>
public sealed class ReleaseClient
{
    public const string Repository = "koteiik/GameLocalizer";
    public const string InstallerAssetName = "GameLocalizer-Setup.exe";
    public const string AssetName = "GameLocalizer-win-x64.zip";
    public const long MaximumZipBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Shared = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
    private readonly HttpClient client;
    private readonly bool installed;
    public ReleaseClient(HttpClient? client = null, bool installed = false) { this.client = client ?? Shared; this.installed = installed; }
    public static bool IsNewer(AppRelease release, string current) => release.Version.Prerelease.Length == 0 && release.Version.CompareTo(SemanticVersion.Parse(current)) > 0;
    public static AppRelease? ParseRelease(string json, bool installed = false)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.GetProperty("prerelease").GetBoolean() || root.GetProperty("draft").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = SemanticVersion.Parse(tag);
        if (version.Prerelease.Length != 0 || tag != "v" + version) return null;
        var assetName = installed ? InstallerAssetName : AssetName;
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == assetName).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("В релизе отсутствует однозначный пакет Windows.");
        var asset = assets[0];
        var expected = $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";
        if (asset.GetProperty("browser_download_url").GetString() != expected) throw new InvalidDataException("Недоверенный адрес обновления.");
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        var sha = digest != null && Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") ? digest[7..] : null;
        return new(tag, root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", asset.GetProperty("size").GetInt64(), sha, assetName);
    }
    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("GameLocalizer/" + Core.Models.ApplicationVersion.Current);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); return request;
    }
    public async Task<AppRelease?> LatestAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = Request($"https://api.github.com/repos/{Repository}/releases/latest");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if ((int)response.StatusCode != 200) throw new InvalidDataException("Неожиданный ответ GitHub");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream(); var bytes = new byte[81920]; int read;
        while ((read = await input.ReadAsync(bytes, timeout.Token)) > 0)
        {
            if (buffer.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Ответ GitHub слишком велик.");
            buffer.Write(bytes, 0, read);
        }
        return ParseRelease(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), installed);
    }
    public async Task<string> DownloadAsync(AppRelease release, string updatesRoot, IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        if (!release.CanInstall || release.Asset is not (AssetName or InstallerAssetName) || release.Tag != "v" + release.Version || release.Version.Prerelease.Length != 0) throw new InvalidDataException("GitHub не предоставил доверенный SHA256. Скачайте ZIP вручную.");
        var directory = Path.Combine(updatesRoot, release.Tag); UpdatePaths.NoLinks(directory); Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, release.Asset); var partial = final + ".partial-" + Guid.NewGuid().ToString("N");
        HttpResponseMessage? response = null;
        try
        {
            var url = release.DownloadUrl;
            for (var redirect = 0; redirect < 4; redirect++)
            {
                using var request = Request(url);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.MovedPermanently)) break;
                var next = response.Headers.Location;
                // GitHub serves assets through its own signed CDN. Only this exact host/path is accepted.
                if (next == null || !next.IsAbsoluteUri || next.Scheme != "https" || next.Host != "release-assets.githubusercontent.com" || next.Port != 443 || next.UserInfo.Length != 0 || !next.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal)) throw new InvalidDataException("Недоверенное перенаправление обновления.");
                url = next.AbsoluteUri; response.Dispose(); response = null;
            }
            if (response == null || response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("Не удалось скачать обновление.");
            if (response.Content.Headers.ContentLength is { } size && size != release.Size) throw new InvalidDataException("Неверный размер ZIP.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            long received = 0;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920]; int read; var timer = System.Diagnostics.Stopwatch.StartNew();
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    received += read; if (received > release.Size) throw new InvalidDataException("ZIP превышает ожидаемый размер.");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (timer.ElapsedMilliseconds >= 150) { progress?.Report(new(received, release.Size)); timer.Restart(); }
                }
                await output.FlushAsync(ct); output.Flush(true);
            }
            if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
            ct.ThrowIfCancellationRequested(); UpdatePaths.NoLinks(final); File.Move(partial, final, true); progress?.Report(new(received, release.Size)); return final;
        }
        finally { response?.Dispose(); if (File.Exists(partial)) File.Delete(partial); }
    }
}

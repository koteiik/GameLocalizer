using System.Text;
namespace GameLocalizer.Infrastructure.FileSystem;
/// <summary>Only bounded, delimiter-separated candidate runs are decoded. Binary files are never decoded as whole text.</summary>
public static class BinaryStringCandidates
{
    public const int BufferBytes = 65536;
    public const int MaxCandidateBytes = 1024;
    public const long MaxBytesPerFile = 64L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static async Task<IReadOnlyList<string>> ReadAsync(string path, CancellationToken ct, Func<string, bool> accept, UnityDiscoveryStatistics? statistics = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferBytes, true);
        var header = new byte[64]; var headerCount = await stream.ReadAsync(header, ct); stream.Position = 0;
        var signature = Encoding.ASCII.GetString(header, 0, headerCount);
        if (signature.StartsWith("UnityFS\0", StringComparison.Ordinal) || signature.StartsWith("UnityWeb\0", StringComparison.Ordinal) || signature.StartsWith("UnityRaw\0", StringComparison.Ordinal))
        {
            if (statistics != null) { statistics.UnsupportedContainers++; statistics.Resources.Add(new(path,"Unsupported binary bundle container / compression not decoded")); }
            return []; // Bundle container; compression/serialized layout unsupported, no decoding of binary data.
        }
        var bytes = new byte[BufferBytes]; var run = new List<byte>(MaxCandidateBytes);
        var ascii16 = new[] { new StringBuilder(), new StringBuilder() }; var overflow16 = new bool[2];
        var results = new HashSet<string>(StringComparer.Ordinal); bool overflow = false; int previous = -1; long offset = 0;
        void Emit()
        {
            if (!overflow && run.Count > 1)
            {
                try { var text = StrictUtf8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(run)).Trim(); if (text.Length > 1 && text.All(c => !char.IsControl(c)) && accept(text) && results.Count < UiResourceDiscovery.ResultLimit) results.Add(text); }
                catch (DecoderFallbackException) { if (statistics != null) statistics.InvalidCandidateRuns++; } // Invalid runs are expected binary data, not text-file failures.
            }
            run.Clear(); overflow = false;
        }
        void Feed16(int parity, char c)
        {
            if (c is >= ' ' and <= '~') { if (!overflow16[parity] && ascii16[parity].Length < MaxCandidateBytes / 2) ascii16[parity].Append(c); else overflow16[parity] = true; return; }
            if (!overflow16[parity] && ascii16[parity].Length > 1)
            {
                var text = ascii16[parity].ToString().Trim(); if (text.Length > 1 && accept(text) && results.Count < UiResourceDiscovery.ResultLimit) results.Add(text);
            }
            ascii16[parity].Clear(); overflow16[parity] = false;
        }
        while (offset < MaxBytesPerFile)
        {
            ct.ThrowIfCancellationRequested(); var count = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(BufferBytes, MaxBytesPerFile - offset)), ct); if (count == 0) break;
            for (var i = 0; i < count; i++, offset++)
            {
                var value = bytes[i];
                if (value is >= 32 and not 127) { if (run.Count < MaxCandidateBytes && !overflow) run.Add(value); else overflow = true; } else Emit();
                if (previous >= 0) Feed16((int)((offset - 1) % 2), value == 0 && previous is >= 32 and <= 126 ? (char)previous : '\0'); previous = value;
            }
            if (results.Count >= UiResourceDiscovery.ResultLimit) break;
        }
        if (statistics != null) { if (stream.Position < stream.Length) statistics.BoundedFiles++; statistics.Resources.Add(new(path,stream.Position < stream.Length ? "Binary / bounded partial inspection" : "Binary / read-only candidate inspection")); }
        if (stream.Position == stream.Length) { Emit(); Feed16(0, '\0'); Feed16(1, '\0'); } // Never expose a truncated run at a byte budget boundary.
        return results.ToArray();
    }
}

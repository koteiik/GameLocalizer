using System.Text.Json;
namespace GameLocalizer.Infrastructure.FileSystem;
public sealed class ScanDiagnosticLog(string? directory = null)
{
    private readonly string folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLocalizer", "Logs", "ScanDiagnostics");
    private readonly object gate = new();
    public string? LastLogPath { get; private set; }
    public void Record(string file, string stage, Exception error, string encoding = "UTF-8 / codepage 65001", string api = "Bounded candidate extraction", int inputBufferBytes = 65536, int outputBufferChars = 0)
    {
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            LastLogPath ??= Path.Combine(folder, "ScanDiagnostic_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");
            File.AppendAllText(LastLogPath, JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.Now, File = Path.GetFullPath(file), Stage = stage, Encoding = encoding, API = api, InputBufferBytes = inputBufferBytes, OutputBufferChars = outputBufferChars, Exception = error.ToString(), StackTrace = error.StackTrace }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        }
    }
}
public record UnityDiscoveryIssue(string File, string Stage, string Message);
public record UnityResourceInspection(string File, string Status);
public sealed class UnityDiscoveryStatistics
{
    public int ResourcesExamined { get; set; }
    public int UnsupportedContainers { get; set; }
    public int BoundedFiles { get; set; }
    public long InvalidCandidateRuns { get; set; }
    public List<UnityDiscoveryIssue> Errors { get; } = [];
    public List<UnityResourceInspection> Resources { get; } = [];
}

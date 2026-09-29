using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.TranslationProviders;

public record ModelHostRequest(string Command, string? Directory = null, TranslationDevice Device = TranslationDevice.CPU, IReadOnlyList<string>? Texts = null);
public record ModelHostResponse(bool Success, string Device, IReadOnlyList<string>? Texts = null, string? Error = null, bool OutOfMemory = false);

/// <summary>Owns one hidden local child process. Native crashes and cancellation cannot take down WPF.</summary>
public sealed class IsolatedTranslationRuntime(string? hostPath = null) : ITranslationRuntime
{
    private Process? process;
    private Task? drainErrors;
    public bool IsLoaded { get; private set; }
    public string Device { get; private set; } = "CPU";
    public long MemoryBytes
    {
        get { try { var child = process; if (child == null || child.HasExited) return 0; child.Refresh(); return child.PrivateMemorySize64; } catch (InvalidOperationException) { return 0; } }
    }
    public int LoadCount { get; private set; }
    public int InferenceCount { get; private set; }
    public int? ProcessId => process is { HasExited: false } value ? value.Id : null;
    public async Task LoadAsync(string directory, TranslationDevice device, CancellationToken ct)
    {
        Unload(); ct.ThrowIfCancellationRequested();
        var executable = hostPath ?? Path.Combine(AppContext.BaseDirectory, "GameLocalizer.ModelHost.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Локальный runtime отсутствует. Распакуйте все файлы ZIP приложения.", executable);
        process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить локальный runtime");
        // Drain native diagnostics without retaining/logging text or paths.
        var errors = process.StandardError;
        drainErrors = Task.Run(async () => { try { var buffer = new char[4096]; while (await errors.ReadAsync(buffer) != 0) { } } catch (IOException) { } catch (ObjectDisposedException) { } });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(device == TranslationDevice.GPU ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(60));
            var response = await Send(new("load", directory, device), timeout.Token);
            Device = response.Device; IsLoaded = true; LoadCount++;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Unload(); throw new TimeoutException("Превышено время загрузки модели на выбранном устройстве"); }
        catch { Unload(); throw; }
    }
    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> text, CancellationToken ct)
    {
        if (!IsLoaded) throw new InvalidOperationException("Model not loaded");
        InferenceCount++;
        try { return (await Send(new("translate", Texts: text), ct)).Texts ?? throw new InvalidDataException("Runtime omitted translations"); }
        catch (OperationCanceledException) { Unload(); throw; }
    }
    private async Task<ModelHostResponse> Send(ModelHostRequest request, CancellationToken ct)
    {
        var child = process ?? throw new InvalidOperationException("Model host stopped");
        try
        {
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct); await child.StandardInput.FlushAsync(ct);
            var line = await child.StandardOutput.ReadLineAsync(ct);
            if (line == null) throw new InvalidOperationException("Локальный runtime завершился. Устройство несовместимо с моделью.");
            var response = JsonSerializer.Deserialize<ModelHostResponse>(line) ?? throw new InvalidDataException("Invalid model response");
            if (!response.Success)
            {
                if (response.OutOfMemory) throw new OutOfMemoryException(response.Error);
                throw new InvalidOperationException(response.Error ?? "Model runtime failed");
            }
            return response;
        }
        catch (IOException e) { throw new InvalidOperationException("Локальный runtime недоступен", e); }
    }
    public void Unload()
    {
        IsLoaded = false; var child = process; process = null;
        if (child == null) return;
        try { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
        finally { child.Dispose(); drainErrors = null; }
    }
    public void Dispose() => Unload();
}

using System.IO;
using Microsoft.Extensions.Logging;
using System.Windows.Input;
using GameLocalizer.Infrastructure.FileSystem;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    private readonly ScanDiagnosticLog scanDiagnostics;
    private readonly UiResourceDiscovery uiDiscovery;
    private int optionalDiscoveryFailures;
    private string scanStatus = "NOT STARTED";
    public string ScanStatus { get => scanStatus; private set => Set(ref scanStatus, value); }
    public int DiscoveryErrorCount => optionalDiscoveryFailures + uiDiscovery.Statistics.Errors.Count;
    public int UnsupportedUnityResourceCount => uiDiscovery.Statistics.ResourcesExamined;
    public string? ScanDiagnosticLogPath => scanDiagnostics.LastLogPath ?? uiDiscovery.LastDiagnosticLogPath ?? workspace.ScanDiagnosticLogPath;
    public ICommand OpenScanLogCommand { get; private set; } = null!;
    private async Task RefreshUnityDiscoveryAsync(Operation op, CancellationToken ct)
    {
        if (op.Game == null) return;
        optionalDiscoveryFailures = 0;
        try
        {
            var unsupported = await Task.Run(() => uiDiscovery.DiscoverAsync(op.Game.Path, ct), ct);
            if (!IsCurrent(op)) return;
            await repository.SaveUiDiscoveryAsync(op.Game.Path,unsupported,ct);
            if (!IsCurrent(op)) return;
            UnsupportedUi.Clear(); foreach (var entry in unsupported) UnsupportedUi.Add(entry);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            optionalDiscoveryFailures++; scanDiagnostics.Record(op.Game.Path, "Optional Unity discovery", error);
            logger.LogWarning(error, "Optional Unity discovery failed for {Root}; supported text results preserved", op.Game.Path);
        }
        Changed(nameof(DiscoveryErrorCount)); Changed(nameof(UnsupportedUnityResourceCount)); Changed(nameof(CoverageSummary)); Changed(nameof(UiDiscoveryNotes)); Changed(nameof(ScanDiagnosticLogPath));
        CommandManager.InvalidateRequerySuggested();
    }
    private void OpenScanLog()
    {
        if (ScanDiagnosticLogPath is string path && File.Exists(path)) Open(path);
        else { Status = "Диагностический журнал не найден."; CommandManager.InvalidateRequerySuggested(); }
    }
}

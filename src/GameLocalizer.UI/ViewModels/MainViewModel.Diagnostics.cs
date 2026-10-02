using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using GameLocalizer.Infrastructure.FileSystem;
namespace GameLocalizer.UI.ViewModels;
public sealed partial class MainViewModel
{
    private string? diagnosticReportPath;
    private readonly ApplyDiagnosticReportLocator diagnosticReports;
    public string? DiagnosticReportPath => diagnosticReportPath;
    public string DiagnosticReportToolTip => diagnosticReportPath != null ? "Открыть последний отчёт применения." : "Отчёт появится после применения перевода.";
    public string EngineSummary => string.Join(" · ", Engine.Split(" · ").Take(2));
    public string StatusSummary => Status.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Готово";
    public string ProgressSummary => ProgressText.StartsWith("Просмотрено файлов:") && ProgressText.Split('·') is { Length: 3 } parts
        ? $"{parts[2].Trim()} · {parts[0].Trim()}" : ProgressText;
    public void RefreshDiagnosticReport()
    {
        diagnosticReportPath = diagnosticReports.FindLatest(SelectedGame?.Path);
        Changed(nameof(DiagnosticReportPath)); Changed(nameof(DiagnosticReportToolTip));
        CommandManager.InvalidateRequerySuggested();
    }
    private void OpenDiagnosticReport()
    {
        var path = diagnosticReportPath;
        if (path == null || !File.Exists(path)) { Status = "Диагностический отчёт не найден."; RefreshDiagnosticReport(); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Status = File.Exists(path) ? "Не удалось открыть диагностический отчёт." : "Диагностический отчёт не найден.";
            RefreshDiagnosticReport();
        }
    }
}

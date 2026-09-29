using System.Windows.Input;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using GameLocalizer.UI.Commands;
namespace GameLocalizer.UI.ViewModels;

public sealed class OfflineSettingsViewModel : Observable
{
    private readonly ITranslationModelManager models;
    private readonly IHardwareDetectionService hardware;
    private CancellationTokenSource? cancellation;
    private bool busy, ready;
    private string status = "Модель не проверена", hardwareText = "Определение оборудования…";
    private double progress;
    public OfflineSettings Settings { get; }
    public string ModelInfo => $"{models.GetModelInfo().Name}\nВерсия: {models.GetModelInfo().Version}\nЗагрузка / на диске: {models.GetModelInfo().DownloadBytes / 1048576.0:F1} MiB (при обновлении нужно до 2× места).\n{models.GetModelInfo().Requirements}\nЛицензия: {models.GetModelInfo().License}";
    public bool IsInstalled => models.IsInstalled;
    public bool Ready { get => ready; private set { Set(ref ready, value); Changed(nameof(ReadyDescription)); } }
    public string ReadyDescription => Ready ? "Offline Ready" : IsInstalled ? "Installed · требуется проверка" : "Missing";
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(Idle)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    public bool Idle => !Busy;
    public string Status { get => status; private set => Set(ref status, value); }
    public string HardwareText { get => hardwareText; private set => Set(ref hardwareText, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public TranslationDevice[] Devices => Enum.GetValues<TranslationDevice>();
    public ICommand DownloadCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CancelCommand { get; }
    public OfflineSettingsViewModel(ITranslationModelManager models, IHardwareDetectionService hardware, AppSettings settings)
    {
        this.models = models; this.hardware = hardware; Settings = settings.Offline;
        DownloadCommand = new AsyncCommand(() => DownloadAsync(default), () => Idle);
        VerifyCommand = new AsyncCommand(() => Run(async ct => { Ready = await models.VerifyModelAsync(ct); Status = Ready ? "Offline Ready · SHA256 проверен" : "Модель отсутствует или повреждена"; }), () => Idle);
        DeleteCommand = new AsyncCommand(() => Run(async ct => { await models.DeleteModelAsync(ct); Ready = false; Status = "Модель удалена. Translation Memory сохранена."; }), () => Idle);
        CancelCommand = new RelayCommand(() => cancellation?.Cancel(), () => Busy);
    }
    public async Task InitializeAsync()
    {
        await Run(async ct =>
        {
            var info = await Task.Run(hardware.Detect, ct);
            HardwareText = $"CPU: {info.Cpu}\nRAM: {info.RamBytes / 1073741824.0:F1} GB\nGPU: {info.Gpu}\nVRAM (данные драйвера): {(info.GpuMemoryBytes is { } bytes ? $"{bytes / 1073741824.0:F1} GB" : "недоступно")}\n" +
                (info.CanAttemptGpu ? "Auto: попытка DirectML GPU; при несовместимости — CPU." : "Рекомендуемый режим: CPU. Перевод может занять больше времени.");
            Ready = await models.VerifyModelAsync(ct); Status = Ready ? "Offline Ready" : "Для офлайн-перевода скачайте модель EN → RU.";
        });
    }
    public Task DownloadAsync(CancellationToken ct) => Run(async token =>
    {
        var report = new Progress<ModelDownloadProgress>(p => { Progress = 100.0 * p.DownloadedBytes / p.TotalBytes; Status = $"{p.File}: {p.DownloadedBytes / 1048576.0:F1} / {p.TotalBytes / 1048576.0:F1} MiB"; });
        await models.DownloadModelAsync(report, token); Ready = await models.VerifyModelAsync(token); Status = Ready ? "Offline Ready · SHA256 проверен" : "Ошибка проверки модели";
    }, ct);
    private async Task Run(Func<CancellationToken, Task> action, CancellationToken outer = default)
    {
        if (Busy) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(outer); cancellation = source; Busy = true;
        try { await action(source.Token); }
        catch (OperationCanceledException) { Status = "Операция с моделью отменена"; }
        catch (Exception e) { Ready = false; Status = "Ошибка: " + e.Message; }
        finally { cancellation = null; Busy = false; Changed(nameof(ReadyDescription)); Changed(nameof(IsInstalled)); }
    }
}

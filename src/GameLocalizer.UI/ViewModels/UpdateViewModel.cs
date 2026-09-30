using System.Windows;
using System.Windows.Input;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.Commands;
using GameLocalizer.UI.Views;

namespace GameLocalizer.UI.ViewModels;

public sealed class UpdateViewModel : Observable
{
    private readonly ReleaseClient client;
    private readonly Func<bool> operationBusy;
    private readonly Func<Task> saveAndUnload;
    private readonly InstallationInfo installation;
    private readonly IInstallerUpdateService installer;
    private readonly Action shutdown;
    private readonly string updatesRoot;
    public string InstallationDescription => $"{installation.Mode} · {installation.Version}\n{installation.Directory}\n" + (installation.Writable ? "Папка доступна для записи" : "Нет доступа для записи");
    private CancellationTokenSource? cancellation;
    private AppRelease? release;
    private bool busy, checking, visible;
    private string status = "Обновления ещё не проверены.", banner = "", latestVersion = "Не проверена", notes = "", notesTitle = "Что нового";
    private double progress;
    public string CurrentVersion => ApplicationVersion.Label;
    public string LatestVersion { get => latestVersion; private set => Set(ref latestVersion, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Banner { get => banner; private set => Set(ref banner, value); }
    public string Notes { get => notes; private set => Set(ref notes, value); }
    public string NotesTitle { get => notesTitle; private set => Set(ref notesTitle, value); }
    public bool Visible { get => visible; private set => Set(ref visible, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); RefreshAvailability(); } }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public bool HandoffStarted { get; private set; }
    public bool HasNewVersion => release != null && ReleaseClient.IsNewer(release, CurrentVersion);
    public bool CanUpdate => HasNewVersion && release!.CanInstall && (!installation.Installed || installation.Writable && release.Asset == ReleaseClient.InstallerAssetName) && !UpdateInstaller.IsOperationBlocked(operationBusy(), false, Busy);
    public string BlockReason => operationBusy() ? "Завершите текущую операцию перед обновлением." : installation.Installed && !installation.Writable ? "Папка установки недоступна для записи." : release is { CanInstall: false } ? "Нет доверенного SHA256 / пакета Windows. Доступна ручная загрузка." : "";
    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand NotesCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ManualDownloadCommand { get; }
    public UpdateViewModel(ReleaseClient client, Func<bool> operationBusy, Func<Task> saveAndUnload, InstallationInfo? installation = null, IInstallerUpdateService? installer = null, Action? shutdown = null, string? updatesRoot = null)
    {
        this.client = client; this.operationBusy = operationBusy; this.saveAndUnload = saveAndUnload;
        this.installation = installation ?? new InstallationInfoService().GetInfo();
        this.updatesRoot = updatesRoot ?? UpdateHandoff.Root;
        this.installer = installer ?? new InstallerUpdateService(); this.shutdown = shutdown ?? (() => Application.Current.Shutdown());
        CheckCommand = new AsyncCommand(() => CheckAsync(default), () => !checking && !Busy);
        UpdateCommand = new AsyncCommand(InstallAsync, () => CanUpdate);
        NotesCommand = new RelayCommand(() => new ReleaseNotesWindow(this) { Owner = Application.Current.MainWindow }.ShowDialog(), () => Notes.Length > 0);
        LaterCommand = new RelayCommand(() => Visible = false, () => !Busy);
        CancelCommand = new RelayCommand(Cancel, () => Busy && !HandoffStarted);
        ManualDownloadCommand = new RelayCommand(() => MainViewModel.Open("https://github.com/koteiik/GameLocalizer/releases/latest"));
    }
    public void RefreshAvailability() { Changed(nameof(CanUpdate)); Changed(nameof(BlockReason)); Changed(nameof(HasNewVersion)); CommandManager.InvalidateRequerySuggested(); }
    public void Cancel() => cancellation?.Cancel();
    public void ShowUpdated(UpdateNotification notification)
    {
        Banner = "GameLocalizer обновлён до " + notification.Version; Status = "Обновление установлено. Предыдущая версия сохранена в резервной копии."; Notes = notification.Notes; NotesTitle = "GameLocalizer " + notification.Version; Visible = true;
    }
    public void ShowInstallerUpdated()
    {
        Banner = "GameLocalizer обновлён до " + CurrentVersion; Status = "Установка завершена. Модель, настройки и память переводов сохранены."; Visible = true;
    }
    public async Task CheckAsync(CancellationToken ct)
    {
        if (checking || Busy) return; checking = true; Status = "Проверка GitHub Releases…";
        try
        {
            release = await client.LatestAsync(ct); LatestVersion = release?.Tag ?? "Нет стабильного релиза";
            if (release == null) Status = "Стабильный релиз не найден.";
            else
            {
                if (Notes.Length == 0 || HasNewVersion) { Notes = release.Notes; NotesTitle = "GameLocalizer " + release.Tag; }
                Status = HasNewVersion ? "Доступна версия " + release.Tag : "Установлена последняя версия.";
                if (HasNewVersion) { Banner = Status; Visible = true; }
            }
        }
        catch (OperationCanceledException) { Status = "Проверка отменена или GitHub не отвечает."; }
        catch (Exception e) { Status = "Не удалось проверить обновления: " + UpdateInstaller.FriendlyError(e); }
        finally { checking = false; RefreshAvailability(); }
    }
    public async Task InstallAsync()
    {
        if (!CanUpdate || release == null) { Status = BlockReason; return; }
        var selected = release; using var source = new CancellationTokenSource(); cancellation = source; Busy = true; Visible = true;
        try
        {
            await saveAndUnload(); source.Token.ThrowIfCancellationRequested();
            Status = "Скачивание обновления…"; Progress = 0;
            var report = new Progress<UpdateDownloadProgress>(p => { Progress = 100.0 * p.Received / p.Total; Status = $"Скачивание обновления: {p.Received / 1048576.0:F1} MB / {p.Total / 1048576.0:F1} MB"; });
            var package = await client.DownloadAsync(selected, updatesRoot, report, source.Token);
            if (installation.Installed)
            {
                Status = "SHA256 проверен. Запуск установщика…";
                var start = await installer.PrepareAsync(selected, package, installation, source.Token);
                source.Token.ThrowIfCancellationRequested(); installer.Launch(start);
            }
            else
            {
                Status = "SHA256 проверен. Подготовка portable updater…";
                var journal = await UpdateHandoff.PrepareAsync(selected, package, source.Token);
                source.Token.ThrowIfCancellationRequested(); UpdateHandoff.Launch(journal);
            }
            HandoffStarted = true; shutdown();
        }
        catch (OperationCanceledException) { Status = "Обновление отменено. Установленная версия не изменена."; }
        catch (Exception e) { Status = "Обновление не выполнено: " + UpdateInstaller.FriendlyError(e); }
        finally { cancellation = null; Busy = false; }
    }
}

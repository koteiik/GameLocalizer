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
    public bool CanUpdate => HasNewVersion && release!.CanInstall && !UpdateInstaller.IsOperationBlocked(operationBusy(), false, Busy);
    public string BlockReason => operationBusy() ? "Завершите текущую операцию перед обновлением." : release is { CanInstall: false } ? "Нет доверенного SHA256 / Windows ZIP. Установка заблокирована; доступна ручная загрузка." : "";
    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand NotesCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ManualDownloadCommand { get; }
    public UpdateViewModel(ReleaseClient client, Func<bool> operationBusy, Func<Task> saveAndUnload)
    {
        this.client = client; this.operationBusy = operationBusy; this.saveAndUnload = saveAndUnload;
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
            var zip = await client.DownloadAsync(selected, UpdateHandoff.Root, report, source.Token);
            Status = "SHA256 проверен. Проверка ZIP и подготовка updater…";
            var journal = await UpdateHandoff.PrepareAsync(selected, zip, source.Token);
            source.Token.ThrowIfCancellationRequested();
            UpdateHandoff.Launch(journal); HandoffStarted = true;
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { Status = "Обновление отменено. Установленная версия не изменена."; }
        catch (Exception e) { Status = "Обновление не выполнено: " + UpdateInstaller.FriendlyError(e); }
        finally { cancellation = null; Busy = false; }
    }
}

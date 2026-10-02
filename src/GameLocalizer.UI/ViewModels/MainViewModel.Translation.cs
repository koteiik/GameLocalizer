using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GameLocalizer.Core.Models;
using GameLocalizer.UI.Views;
using GameLocalizer.UI.Commands;
namespace GameLocalizer.UI.ViewModels;

public sealed partial class MainViewModel
{
    public ICommand RetranslateCommand { get; }
    public string ProviderDescription => $"{workspace.Provider} · Offline model: {offlineSettings?.ReadyDescription ?? "Missing"}";
    private static string Ask(string title, string text, params string[] buttons)
    {
        var dialog = new ChoiceWindow(title, text, buttons) { Owner = Application.Current.MainWindow }; dialog.ShowDialog(); return dialog.Choice;
    }
    public Task StartTranslationInteractiveAsync(bool retranslate) => Run(async (op, ct) =>
    {
        if (op.Game == null || op.Session == null) return;
        await FlushEditsAsync(); ct.ThrowIfCancellationRequested();
        var ignore = false;
        if (retranslate)
        {
            var choice = Ask("Перевести заново", "Ручные переводы Manual сохраняются при любом выборе.", "Использовать память", "Игнорировать память", "Отмена");
            if (choice == "Отмена") return; ignore = choice == "Игнорировать память";
        }
        else if (workspace.FindIncomplete(op.Game) != null)
        {
            var choice = Ask("Продолжение перевода", "Предыдущий перевод не завершён. Готовые строки сохранены в Translation Memory. Новый job также использует память; повторный перевод доступен отдельной кнопкой.", "Продолжить", "Начать новый", "Отмена");
            if (choice == "Отмена") return;
        }
        Status = "Подготовка: проверка сохранённых переводов…";
        var preflight = await Task.Run(() => workspace.PreflightAsync(op.Game, op.Session, ignore, ct), ct);
        if (!IsCurrent(op)) return;
        var selected = Ask("Перед переводом", $"Выбрано строк: {preflight.TotalStrings:N0}\nУже готово / из памяти: {preflight.CachedStrings:N0}\nРучные (не заменяются): {preflight.ManualStrings:N0}\nТребует перевода: {preflight.RequiresTranslation:N0}\nМодель: {preflight.Model}\nУстройство: {Settings.Offline.Device} (при недоступности GPU используется CPU)\nОбъём: {preflight.Characters:N0} символов / ~{preflight.EstimatedTokens:N0} токенов\n\nПереводы сохраняются в памяти и Preview. Игровые файлы меняет только «Применить».", "Тест 20 строк", "Начать перевод", "Отмена");
        if (selected == "Отмена") return;
        if (Settings.TranslationProvider == "Offline" && preflight.RequiresTranslation > 0 && offlineSettings is { Ready: false })
        {
            var choice = Ask("Офлайн-модель не установлена или не проверена", offlineSettings.ModelInfo, "Скачать модель", "Настройки", "Отмена");
            if (choice == "Настройки")
            {
                var profile = workspace.Profile; new SettingsWindow(Settings, offlineSettings, glossary) { Owner = Application.Current.MainWindow }.ShowDialog(); settingsService.Save(Settings); Changed(nameof(ProviderDescription));
                if (profile != workspace.Profile) { session = null; ClearPreview(); Status = "Переводчик изменён. Нажмите «Найти текст»."; return; }
            }
            else if (choice == "Скачать модель") await offlineSettings.DownloadAsync(ct);
            else return;
            if (!offlineSettings.Ready) { Status = offlineSettings.Status; return; }
        }
        ct.ThrowIfCancellationRequested();
        await ExecuteTranslationJobAsync(op, ignore, selected == "Тест 20 строк", false, ct);
        if (selected == "Тест 20 строк" && !ct.IsCancellationRequested)
        {
            var rows = await workspace.GetTestRowsAsync(op.Session, ct);
            if (IsCurrent(op)) new TranslationTestWindow(rows) { Owner = Application.Current.MainWindow }.ShowDialog();
        }
    });
    private TranslationJob? currentTranslationJob;
    private bool translating;
    public TranslationJob? CurrentTranslationJob => currentTranslationJob;
    public bool IsTranslating => translating;
    public double TranslationProgressPercent => currentTranslationJob?.ProgressPercent ?? 0;
    public string TranslationProgressText => currentTranslationJob is {} j ? $"Обработано {j.ProcessedCount:N0}/{j.TotalStrings:N0} · {j.ProgressPercent:F1}%" : "Подготовка перевода…";
    public bool HasTranslationIssues => HasTranslationFailures || currentTranslationJob?.SkippedStrings > 0;
    public bool HasTranslationFailures => currentTranslationJob?.FailedStrings > 0;
    public string TranslationOutcomeSummary => currentTranslationJob is {} j ? $"Переведено: {j.Successful:N0} · Ошибок: {j.FailedStrings:N0} · Пропущено: {j.SkippedStrings:N0}" : "";
    public string TranslationDiagnosticText { get; private set; } = "";
    public ICommand RetryFailedCommand => retryFailedCommand ??= new AsyncCommand(RetryFailedAsync, () => Idle && session != null && HasTranslationFailures);
    public ICommand ShowTranslationErrorsCommand => showTranslationErrorsCommand ??= new AsyncCommand(ShowTranslationErrorsAsync, () => Idle && session != null);
    private ICommand? retryFailedCommand, showTranslationErrorsCommand;
    public Task RetryFailedAsync() => Run(async (op, ct) => {
        if(op.Game == null || op.Session == null) return;
        await FlushEditsAsync(); await ExecuteTranslationJobAsync(op, false, false, true, ct);
    });
    public async Task ShowTranslationErrorsAsync() {
        IsTranslation = true; Search = ""; FileFilter = ""; MinimumConfidence = 0;
        Filter = "Все"; Mode = "Ошибки перевода"; PreviewTabIndex = 0;
        PreviewTask = RefreshPreviewAsync(); await PreviewTask;
    }
    private void NotifyTranslationJob() {
        foreach(var name in new[]{nameof(CurrentTranslationJob),nameof(IsTranslating),nameof(TranslationProgressPercent),nameof(TranslationProgressText),nameof(HasTranslationFailures),nameof(HasTranslationIssues),nameof(TranslationOutcomeSummary),nameof(TranslationDiagnosticText)}) Changed(name);
        NotifyWorkflow();
    }
    private async Task ExecuteTranslationJobAsync(Operation op, bool ignore, bool test, bool failedOnly, CancellationToken ct) {
        if(op.Game == null || op.Session == null) return;
        translating = true; currentTranslationJob = null; NotifyTranslationJob();
        void Accept(TranslationJobProgress p) {
            if(!IsCurrent(op)) return;
            currentTranslationJob = p.Job;
            TranslationDiagnosticText = $"Job: {p.Job.JobId} · Batch: {p.CurrentBatch} · Device: {p.Device} · RAM: {p.RamBytes / 1048576:N0} MiB";
            ProgressText = TranslationProgressText; NotifyTranslationJob();
        }
        var progress = new Progress<TranslationJobProgress>(Accept);
        try {
            var result = await Task.Run(() => workspace.RunTranslationJobAsync(op.Game, op.Session, ignore, test, progress, ct, failedOnly), ct);
            if(!IsCurrent(op)) return;
            currentTranslationJob = result;
            var label = result.Status switch { TranslationJobStatus.Completed => "Перевод завершён", TranslationJobStatus.PartiallyCompleted => "Перевод завершён с ошибками", TranslationJobStatus.Cancelled => "Перевод отменён", _ => "Перевод завершился с ошибкой" };
            Status = $"{label}. {TranslationOutcomeSummary}. Результаты сохранены.";
            ProgressText = TranslationProgressText;
        }
        finally { translating = false; NotifyTranslationJob(); }
    }

}

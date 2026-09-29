using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GameLocalizer.Core.Models;
using GameLocalizer.UI.Views;
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
        TranslationJobProgress? last = null;
        void ShowUsage()
        {
            if (!IsCurrent(op)) return;
            var job = last?.Job;
            ProgressText = $"RAM приложения + модели: {(Process.GetCurrentProcess().PrivateMemorySize64 + workspace.ModelMemoryBytes) / 1048576:N0} MiB · GPU usage: недоступно · Model loaded: {workspace.IsModelLoaded}\nBatch: {last?.CurrentBatch ?? 0} · {job?.TranslatedStrings + job?.CachedStrings ?? 0:N0} / {job?.TotalStrings ?? preflight.TotalStrings:N0} · {last?.Device ?? Settings.Offline.Device.ToString()}";
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += (_, _) => ShowUsage(); timer.Start();
        var progress = new Progress<TranslationJobProgress>(p => { last = p; ShowUsage(); });
        TranslationJob result;
        try { result = await Task.Run(() => workspace.RunTranslationJobAsync(op.Game, op.Session, ignore, selected == "Тест 20 строк", progress, ct), ct); }
        finally { timer.Stop(); ShowUsage(); }
        if (!IsCurrent(op)) return;
        Status = $"{result.Status}: переведено {result.TranslatedStrings:N0}, из памяти/готово {result.CachedStrings:N0}, ошибок {result.FailedStrings:N0}, отменено {result.CancelledStrings:N0}. Результаты сохранены. Проверьте Preview.";
        if (selected == "Тест 20 строк" && !ct.IsCancellationRequested)
        {
            var rows = await workspace.GetTestRowsAsync(op.Session, ct);
            if (IsCurrent(op)) new TranslationTestWindow(rows) { Owner = Application.Current.MainWindow }.ShowDialog();
        }
    });
}

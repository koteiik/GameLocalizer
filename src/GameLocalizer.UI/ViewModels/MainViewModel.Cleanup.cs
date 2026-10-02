using System.IO;
using System.Windows;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.UI.Views;
namespace GameLocalizer.UI.ViewModels;

public sealed partial class MainViewModel
{
    public Task CleanupSelectedAsync() => Run(async (op, ct) =>
    {
        if (op.Game == null) return;
        await FlushEditsAsync();
        var plan = await Task.Run(() => backup.PlanCleanupAsync(op.Game.Path, ct), ct);
        if (plan.Conflicts.Any(c => !c.CanForceDelete))
        {
            MessageBox.Show(string.Join("\n", plan.Conflicts.Select(c => c.Path + ": " + c.Message)), "Очистка остановлена");
            return;
        }
        var force = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in plan.Conflicts)
        {
            var hash = TextFiles.Hash(await File.ReadAllBytesAsync(BackupService.Resolve(op.Game.Path, conflict.Path), ct));
            var dialog = new ChoiceWindow("Изменённый файл", conflict.Path + "\n" + conflict.Message, "Оставить", "Удалить всё равно", "Отмена") { Owner = Application.Current.MainWindow };
            dialog.ShowDialog();
            if (dialog.Choice == "Отмена") return;
            if (dialog.Choice == "Удалить всё равно") force[conflict.Path] = hash;
        }
        var confirmation = new ChoiceWindow("Убрать GameLocalizer из этой игры?",
            $"Закройте игру.\nВосстановлено оригинальных файлов: {plan.FilesToRestore.Count}\nУдалено созданных файлов: {plan.FilesToDelete.Count - plan.SkippedFiles.Count + force.Count}\nУдалено пустых папок: до {plan.DirectoriesToDelete.Count}\nУдалена резервная копия GameLocalizer после проверки.\nНе будут удалены файлы, не принадлежащие GameLocalizer.", "Убрать", "Отмена") { Owner = Application.Current.MainWindow };
        if (confirmation.ShowDialog() != true) return;
        var result = await Task.Run(() => backup.CleanupAsync(op.Game.Path, force, ct), ct);
        await UpdateCacheAfterRestoreAsync(op.Game,ct);
        if (!IsCurrent(op)) return;
        await RefreshPreviewAsync(); ProgressText = "";
        op.Game.Status = "GameLocalizer не имеет активных изменений для этой игры.";
        Status = $"Очистка завершена. Восстановлено: {result.Restored} файлов. Удалено: {result.Deleted} файлов. Удалено папок: {result.DirectoriesDeleted}. Пропущено изменённых файлов: {result.Skipped}. " + op.Game.Status;
        if (!result.BackupRemoved) Status += " Папка резервной копии сохранена: в ней есть посторонние файлы.";
        MessageBox.Show(Status, "Очистка");
    });
}

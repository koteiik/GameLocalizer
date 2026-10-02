using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameLocalizer.Infrastructure.Runtime;
using GameLocalizer.UI.Commands;
using Microsoft.Win32;

namespace GameLocalizer.UI.ViewModels;

public sealed partial class MainViewModel
{
    public ICommand ExportCollectorCommand { get; private set; } = null!;
    public ICommand OpenCollectorDataFolderCommand { get; private set; } = null!;
    public string? RuntimeExportToolTip => RuntimeUi.Count == 0 ? "Сначала импортируйте строки Runtime UI." : null;

    private void InitializeRuntimeExportCommands()
    {
        RuntimeUi.CollectionChanged += (_, _) =>
        {
            Changed(nameof(RuntimeExportToolTip));
            CommandManager.InvalidateRequerySuggested();
        };
        ExportCollectorCommand = new AsyncCommand(ExportRuntimeUiInteractiveAsync, () => Idle && HasGame && RuntimeUi.Count > 0);
        OpenCollectorDataFolderCommand = new RelayCommand(() => OpenRuntimeUiDataFolder(), () => Idle && HasGame);
    }

    private async Task ExportRuntimeUiInteractiveAsync()
    {
        if (!Idle || game == null || RuntimeUi.Count == 0) return;
        try
        {
            Directory.CreateDirectory(RuntimeUiExport.DefaultDirectory);
            var dialog = new SaveFileDialog
            {
                Title = "Экспортировать Runtime UI", InitialDirectory = RuntimeUiExport.DefaultDirectory,
                FileName = RuntimeUiExport.SuggestedFileName(game!.Name, DateTimeOffset.Now),
                Filter = "JSON (*.json)|*.json|Текст (*.txt)|*.txt", DefaultExt = ".json",
                AddExtension = true, OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true) return;
            var companion = Path.ChangeExtension(dialog.FileName,
                Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase) ? ".txt" : ".json");
            if (File.Exists(companion) && MessageBox.Show($"Экспорт также заменит файл:\n{companion}\n\nПродолжить?",
                    "Экспорт Runtime UI", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await ExportRuntimeUiAsync(dialog.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            RuntimeCollectorStatus = "Ошибка экспорта: " + e.Message;
        }
    }

    public async Task ExportRuntimeUiAsync(string selectedPath)
    {
        if (!Idle || game == null || RuntimeUi.Count == 0) return;
        var selectedGame = game;
        var version = selectionVersion;
        var rows = RuntimeUiExport.Snapshot(RuntimeUi);
        Busy = true;
        try
        {
            var result = await Task.Run(() => RuntimeUiExport.Write(selectedPath, rows));
            if (ReferenceEquals(game, selectedGame) && selectionVersion == version)
            {
                RuntimeCollectorStatus = $"Экспортировано: {result.Count} строк\n{result.JsonPath}\n{result.TextPath}";
                Status = RuntimeCollectorStatus;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (ReferenceEquals(game, selectedGame) && selectionVersion == version)
                RuntimeCollectorStatus = "Ошибка экспорта: " + e.Message;
        }
        finally { Busy = false; }
    }

    public bool OpenRuntimeUiDataFolder(Action<string>? openDirectory = null)
    {
        if (game == null) return false;
        var success = RuntimeUiDataFolder.TryOpen(game.Path, openDirectory ?? (folder =>
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { folder } })), out var message);
        Status = message;
        if (!success) RuntimeCollectorStatus = message;
        return success;
    }
}

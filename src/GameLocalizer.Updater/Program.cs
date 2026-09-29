using System.Windows;
using System.IO;
using GameLocalizer.Infrastructure.Update;

namespace GameLocalizer.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] is not ("--install" or "--recover")) throw new ArgumentException("Updater запускается через кнопку «Обновить» в GameLocalizer.");
            var installer = new UpdateInstaller(UpdateHandoff.Root, new UpdateProcesses(UpdateHandoff.Root));
            var request = installer.ReadRequest(args[1]);
            if (UpdatePaths.Inside(AppContext.BaseDirectory, request.InstallDirectory)) throw new IOException("Updater должен запускаться из временной папки.");
            if (args[0] == "--recover")
            {
                new UpdateProcesses(UpdateHandoff.Root).WaitForParentAsync(request, default).GetAwaiter().GetResult();
                installer.Recover(request); new UpdateProcesses(UpdateHandoff.Root).StartPrevious(request.InstallDirectory);
            }
            else installer.InstallAsync(request, default).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            MessageBox.Show(UpdateInstaller.FriendlyError(error) + "\n\nПри замене выполнена попытка восстановления предыдущей версии. Резервные копии и журнал находятся в:\n" + UpdateHandoff.Root + "\n\nРучная загрузка: https://github.com/koteiik/GameLocalizer/releases/latest", "GameLocalizer — обновление не выполнено", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
        }
    }
}

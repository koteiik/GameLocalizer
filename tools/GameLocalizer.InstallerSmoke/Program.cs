using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (ApplicationPaths.DataDirectoryName == "GameLocalizer") throw new InvalidOperationException("Smoke must use an isolated QA build identity.");
        var install = Path.GetFullPath(args[0]); var setup = Path.GetFullPath(args[1]); var report = Path.GetFullPath(args[2]);
        var bytes = File.ReadAllBytes(setup);
        var installation = new InstallationInfoService(install).GetInfo();
        if (!installation.Installed) throw new InvalidOperationException("QA installation not detected.");
        using var mutex = new Mutex(false, ApplicationPaths.AppMutex);
        using var client = new HttpClient(new TestRelease(bytes));
        var application = new Application(); var window = new Window { Title = "GameLocalizer — Installer update smoke", Width = 620, Height = 240 };
        var vm = new UpdateViewModel(new(client, true), () => false, () => Task.CompletedTask, installation, shutdown: () =>
        {
            File.WriteAllText(report, "PASS: UpdateCommand downloaded and SHA256-verified installer; launch succeeded; closing app.");
            application.Shutdown();
        });
        var panel = new StackPanel { Margin = new Thickness(24) }; window.Content = panel;
        panel.Children.Add(new TextBlock { Text = "Isolated QA profile: " + ApplicationPaths.DataDirectoryName });
        panel.Children.Add(new TextBlock { Text = "0.3.0 → synthetic 0.3.1 release (production UpdateCommand / installer)", Margin = new Thickness(0, 10, 0, 10) });
        panel.Children.Add(new Button { Content = "Обновить", Command = vm.UpdateCommand, Height = 40 });
        var status = new TextBlock { DataContext = vm, TextWrapping = TextWrapping.Wrap }; status.SetBinding(TextBlock.TextProperty, "Status"); panel.Children.Add(status);
        window.Loaded += async (_, _) =>
        {
            await vm.CheckAsync(default);
            if (args.Contains("--auto"))
            {
                if (!vm.UpdateCommand.CanExecute(null)) throw new InvalidOperationException(vm.Status);
                await vm.InstallAsync();
                if (!vm.HandoffStarted) { File.WriteAllText(report, "FAIL: " + vm.Status); application.Shutdown(1); }
            }
        };
        application.Run(window);
    }
    private sealed class TestRelease(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpContent content;
            if (request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/koteiik/GameLocalizer/releases/latest")
                content = new StringContent(JsonSerializer.Serialize(new { tag_name = "v0.3.1", prerelease = false, draft = false, body = "Synthetic installer smoke release", assets = new[] {
                    new { name = ReleaseClient.InstallerAssetName, size = bytes.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), browser_download_url = "https://github.com/koteiik/GameLocalizer/releases/download/v0.3.1/" + ReleaseClient.InstallerAssetName } } }));
            else if (request.RequestUri.AbsoluteUri == "https://github.com/koteiik/GameLocalizer/releases/download/v0.3.1/" + ReleaseClient.InstallerAssetName) content = new ByteArrayContent(bytes);
            else throw new InvalidDataException("Unexpected request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

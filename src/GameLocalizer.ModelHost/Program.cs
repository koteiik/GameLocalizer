using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.TranslationProviders;

Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
if (OperatingSystem.IsWindows()) Native.SetErrorMode(0x0001 | 0x0002);
using var runtime = new MarianOnnxRuntime();
string? input;
while ((input = await Console.In.ReadLineAsync()) != null)
{
    try
    {
        var request = JsonSerializer.Deserialize<ModelHostRequest>(input) ?? throw new InvalidDataException("Missing request");
        switch (request.Command)
        {
            case "load": await runtime.LoadAsync(request.Directory ?? throw new InvalidDataException("Missing directory"), request.Device, default); Reply(new(true, runtime.Device)); break;
            case "translate": Reply(new(true, runtime.Device, await runtime.TranslateAsync(request.Texts ?? [], default))); break;
            default: throw new InvalidDataException("Unknown command");
        }
    }
    catch (Exception e) { Reply(new(false, runtime.Device, Error: e.Message, OutOfMemory: e is OutOfMemoryException || e.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("Failed to allocate", StringComparison.OrdinalIgnoreCase))); }
}
static void Reply(ModelHostResponse response) => Console.WriteLine(JsonSerializer.Serialize(response));
internal static partial class Native
{
    [DllImport("kernel32.dll")] internal static extern uint SetErrorMode(uint mode);
}

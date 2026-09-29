using System.Management;
using System.Runtime.Versioning;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.Hardware;

public sealed class HardwareDetectionService : IHardwareDetectionService
{
    public HardwareInfo Detect()
    {
        if (!OperatingSystem.IsWindows()) return new(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU", GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, "Недоступно", null, false);
        return DetectWindows();
    }
    [SupportedOSPlatform("windows")]
    private static HardwareInfo DetectWindows()
    {
        var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; var gpu = "Не обнаружен"; long? vram = null; var supported = false;
        try
        {
            using var cpus = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject item in cpus.Get()) { using (item) cpu = item["Name"]?.ToString() ?? cpu; }
            using var systems = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject item in systems.Get()) { using (item) if (long.TryParse(item["TotalPhysicalMemory"]?.ToString(), out var value)) ram = value; }
            using var cards = new ManagementObjectSearcher("SELECT Name,AdapterRAM FROM Win32_VideoController");
            var names = new List<string>();
            foreach (ManagementObject item in cards.Get())
            {
                using (item)
                {
                    var name = item["Name"]?.ToString() ?? "GPU"; names.Add(name);
                    if (!name.Contains("Basic", StringComparison.OrdinalIgnoreCase) && !name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) supported = true;
                    if (long.TryParse(item["AdapterRAM"]?.ToString(), out var value) && value > 0) vram = Math.Max(vram ?? 0, value);
                }
            }
            if (names.Count != 0) gpu = string.Join(", ", names);
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
        return new(cpu, ram, gpu, vram, supported);
    }
}

namespace GameLocalizer.Core.Models;

public static class ApplicationVersion
{
    public static Version Current { get; } = GetCurrent();
    public static string Label => "v" + Current;
    private static Version GetCurrent()
    {
        var assembly = typeof(ApplicationVersion).Assembly.GetName().Version!;
        return new(assembly.Major, assembly.Minor, Math.Max(0, assembly.Build));
    }
}

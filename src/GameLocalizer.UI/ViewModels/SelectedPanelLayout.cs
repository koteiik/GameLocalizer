using System.ComponentModel;
using GameLocalizer.Core.Models;

namespace GameLocalizer.UI.ViewModels;

// Global presentation preferences, persisted by the existing SettingsService.
public sealed class SelectedPanelLayout(AppSettings settings, Action save) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Dock => settings.SelectedPanelDock == "Right" ? "Right" : "Bottom";
    public double BottomHeight => Valid(settings.SelectedPanelBottomHeight, 300);
    public double RightWidth => Valid(settings.SelectedPanelRightWidth, 380);
    public bool Collapsed => settings.SelectedPanelCollapsed;
    public void SetDock(string dock) { settings.SelectedPanelDock = dock == "Right" ? "Right" : "Bottom"; Commit(); }
    public void SetCollapsed(bool collapsed) { settings.SelectedPanelCollapsed = collapsed; Commit(); }
    public void Resize(string effectiveDock, double size, double available)
    {
        if (!double.IsFinite(size) || !double.IsFinite(available) || available <= 0) return;
        var constrained = Limit(size, effectiveDock == "Right" ? 320 : 180, available * (effectiveDock == "Right" ? .55 : .6));
        if (effectiveDock == "Right") settings.SelectedPanelRightWidth = constrained;
        else settings.SelectedPanelBottomHeight = constrained;
        Commit();
    }
    public void Reset()
    {
        settings.SelectedPanelDock = "Bottom";
        settings.SelectedPanelBottomHeight = 300;
        settings.SelectedPanelRightWidth = 380;
        settings.SelectedPanelCollapsed = false;
        Commit();
    }
    public PanelGeometry Geometry(double width, double height, bool hasGame)
    {
        var right = Dock == "Right" && width >= 900;
        var dock = right ? "Right" : "Bottom";
        var maximum = Math.Max(0, (right ? width * .55 : height * .6));
        var minimum = Math.Min(right ? 320 : 180, maximum);
        var size = !hasGame ? 0 : Collapsed ? Math.Min(right ? 220 : 84, maximum) : Limit(right ? RightWidth : BottomHeight, minimum, maximum);
        return new(dock, size, minimum, maximum, hasGame && !Collapsed);
    }
    private void Commit() { save(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
    private static double Valid(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
    private static double Limit(double value, double minimum, double maximum) => Math.Clamp(value, Math.Min(minimum, maximum), maximum);
}
public readonly record struct PanelGeometry(string Dock, double Size, double Minimum, double Maximum, bool Resizable);


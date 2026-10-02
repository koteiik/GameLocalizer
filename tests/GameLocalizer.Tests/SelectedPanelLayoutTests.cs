using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;
using Xunit;

namespace GameLocalizer.Tests;

public class SelectedPanelLayoutTests
{
    [Theory]
    [InlineData("Bottom", 1200, 600)]
    [InlineData("Right", 1200, 600)]
    public void NoSelectionReservesNoSpace(string dock, double width, double height)
    {
        var layout = new SelectedPanelLayout(new() { SelectedPanelDock = dock }, () => { });
        Assert.Equal(0, layout.Geometry(width,height,false).Size);
        Assert.False(layout.Geometry(width,height,false).Resizable);
        Assert.True(layout.Geometry(width,height,true).Size > 0);
    }
    [Theory]
    [InlineData("Bottom", 180, .6)]
    [InlineData("Right", 320, .55)]
    public void ResizeLimitsAndSeparateSizes(string dock, double minimum, double fraction)
    {
        var settings = new AppSettings(); var saved = 0;
        var layout = new SelectedPanelLayout(settings, () => saved++);
        layout.SetDock(dock);
        var otherSize = dock == "Right" ? layout.BottomHeight : layout.RightWidth;
        layout.Resize(dock, 1, 1200);
        Assert.Equal(minimum,dock == "Right" ? layout.RightWidth : layout.BottomHeight);
        layout.Resize(dock, 5000, 1200);
        Assert.Equal(1200*fraction,dock == "Right" ? layout.RightWidth : layout.BottomHeight);
        Assert.Equal(otherSize,dock == "Right" ? layout.BottomHeight : layout.RightWidth);
        Assert.Equal(3,saved);
    }
    [Fact] public void CollapseExpandAndDockSwitchPreserveSizes()
    {
        var layout = new SelectedPanelLayout(new(), () => { });
        layout.Resize("Bottom", 240, 600); layout.Resize("Right", 440, 1200);
        layout.SetCollapsed(true); layout.SetDock("Right");
        Assert.Equal(220,layout.Geometry(1200,600,true).Size);
        Assert.False(layout.Geometry(1200,600,true).Resizable);
        layout.SetDock("Bottom"); Assert.Equal(84,layout.Geometry(1200,600,true).Size);
        layout.SetCollapsed(false); Assert.Equal(240,layout.Geometry(1200,600,true).Size);
        layout.SetDock("Right"); Assert.Equal(440,layout.Geometry(1200,600,true).Size);
    }
    [Theory]
    [InlineData(1920, "Right")]
    [InlineData(1600, "Right")]
    [InlineData(1280, "Right")]
    [InlineData(1060, "Bottom")]
    public void ResponsiveFallbackPreservesPreference(int windowWidth, string expectedDock)
    {
        var layout = new SelectedPanelLayout(new() { SelectedPanelDock = "Right" }, () => { });
        var geometry = layout.Geometry(windowWidth-312,500,true);
        Assert.Equal(expectedDock,geometry.Dock); Assert.Equal("Right",layout.Dock);
        Assert.True(geometry.Size <= geometry.Maximum);
        Assert.Equal("Right",layout.Geometry(1600,700,true).Dock);
    }
    [Fact] public void ExistingSettingsRoundTripRestoresGlobalLayoutAndReset()
    {
        var folder = Path.Combine(Path.GetTempPath(),"panel-layout-"+Guid.NewGuid());
        try
        {
            var service = new SettingsService(folder);
            var settings = new AppSettings { TranslationProvider = "Mock", GitHubRepository = "test/project" };
            var layout = new SelectedPanelLayout(settings, () => service.Save(settings));
            layout.SetDock("Right"); layout.Resize("Bottom", 250, 600); layout.Resize("Right", 420, 1200); layout.SetCollapsed(true);
            var loaded = service.Load(); var restarted = new SelectedPanelLayout(loaded, () => service.Save(loaded));
            Assert.Equal("Right",restarted.Dock); Assert.Equal(250,restarted.BottomHeight); Assert.Equal(420,restarted.RightWidth); Assert.True(restarted.Collapsed);
            Assert.Equal(0,restarted.Geometry(1200,600,false).Size);
            Assert.Equal("Mock",loaded.TranslationProvider); Assert.Equal("test/project",loaded.GitHubRepository);
            restarted.Reset(); var reset = service.Load();
            Assert.Equal("Bottom",reset.SelectedPanelDock); Assert.Equal(300,reset.SelectedPanelBottomHeight); Assert.Equal(380,reset.SelectedPanelRightWidth); Assert.False(reset.SelectedPanelCollapsed);
        }
        finally { if(Directory.Exists(folder)) Directory.Delete(folder,true); }
    }
    [Fact] public void OldOrInvalidPreferencesHaveSafeDefaults()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Equal(300,settings.SelectedPanelBottomHeight);
        settings.SelectedPanelDock = "Floating"; settings.SelectedPanelBottomHeight = double.NaN; settings.SelectedPanelRightWidth = -1;
        var layout = new SelectedPanelLayout(settings, () => { });
        Assert.Equal("Bottom",layout.Dock); Assert.Equal(300,layout.BottomHeight); Assert.Equal(380,layout.RightWidth);
        Assert.True(double.IsFinite(layout.Geometry(100,100,true).Size));
    }
}

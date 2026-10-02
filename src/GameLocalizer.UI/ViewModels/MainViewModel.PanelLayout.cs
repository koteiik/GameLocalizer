namespace GameLocalizer.UI.ViewModels;

public sealed partial class MainViewModel
{
    private SelectedPanelLayout? selectedPanelLayout;
    public SelectedPanelLayout PanelLayout => selectedPanelLayout ??= new(Settings, () =>
    {
        try { settingsService.Save(Settings); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Status = "Не удалось сохранить расположение панели: " + e.Message; }
    });
}

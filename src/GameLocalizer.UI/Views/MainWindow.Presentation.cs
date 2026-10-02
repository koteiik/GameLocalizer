using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
namespace GameLocalizer.UI.Views;

public partial class MainWindow
{
    private bool presentationUpdateQueued;
    private void InitializePresentation()
    {
        SourceInitialized += (_, _) => SetDarkCaption();
        LibraryHost.SizeChanged += (_, _) => UpdateCards();
        SelectedGamePanel.SizeChanged += (_, _) => UpdateCards();
        LibraryGrid.ItemContainerGenerator.StatusChanged += (_, _) => UpdateCards();
    }
    private void QueueLibraryPresentation()
    {
        if(presentationUpdateQueued) return;
        presentationUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            presentationUpdateQueued = false;
            if(DataContext is ViewModels.MainViewModel vm && vm.IsLibrary)
            {
                if (!panelResizing) ApplyDockPresentation(this, vm.PanelLayout, vm.HasGame);
                ApplyLibraryPresentation(LibraryGrid, LibraryHost, SelectedGamePanel, vm.LibraryList);
            }
        }));
    }
    // These methods only size presentation controls; shared by live layout and visual regression tests.
    public static int CardColumns(double availableWidth) => Math.Clamp((int)Math.Floor((Math.Max(0,availableWidth) + 18) / 298), 1, 4);
    public static void ApplyLibraryPresentation(ListBox library, FrameworkElement host, FrameworkElement selectedPanel, bool list)
    {
        if(host.ActualWidth <= 0) return;
        var available = Math.Max(160, library.ActualWidth - 24);
        var columns = list ? 1 : CardColumns(available);
        var cardHeight = list ? 100d : host.ActualHeight < 700 ? 166d : 174d;
        var cardWidth = Math.Floor(available / columns) - 18;
        SetResource(library, "CardWidth", cardWidth);
        SetResource(library, "CardHeight", cardHeight);
    }

    private static void SetResource(FrameworkElement target, string key, double value)
    {
        if(target.Resources[key] is not double old || Math.Abs(old-value) > .5) target.Resources[key] = value;
    }
    private void SetDarkCaption()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var dark = 1; var caption = 0x001B1411; var text = 0x00FAF6F4;
        // Unsupported DWM attributes simply fall back to native chrome; window input remains native.
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

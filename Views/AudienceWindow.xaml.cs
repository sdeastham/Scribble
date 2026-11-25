using System.Windows;
using Scribble.Services;
using Scribble.Session;

namespace Scribble.Views;

public partial class AudienceWindow : Window
{
    public AudienceWindow(PresentationSession session)
    {
        InitializeComponent();
        Viewport.AttachSession(session);
    }

    public void ShowOnMonitor(MonitorBounds target)
    {
        if (!IsVisible)
        {
            // Place off-screen before Show to prevent a brief flash on Screen 1
            Left = -32000;
            Top = -32000;
            Show();
        }

        MonitorService.PlaceBorderlessFullscreen(this, target, activate: false);

        // Re-apply once loaded to handle mixed-DPI monitor transitions (e.g. 200% Surface + 100% projector)
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            () => MonitorService.PlaceBorderlessFullscreen(this, target, activate: false));
    }
}

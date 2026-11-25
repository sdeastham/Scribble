using System.IO;
using System.Windows;
using Scribble.Diagnostics;
using Scribble.Views;

namespace Scribble;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
        {
            string? sampleOut = e.Args.Length > 1 ? e.Args[1] : null;
            int code = SelfTestRunner.Run(sampleOut);
            Shutdown(code);
            return;
        }

        string? initialPdfPath = null;
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
        {
            initialPdfPath = Path.GetFullPath(e.Args[0]);
        }

        var presenter = new PresenterWindow(initialPdfPath);
        MainWindow = presenter;
        presenter.Show();
    }
}

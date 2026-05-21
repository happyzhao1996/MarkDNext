using System.IO;
using System.Windows;

namespace MarkDNext;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnLastWindowClose;

        var files = e.Args
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .ToArray();

        if (files.Length == 0)
        {
            new MainWindow().Show();
            return;
        }

        foreach (var file in files)
        {
            new MainWindow(file).Show();
        }
    }
}

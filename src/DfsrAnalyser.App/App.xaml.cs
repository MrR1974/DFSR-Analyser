using System.Windows;
using System.Windows.Threading;

namespace DfsrAnalyser.App;

public partial class App : Application
{
    /// <summary>Case folder to open at start-up (--case).</summary>
    public static string? StartupCase { get; private set; }

    /// <summary>When set (--screenshot), the window opens the case, exercises the views, saves a PNG here and exits.</summary>
    public static string? ScreenshotPath { get; private set; }

    /// <summary>File or folder to import into the start-up case (--import), through the same path as the menu.</summary>
    public static string? StartupImport { get; private set; }

    /// <summary>Server naming for --import: "header" (default), "folder", or a fixed server name (--import-naming).</summary>
    public static string ImportNaming { get; private set; } = "header";

    /// <summary>File name pattern applied for the screenshot mode's filters image (--screenshot-file).</summary>
    public static string ScreenshotFilePattern { get; private set; } = "file_00*.txt";

    protected override void OnStartup(StartupEventArgs e)
    {
        for (var i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--case" when i + 1 < e.Args.Length: StartupCase = e.Args[++i]; break;
                case "--screenshot" when i + 1 < e.Args.Length: ScreenshotPath = e.Args[++i]; break;
                case "--import" when i + 1 < e.Args.Length: StartupImport = e.Args[++i]; break;
                case "--import-naming" when i + 1 < e.Args.Length: ImportNaming = e.Args[++i]; break;
                case "--screenshot-file" when i + 1 < e.Args.Length: ScreenshotFilePattern = e.Args[++i]; break;
                default:
                    if (!e.Args[i].StartsWith("--", StringComparison.Ordinal) && Directory.Exists(e.Args[i])) StartupCase = e.Args[i];
                    break;
            }
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        new MainWindow().Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (ScreenshotPath is not null)
        {
            Console.Error.WriteLine(e.Exception);
            File.WriteAllText(ScreenshotPath + ".error.txt", e.Exception.ToString());
            Shutdown(1);
            e.Handled = true;
            return;
        }
        MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WatchFlix.Core;
using WatchFlix.Core.Data;

namespace WatchFlix.Desktop;

public static class Program
{
    const string MutexName = "Local\\WatchFlix.Desktop.SingleInstance";
    const string WakeName = "Local\\WatchFlix.Desktop.Wake";

    [STAThread]
    public static int Main(string[] args)
    {
        // A second launch hands over to the copy already open rather than
        // fighting it for the database.
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            try
            {
                using var wake = EventWaitHandle.OpenExisting(WakeName);
                wake.Set();
            }
            catch { }
            return 0;
        }

        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName);
        var app = new App();
        new Thread(() =>
        {
            while (true)
            {
                wakeEvent.WaitOne();
                app.Dispatcher.BeginInvoke(() => (app.MainWindow as MainWindow)?.BringToFront());
            }
        }) { IsBackground = true, Name = "WatchFlix wake" }.Start();
        return app.Run();
    }
}

public sealed class App : Application
{
    static readonly object LogGate = new();

    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Background task: " + e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply(this);
        try
        {
            Db.Init();
        }
        catch (Exception ex)
        {
            Log("Could not open the library: " + ex);
            MessageBox.Show("WatchFlix could not open its library:\n\n" + ex.Message +
                            $"\n\nLibrary folder: {Config.AppHome}", "WatchFlix", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        Log($"=== WatchFlix {Config.AppVersion} started ===");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UI error: " + e.Exception);
        // One broken screen should not take the whole app down with it.
        e.Handled = true;
        (MainWindow as MainWindow)?.Toast("Something went wrong: " + e.Exception.Message, true);
    }

    public static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Config.AppHome);
                var path = Config.LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>A dark title bar, so the window frame matches what is inside it.</summary>
    public static void DarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { }
        };
    }
}

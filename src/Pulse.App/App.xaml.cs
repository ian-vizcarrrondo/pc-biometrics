using System.Windows;

namespace Pulse.App;

public partial class App : Application
{
    private const string MutexName = @"Local\Pulse.PcHealth.SingleInstance";
    private const string ShowEventName = @"Local\Pulse.PcHealth.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private AppController? _controller;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, MutexName, out var first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            // A second launch asks the running instance to show itself instead of starting duplicate collectors.
            _showEvent.Set();
            Shutdown();
            return;
        }

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);

        _controller = new AppController();
        _controller.ShowRequested += ShowWindow;
        _window = new MainWindow(_controller);
        var startInTray = e.Args.Contains("--tray") && _controller.Settings.CloseToTray;
        if (!startInTray) _window.Show();
        _controller.Start();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _controller?.Dispose();
        _showEvent?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }
}

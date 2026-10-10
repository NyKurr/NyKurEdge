using Microsoft.UI.Xaml;

namespace NyKurEdge.App;

public partial class App : Application, IAsyncDisposable
{
    private MainWindow? _window;
    private AppServices? _services;
    // Keep this handle alive until process termination. Setup and Uninstall use
    // the same session-local name to protect files while the app is running.
    private Mutex? _runningMutex;
    private bool _disposed;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Setup holds this gate only while files are being installed or
            // removed. Create the running marker atomically under that gate,
            // then release it before any asynchronous service initialization.
            using var lifecycleGate = new Mutex(false, @"Local\NyKurEdge.App.Lifecycle");
            bool gateAcquired;
            try { gateAcquired = lifecycleGate.WaitOne(0); }
            catch (AbandonedMutexException) { gateAcquired = true; }
            if (!gateAcquired)
            {
                Exit();
                return;
            }

            bool firstInstance;
            try
            {
                _runningMutex = new Mutex(false, @"Local\NyKurEdge.App.Running", out firstInstance);
            }
            finally { lifecycleGate.ReleaseMutex(); }
            if (!firstInstance)
            {
                _runningMutex.Dispose();
                _runningMutex = null;
                Exit();
                return;
            }

            _services = new AppServices();
            await _services.InitializeAsync();
            _window = new MainWindow(_services);
            _window.Closed += OnWindowClosed;
            // Subscribe the UI to initial provider events, but finish startup
            // before exposing a window/tray that the user can close.
            await _services.StartRuntimeAsync();
            if (!_disposed)
            {
                _window?.ShowWithoutActivation();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"NyKur Edge failed to launch: {exception}");
            throw;
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.Dispose();
        }

        _window = null;
        // Closing the last window can stop the UI dispatcher. Service cleanup
        // touches no UI and must not queue a continuation back to that window.
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnhandledException -= OnUnhandledException;
        if (_services is not null)
        {
            await _services.DisposeAsync().ConfigureAwait(false);
            _services = null;
        }

        GC.SuppressFinalize(this);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled UI exception: {args.Exception}");
    }
}

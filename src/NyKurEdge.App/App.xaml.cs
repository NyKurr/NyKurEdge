using Microsoft.UI.Xaml;

namespace NyKurEdge.App;

public partial class App : Application, IAsyncDisposable
{
    private MainWindow? _window;
    private AppServices? _services;
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
            _services = new AppServices();
            await _services.InitializeAsync();

            _window = new MainWindow(_services);
            _window.Closed += OnWindowClosed;
            _window.ShowWithoutActivation();

            await _services.StartRuntimeAsync();
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
        await DisposeAsync();
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
            await _services.DisposeAsync();
            _services = null;
        }

        GC.SuppressFinalize(this);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled UI exception: {args.Exception}");
    }
}

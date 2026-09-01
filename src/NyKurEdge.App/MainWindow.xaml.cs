using Microsoft.UI.Xaml;
using NyKurEdge.App.Presentation.Edge;
using NyKurEdge.App.Presentation.Shell;

namespace NyKurEdge.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly AppServices _services;
    private readonly EdgeWindowController _windowController;
    private readonly NyKurNotificationAreaIcon? _notificationAreaIcon;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        Title = "NyKur Edge";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _windowController = new EdgeWindowController(this, services.DisplayService, services.Settings);
        RootFrame.Content = new MainPage(services, _windowController, OpenSettings);
        try
        {
            _notificationAreaIcon = new NyKurNotificationAreaIcon(OpenSettings, Close);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"NyKur Edge notification-area integration is unavailable: {exception}");
        }
        Closed += OnClosed;
    }

    public void ShowWithoutActivation()
    {
        _windowController.ShowWithoutActivation();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Closed -= OnClosed;
        _notificationAreaIcon?.Dispose();
        if (_settingsWindow is not null)
        {
            var settingsWindow = _settingsWindow;
            _settingsWindow = null;
            settingsWindow.Closed -= OnSettingsWindowClosed;
            settingsWindow.Close();
            settingsWindow.Dispose();
        }
        (RootFrame.Content as IDisposable)?.Dispose();
        RootFrame.Content = null;
        _windowController.Dispose();
    }

    private void OnClosed(object sender, WindowEventArgs args) => Dispose();

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_services, Close);
        _settingsWindow.Closed += OnSettingsWindowClosed;
        _settingsWindow.Activate();
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.Closed -= OnSettingsWindowClosed;
        _settingsWindow.Dispose();
        _settingsWindow = null;
    }
}

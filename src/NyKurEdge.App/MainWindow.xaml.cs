using Microsoft.UI.Xaml;
using NyKurEdge.App.Presentation.Edge;

namespace NyKurEdge.App;

public sealed partial class MainWindow : Window, IDisposable
{
    private readonly EdgeWindowController _windowController;
    private bool _disposed;

    public MainWindow(AppServices services)
    {
        InitializeComponent();

        Title = "NyKur Edge";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _windowController = new EdgeWindowController(this, services.DisplayService, services.Settings);
        RootFrame.Content = new MainPage(services, _windowController);
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
        (RootFrame.Content as IDisposable)?.Dispose();
        RootFrame.Content = null;
        _windowController.Dispose();
    }

    private void OnClosed(object sender, WindowEventArgs args) => Dispose();
}

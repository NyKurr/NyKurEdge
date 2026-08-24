using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NyKurEdge.App.Presentation.Animations;
using NyKurEdge.App.Presentation.Edge;
using NyKurEdge.App.Presentation.ViewModels;
using NyKurEdge.Core.Appearance;
using NyKurEdge.Core.Display;
using NyKurEdge.Core.Settings;
using NyKurEdge.Core.State;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace NyKurEdge.App;

public sealed partial class MainPage : Page, IDisposable
{
    private readonly AppServices _services;
    private readonly EdgeWindowController _windowController;
    private readonly Action _openSettings;
    private readonly EdgeInteractionStateMachine _stateMachine = new();
    private readonly DispatcherQueueTimer _collapseTimer;
    private readonly EdgeWaveRenderer _edgeRenderer;
    private readonly AccentTransitionController _accentController;
    private DateTimeOffset _notificationContextUntil = DateTimeOffset.MinValue;
    private double _notificationIconProgress;
    private bool _cleanedUp;

#if NYKUR_EDGE_VISUAL_TEST
    private static readonly AccentColor[] VisualTestAccents =
    [
        new(104, 184, 146),
        new(168, 134, 216),
        new(215, 151, 104),
        new(210, 123, 134),
        new(174, 183, 194),
    ];

    private readonly List<KeyboardAccelerator> _visualTestAccelerators = [];
    private EdgeSide _visualTestSide;
    private bool _visualTestPlaying;
    private bool _visualTestExpanded;
    private bool _visualTestNotificationContext;
    private int _visualTestAccentIndex;
#endif

    public MainPage(
        AppServices services,
        EdgeWindowController windowController,
        Action openSettings)
    {
        _services = services;
        _windowController = windowController;
        _openSettings = openSettings;
        ViewModel = new EdgeViewModel(services);

        InitializeComponent();

        _collapseTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(40);
        _collapseTimer.IsRepeating = true;
        _collapseTimer.Tick += OnCollapseTimerTick;

        _edgeRenderer = new EdgeWaveRenderer(
            FluidCanvas,
            FluidCanvasSecondary,
            GetApplicationBrush("NyKurAccentBrush"));
        _edgeRenderer.NotificationIconProgressChanged += OnNotificationIconProgressChanged;
        _accentController = new AccentTransitionController(
            (GetApplicationBrush("NyKurAccentBrush"), 255),
            (GetApplicationBrush("NyKurAccentSoftBrush"), 80),
            (GetApplicationBrush("NyKurAccentFaintBrush"), 24));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.AccentRequested += OnAccentRequested;
        ViewModel.NotificationArrived += OnNotificationArrived;
        ViewModel.GlanceVisibilityChanged += OnGlanceVisibilityChanged;
        _windowController.ExpansionProgressChanged += OnExpansionProgressChanged;

#if NYKUR_EDGE_VISUAL_TEST
        _visualTestSide = ViewModel.Settings.EdgeSide;
        RegisterVisualTestAccelerator(VirtualKey.F3);
        RegisterVisualTestAccelerator(VirtualKey.F4);
        RegisterVisualTestAccelerator(VirtualKey.F5);
        RegisterVisualTestAccelerator(VirtualKey.F6);
        RegisterVisualTestAccelerator(VirtualKey.F7);
        RegisterVisualTestAccelerator(VirtualKey.F8);
        RegisterVisualTestAccelerator(VirtualKey.F9);
        RegisterVisualTestAccelerator(VirtualKey.F10);
        RegisterVisualTestAccelerator(VirtualKey.F11);
        RegisterVisualTestAccelerator(VirtualKey.F12);
#endif

        ApplyEdgeSide();
        UpdateContextSurface();
    }

    public EdgeViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
#if NYKUR_EDGE_VISUAL_TEST
        _visualTestPlaying = ViewModel.IsPlaying;
        _windowController.SetVisualInspectionStatus("NyKur Edge QA · idle");
#endif
        _edgeRenderer.Start();
        _edgeRenderer.SetPlaying(ViewModel.IsPlaying);
        _edgeRenderer.SetIntensity(ViewModel.Settings.Appearance.AnimationIntensity);
        ViewModel.RefreshNotificationAccess();
        _ = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            _windowController.EnableAdaptiveRegion);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Cleanup();

    private void Cleanup()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.AccentRequested -= OnAccentRequested;
        ViewModel.NotificationArrived -= OnNotificationArrived;
        ViewModel.GlanceVisibilityChanged -= OnGlanceVisibilityChanged;
        _windowController.ExpansionProgressChanged -= OnExpansionProgressChanged;
        _edgeRenderer.NotificationIconProgressChanged -= OnNotificationIconProgressChanged;

#if NYKUR_EDGE_VISUAL_TEST
        foreach (var accelerator in _visualTestAccelerators)
        {
            accelerator.Invoked -= OnVisualTestAcceleratorInvoked;
            KeyboardAccelerators.Remove(accelerator);
        }
        _visualTestAccelerators.Clear();
#endif

        _collapseTimer.Stop();
        _collapseTimer.Tick -= OnCollapseTimerTick;
        _edgeRenderer.Dispose();
        _accentController.Dispose();
        ViewModel.Dispose();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _collapseTimer.Stop();
        _stateMachine.PointerEntered();
        UpdateContextSurface();
        _windowController.SetExpanded(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _stateMachine.PointerExited(DateTimeOffset.Now);
        if (!_stateMachine.State.IsPinnedOpen)
        {
            _collapseTimer.Start();
        }
    }

    private void OnCollapseTimerTick(DispatcherQueueTimer sender, object args)
    {
        var state = _stateMachine.Advance(DateTimeOffset.Now);
        if (state.Visibility == EdgeVisibility.Collapsed)
        {
            _collapseTimer.Stop();
            _windowController.SetExpanded(false);
        }
    }

    private void OnExpansionProgressChanged(object? sender, double progress)
    {
        LayoutGrid.Width = EdgeWindowLayout.CollapsedWidthDip +
                           ((EdgeWindowLayout.ExpandedWidthDip - EdgeWindowLayout.CollapsedWidthDip) * progress);
        EdgeSurface.Width = LayoutGrid.Width;
        _edgeRenderer.SetExpansionProgress(progress);
        NotificationIconHost.Opacity = _notificationIconProgress * (1 - SmootherStep(progress));

        if (progress > 0.001)
        {
            PanelContent.Visibility = Visibility.Visible;
        }

        var contentProgress = SmootherStep(Math.Clamp((progress - 0.30) / 0.58, 0, 1));
        PanelContent.Opacity = contentProgress;
        PanelContent.IsHitTestVisible = contentProgress >= 0.97;
        var direction = EffectiveSide == EdgeSide.Right ? 1 : -1;
        PanelTransform.TranslateX = direction * (1 - contentProgress) * 22;
        PanelTransform.ScaleX = 0.96 + (contentProgress * 0.04);
        PanelTransform.ScaleY = 0.96 + (contentProgress * 0.04);

        if (progress <= 0.001)
        {
            PanelContent.IsHitTestVisible = false;
            PanelContent.Visibility = Visibility.Collapsed;
        }
    }

    private void OnEdgeLauncherClicked(object sender, RoutedEventArgs e)
    {
        SetPinnedOpen(!_stateMachine.State.IsPinnedOpen);
#if NYKUR_EDGE_VISUAL_TEST
        _windowController.SetVisualInspectionStatus(
            $"NyKur Edge QA · {(_stateMachine.State.IsPinnedOpen ? "pinned" : "released")}");
#endif
    }

    private void SetPinnedOpen(bool pinned)
    {
        _collapseTimer.Stop();
        var state = _stateMachine.SetPinned(pinned, DateTimeOffset.Now);
        _windowController.SetPinnedInteraction(state.IsPinnedOpen);

        if (state.IsPinnedOpen)
        {
            UpdateContextSurface();
            _windowController.SetExpanded(true);
        }
        else if (!state.IsPointerInside && !state.IsGlanceActive)
        {
            _collapseTimer.Start();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EdgeViewModel.IsPlaying):
                _edgeRenderer.SetPlaying(ViewModel.IsPlaying);
                break;
            case nameof(EdgeViewModel.HasNotification):
                if (!ViewModel.HasNotification)
                {
                    UpdateContextSurface();
                }
                break;
            case nameof(EdgeViewModel.Settings):
                ApplyEdgeSide();
                _windowController.ApplySettings();
                _edgeRenderer.SetIntensity(ViewModel.Settings.Appearance.AnimationIntensity);
                break;
        }
    }

    private void OnAccentRequested(AccentColor accent) => _accentController.TransitionTo(accent);

    private void OnNotificationArrived()
    {
        _notificationContextUntil = DateTimeOffset.Now.AddSeconds(12);
        _edgeRenderer.TriggerNotificationPulse();
        if (_windowController.IsExpanded)
        {
            UpdateContextSurface();
        }
    }

    private void OnNotificationIconProgressChanged(double progress)
    {
        _notificationIconProgress = progress;
        NotificationIconHost.Opacity = progress * (_windowController.IsExpanded ? 0 : 1);
        var scale = 0.72 + (progress * 0.28);
        NotificationIconTransform.ScaleX = scale;
        NotificationIconTransform.ScaleY = scale;
    }

    private void OnGlanceVisibilityChanged(bool visible)
    {
        if (visible)
        {
            _collapseTimer.Stop();
            _stateMachine.BeginGlance();
            _windowController.SetExpanded(true);
        }
        else
        {
            _stateMachine.EndGlance(DateTimeOffset.Now);
            if (!_stateMachine.State.IsPinnedOpen)
            {
                _collapseTimer.Start();
            }
        }

        AnimateGlance(visible);
    }

    private void AnimateGlance(bool visible)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(GlanceOverlay);
        var targetOpacity = visible ? 1f : 0f;
        var startOpacity = visual.Opacity;
        visual.StopAnimation("Opacity");
        visual.Opacity = targetOpacity;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, startOpacity);
        animation.InsertKeyFrame(1, targetOpacity);
        animation.Duration = TimeSpan.FromMilliseconds(visible ? 230 : 190);
        visual.StartAnimation("Opacity", animation);
    }

    private void UpdateContextSurface()
    {
#if NYKUR_EDGE_VISUAL_TEST
        var showNotification = _visualTestNotificationContext;
#else
        var showNotification = ViewModel.HasNotification &&
                               DateTimeOffset.Now <= _notificationContextUntil;
#endif
        NotificationSurface.Visibility = showNotification ? Visibility.Visible : Visibility.Collapsed;
        MediaSurface.Visibility = showNotification ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => _openSettings();

    private async void OnPlayPauseClicked(object sender, RoutedEventArgs e) =>
        await ObserveAsync(ViewModel.TogglePlayPauseAsync());

    private async void OnPreviousClicked(object sender, RoutedEventArgs e) =>
        await ObserveAsync(ViewModel.SkipPreviousAsync());

    private async void OnNextClicked(object sender, RoutedEventArgs e) =>
        await ObserveAsync(ViewModel.SkipNextAsync());

    private async void OnMediaProgressPointerReleased(object sender, PointerRoutedEventArgs e) =>
        await ObserveAsync(ViewModel.SeekAsync(MediaProgressSlider.Value));

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        Cleanup();
        _windowController.CloseWindow();
    }

    private void ApplyEdgeSide() => ApplyEdgeSide(EffectiveSide);

    private void ApplyEdgeSide(EdgeSide side)
    {
        _edgeRenderer.SetSide(side);
        if (side == EdgeSide.Right)
        {
            PanelContent.Margin = new Thickness(28, 24, 30, 24);
            EdgeSurface.HorizontalAlignment = HorizontalAlignment.Right;
            EdgeLauncherButton.HorizontalAlignment = HorizontalAlignment.Right;
            PanelSettingsButton.HorizontalAlignment = HorizontalAlignment.Right;
            NotificationIconHost.HorizontalAlignment = HorizontalAlignment.Right;
            NotificationIconHost.Margin = new Thickness(0, 0, 19, 0);
        }
        else
        {
            PanelContent.Margin = new Thickness(30, 24, 28, 24);
            EdgeSurface.HorizontalAlignment = HorizontalAlignment.Left;
            EdgeLauncherButton.HorizontalAlignment = HorizontalAlignment.Left;
            PanelSettingsButton.HorizontalAlignment = HorizontalAlignment.Left;
            NotificationIconHost.HorizontalAlignment = HorizontalAlignment.Left;
            NotificationIconHost.Margin = new Thickness(19, 0, 0, 0);
        }
    }

    private static double SmootherStep(double value) =>
        value * value * value * ((value * ((value * 6) - 15)) + 10);

    private EdgeSide EffectiveSide
    {
        get
        {
#if NYKUR_EDGE_VISUAL_TEST
            return _visualTestSide;
#else
            return ViewModel.Settings.EdgeSide;
#endif
        }
    }

#if NYKUR_EDGE_VISUAL_TEST
    private void RegisterVisualTestAccelerator(VirtualKey key)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += OnVisualTestAcceleratorInvoked;
        _visualTestAccelerators.Add(accelerator);
        KeyboardAccelerators.Add(accelerator);
    }

    private void OnVisualTestAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        switch (sender.Key)
        {
            case VirtualKey.F3:
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · orb {_edgeRenderer.CycleOrbScale().ToString().ToLowerInvariant()}");
                break;
            case VirtualKey.F4:
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · shell {_edgeRenderer.CycleShellShape().ToString().ToLowerInvariant()}");
                break;
            case VirtualKey.F5:
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · fluid {_edgeRenderer.CycleCharacter().ToString().ToLowerInvariant()}");
                break;
            case VirtualKey.F6:
                _visualTestPlaying = !_visualTestPlaying;
                _edgeRenderer.SetPlaying(_visualTestPlaying);
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · {(_visualTestPlaying ? "playing" : "idle")}");
                break;
            case VirtualKey.F7:
                _visualTestNotificationContext = true;
                UpdateContextSurface();
                _edgeRenderer.TriggerNotificationPulse(timingScale: 4);
                _windowController.SetVisualInspectionStatus("NyKur Edge QA · notification");
                break;
            case VirtualKey.F8:
                _visualTestAccentIndex = (_visualTestAccentIndex + 1) % VisualTestAccents.Length;
                _accentController.TransitionTo(VisualTestAccents[_visualTestAccentIndex]);
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · accent {_visualTestAccentIndex + 1}");
                break;
            case VirtualKey.F9:
                _visualTestExpanded = !_visualTestExpanded;
                _windowController.SetExpanded(_visualTestExpanded);
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · {(_visualTestExpanded ? "expanded" : "collapsed")}");
                break;
            case VirtualKey.F10:
                _visualTestSide = _visualTestSide == EdgeSide.Right ? EdgeSide.Left : EdgeSide.Right;
                ApplyEdgeSide(_visualTestSide);
                _windowController.SetVisualInspectionSide(_visualTestSide);
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · {_visualTestSide.ToString().ToLowerInvariant()}");
                break;
            case VirtualKey.F11:
                SetPinnedOpen(!_stateMachine.State.IsPinnedOpen);
                break;
            case VirtualKey.F12:
                var now = DateTimeOffset.Now;
                _ = ObserveAsync(_services.Glances.ShowAsync(
                    new NyKurEdge.Core.Glances.GlancePresentation(
                        Guid.NewGuid(),
                        NyKurEdge.Core.Glances.GlanceKind.Clock,
                        "TIME",
                        now.ToString("HH:mm", CultureInfo.CurrentCulture),
                        now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture),
                        TimeSpan.FromSeconds(16))));
                _windowController.SetVisualInspectionStatus("NyKur Edge QA · clock glance");
                break;
            default:
                return;
        }

        args.Handled = true;
    }
#endif

    private static async Task ObserveAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"UI operation failed: {exception}");
        }
    }

    private static async Task ObserveAsync(Task<bool> operation)
    {
        try
        {
            _ = await operation;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"UI operation failed: {exception}");
        }
    }

    private static SolidColorBrush GetApplicationBrush(string key) =>
        (SolidColorBrush)Application.Current.Resources[key];

    public void Dispose() => Cleanup();
}

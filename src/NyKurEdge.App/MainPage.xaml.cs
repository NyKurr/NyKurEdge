using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NyKurEdge.App.Presentation.Animations;
using NyKurEdge.App.Presentation.Edge;
using NyKurEdge.App.Presentation.ViewModels;
using NyKurEdge.Core.Appearance;
using NyKurEdge.Core.Display;
using NyKurEdge.Core.Notifications;
using NyKurEdge.Core.Settings;
using NyKurEdge.Core.State;
using Windows.System;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace NyKurEdge.App;

public sealed partial class MainPage : Page, IDisposable
{
    private const double ExpandedWaveSurfaceWidthDip = 156;

    private readonly AppServices _services;
    private readonly EdgeWindowController _windowController;
    private readonly EdgeInteractionStateMachine _stateMachine = new();
    private readonly DispatcherQueueTimer _collapseTimer;
    private readonly DispatcherQueueTimer _accentSaveTimer;
    private readonly EdgeWaveRenderer _edgeRenderer;
    private readonly EdgeBubbleController _bubbleController;
    private readonly AccentTransitionController _accentController;
    private AccentColor _pendingManualAccent = AccentColor.Default;
    private bool _loadingSettings;
    private bool _settingsOpen;
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

    private EdgeSide _visualTestSide;
    private bool _visualTestPlaying;
    private bool _visualTestExpanded;
    private int _visualTestAccentIndex;
    private readonly List<KeyboardAccelerator> _visualTestAccelerators = [];
#endif

    public MainPage(AppServices services, EdgeWindowController windowController)
    {
        _services = services;
        _windowController = windowController;
        ViewModel = new EdgeViewModel(services);

        InitializeComponent();

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _collapseTimer = dispatcher.CreateTimer();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(40);
        _collapseTimer.IsRepeating = true;
        _collapseTimer.Tick += OnCollapseTimerTick;

        _accentSaveTimer = dispatcher.CreateTimer();
        _accentSaveTimer.Interval = TimeSpan.FromMilliseconds(260);
        _accentSaveTimer.IsRepeating = false;
        _accentSaveTimer.Tick += OnAccentSaveTimerTick;

        _edgeRenderer = new EdgeWaveRenderer(
            EdgeSurface,
            WaveBloom,
            WaveOuterTrace,
            WaveSecondaryTrace,
            WaveCoreTrace);
        _bubbleController = new EdgeBubbleController(
            EdgeSurface,
            BubbleBreathHost,
            BubbleBody,
            NotificationIconHost,
            IncomingNotificationPulse,
            NotificationHaloPrimary,
            NotificationHaloSecondary,
            UnreadRing,
            LauncherRing);
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
        RegisterVisualTestAccelerator(VirtualKey.F6);
        RegisterVisualTestAccelerator(VirtualKey.F7);
        RegisterVisualTestAccelerator(VirtualKey.F8);
        RegisterVisualTestAccelerator(VirtualKey.F9);
        RegisterVisualTestAccelerator(VirtualKey.F10);
        RegisterVisualTestAccelerator(VirtualKey.F11);
#endif
        LoadSettingsControls();
        ApplyEdgeSide();
    }

    public EdgeViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
#if NYKUR_EDGE_VISUAL_TEST
        _visualTestPlaying = ViewModel.IsPlaying;
        _windowController.SetVisualInspectionStatus("NyKur Edge QA · loaded");
#endif
        _edgeRenderer.Start();
#if NYKUR_EDGE_VISUAL_TEST
        _windowController.SetVisualInspectionStatus("NyKur Edge QA · renderer");
#endif
        _edgeRenderer.SetPlaying(ViewModel.IsPlaying);
        _edgeRenderer.SetIntensity(ViewModel.Settings.Appearance.AnimationIntensity);
        _bubbleController.Start(ViewModel.IsPlaying);
#if NYKUR_EDGE_VISUAL_TEST
        _windowController.SetVisualInspectionStatus(
            $"NyKur Edge QA · {_windowController.IsExpanded} · {ActualWidth:F0}x{ActualHeight:F0} · surface {EdgeSurface.ActualWidth:F0}x{EdgeSurface.ActualHeight:F0}");
#endif
        _bubbleController.SetUnread(ViewModel.HasNotification);
        _bubbleController.SetPinned(false);
        ViewModel.RefreshNotificationAccess();
        _ = LoadStartupStateAsync();
        _ = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            _windowController.EnableAdaptiveRegion);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Cleanup();
    }

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
        _accentSaveTimer.Stop();
        _accentSaveTimer.Tick -= OnAccentSaveTimerTick;
        _edgeRenderer.Dispose();
        _bubbleController.Dispose();
        _accentController.Dispose();
        ViewModel.Dispose();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _collapseTimer.Stop();
        _stateMachine.PointerEntered();
        _windowController.SetExpanded(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _stateMachine.PointerExited(DateTimeOffset.Now);
        if (!_settingsOpen && !_stateMachine.State.IsPinnedOpen)
        {
            _collapseTimer.Start();
        }
    }

    private void OnCollapseTimerTick(DispatcherQueueTimer sender, object args)
    {
        var state = _stateMachine.Advance(DateTimeOffset.Now);
        if (state.Visibility == EdgeVisibility.Collapsed && !_settingsOpen)
        {
            _collapseTimer.Stop();
            _windowController.SetExpanded(false);
        }
    }

    private void OnExpansionProgressChanged(object? sender, double progress)
    {
        LayoutGrid.Width = EdgeWindowLayout.CollapsedWidthDip +
                           ((EdgeWindowLayout.ExpandedWidthDip - EdgeWindowLayout.CollapsedWidthDip) * progress);
        EdgeSurface.Width = EdgeWindowLayout.CollapsedWidthDip +
                            ((ExpandedWaveSurfaceWidthDip - EdgeWindowLayout.CollapsedWidthDip) * progress);
        _edgeRenderer.SetExpansionProgress(progress);
        if (progress > 0.001)
        {
            ExpandedSurface.Visibility = Visibility.Visible;
            PanelContent.Visibility = Visibility.Visible;
        }

        var shellProgress = Math.Clamp((progress - 0.015) / 0.78, 0, 1);
        ExpandedShellTransform.ScaleX = 0.08 + (shellProgress * 0.92);
        ExpandedShellTransform.ScaleY = 0.16 + (Math.Pow(shellProgress, 0.62) * 0.84);
        ExpandedSurface.Opacity = Math.Clamp((progress - 0.035) / 0.48, 0, 1);

        var contentProgress = Math.Clamp((progress - 0.22) / 0.62, 0, 1);
        PanelContent.Opacity = contentProgress;
        PanelContent.IsHitTestVisible = contentProgress >= 0.98;
        var direction = EffectiveSide == EdgeSide.Right ? 1 : -1;
        PanelTransform.TranslateX = direction * (1 - contentProgress) * 18;
        PanelTransform.ScaleX = 0.97 + (contentProgress * 0.03);
        PanelTransform.ScaleY = 0.97 + (contentProgress * 0.03);

        if (progress <= 0.001)
        {
            PanelContent.IsHitTestVisible = false;
            PanelContent.Visibility = Visibility.Collapsed;
            ExpandedSurface.Visibility = Visibility.Collapsed;
        }
    }

    private void OnEdgeLauncherClicked(object sender, RoutedEventArgs e)
    {
        SetPinnedOpen(!_stateMachine.State.IsPinnedOpen);
#if NYKUR_EDGE_VISUAL_TEST
        _windowController.SetVisualInspectionStatus(
            $"NyKur Edge QA · {(_stateMachine.State.IsPinnedOpen ? "orb pinned" : "orb released")}");
#endif
    }

    private void SetPinnedOpen(bool pinned)
    {
        _collapseTimer.Stop();
        var state = _stateMachine.SetPinned(pinned, DateTimeOffset.Now);
        _bubbleController.SetPinned(state.IsPinnedOpen);
        _windowController.SetPinnedInteraction(state.IsPinnedOpen);

        if (state.IsPinnedOpen)
        {
            _windowController.SetExpanded(true);
        }
        else if (!state.IsPointerInside && !state.IsGlanceActive && !_settingsOpen)
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
                _bubbleController.SetPlaying(ViewModel.IsPlaying);
                break;
            case nameof(EdgeViewModel.HasNotification):
                _bubbleController.SetUnread(ViewModel.HasNotification);
                break;
            case nameof(EdgeViewModel.Settings):
                LoadSettingsControls();
                ApplyEdgeSide();
                _windowController.ApplySettings();
                _edgeRenderer.SetIntensity(ViewModel.Settings.Appearance.AnimationIntensity);
                break;
        }
    }

    private void OnAccentRequested(AccentColor accent)
    {
        _accentController.TransitionTo(accent);
    }

    private void OnNotificationArrived()
    {
        _edgeRenderer.TriggerNotificationPulse();
        _bubbleController.SetUnread(true);
        _bubbleController.TriggerNotification();
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
            if (!_settingsOpen && !_stateMachine.State.IsPinnedOpen)
            {
                _collapseTimer.Start();
            }
        }

        AnimateGlance(visible);
    }

    private void AnimateGlance(bool visible)
    {
        var visual = ElementCompositionPreview.GetElementVisual(GlanceOverlay);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, visual.Opacity);
        animation.InsertKeyFrame(1, visible ? 1 : 0);
        animation.Duration = TimeSpan.FromMilliseconds(visible ? 220 : 180);
        visual.StartAnimation("Opacity", animation);
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SetSettingsOpen(true);

    private void OnSettingsBackClicked(object sender, RoutedEventArgs e) => SetSettingsOpen(false);

    private void SetSettingsOpen(bool open)
    {
        _settingsOpen = open;
        HomePane.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        SettingsPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _windowController.SetSettingsInteraction(open);
        _windowController.SetExpanded(true);

        if (!open &&
            !_stateMachine.State.IsPointerInside &&
            !_stateMachine.State.IsPinnedOpen)
        {
            _stateMachine.PointerExited(DateTimeOffset.Now);
            _collapseTimer.Start();
        }
    }

    private async void OnPlayPauseClicked(object sender, RoutedEventArgs e) =>
        await ObserveAsync(ViewModel.TogglePlayPauseAsync());

    private async void OnPreviousClicked(object sender, RoutedEventArgs e) =>
        await ObserveAsync(ViewModel.SkipPreviousAsync());

    private async void OnNextClicked(object sender, RoutedEventArgs e) =>
        await ObserveAsync(ViewModel.SkipNextAsync());

    private async void OnMediaProgressPointerReleased(object sender, PointerRoutedEventArgs e) =>
        await ObserveAsync(ViewModel.SeekAsync(MediaProgressSlider.Value));

    private void OnPreviewClockClicked(object sender, RoutedEventArgs e)
    {
        _ = ObserveAsync(_services.Clock.PreviewAsync());
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        var desired = StartupToggle.IsOn;
        var state = await _services.Startup.SetEnabledAsync(desired);
        var enabled = state is StartupState.Enabled or StartupState.EnabledByPolicy;
        _loadingSettings = true;
        StartupToggle.IsOn = enabled;
        _loadingSettings = false;
        await UpdateSettingsAsync(settings => settings with { LaunchOnStartup = enabled });
    }

    private async void OnEdgeSideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || GetSelectedTag(EdgeSideCombo) is not { } selected)
        {
            return;
        }

        var side = Enum.Parse<EdgeSide>(selected);
        await UpdateSettingsAsync(settings => settings with { EdgeSide = side });
    }

    private async void OnAccentModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || GetSelectedTag(AccentModeCombo) is not { } selected)
        {
            return;
        }

        var mode = Enum.Parse<AccentMode>(selected);
        ManualAccentContainer.Visibility = mode == AccentMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        await UpdateSettingsAsync(settings => settings with
        {
            Appearance = settings.Appearance with { AccentMode = mode },
        });
    }

    private void OnManualAccentChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loadingSettings || ViewModel.Settings.Appearance.AccentMode != AccentMode.Manual)
        {
            return;
        }

        _pendingManualAccent = new AccentColor(args.NewColor.R, args.NewColor.G, args.NewColor.B);
        _accentController.TransitionTo(_pendingManualAccent);
        _accentSaveTimer.Stop();
        _accentSaveTimer.Start();
    }

    private async void OnAccentSaveTimerTick(DispatcherQueueTimer sender, object args)
    {
        await UpdateSettingsAsync(settings => settings with
        {
            Appearance = settings.Appearance with { ManualAccent = _pendingManualAccent.ToHex() },
        });
    }

    private async void OnAnimationIntensityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || GetSelectedTag(AnimationIntensityCombo) is not { } selected)
        {
            return;
        }

        var intensity = Enum.Parse<AnimationIntensity>(selected);
        _edgeRenderer.SetIntensity(intensity);
        await UpdateSettingsAsync(settings => settings with
        {
            Appearance = settings.Appearance with { AnimationIntensity = intensity },
        });
    }

    private async void OnMediaToggled(object sender, RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            await UpdateSettingsAsync(settings => settings with
            {
                Media = settings.Media with { Enabled = MediaToggle.IsOn },
            });
        }
    }

    private async void OnNotificationToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        var enabled = NotificationToggle.IsOn;
        if (enabled && ViewModel.NotificationAccess != NotificationAccessState.Allowed)
        {
            var access = await ViewModel.RequestNotificationAccessAsync();
            enabled = access == NotificationAccessState.Allowed;
            _loadingSettings = true;
            NotificationToggle.IsOn = enabled;
            _loadingSettings = false;
        }

        await UpdateSettingsAsync(settings => settings with
        {
            Notifications = settings.Notifications with { Enabled = enabled },
        });
    }

    private async void OnNotificationAccessClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.RequestNotificationAccessAsync();
        _loadingSettings = true;
        NotificationToggle.IsOn = ViewModel.NotificationAccess == NotificationAccessState.Allowed;
        _loadingSettings = false;
    }

    private async void OnOpenNotificationSettingsClicked(object sender, RoutedEventArgs e)
    {
        _ = await Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"));
    }

    private async void OnClockToggled(object sender, RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            await UpdateSettingsAsync(settings => settings with
            {
                Clock = settings.Clock with { Enabled = ClockToggle.IsOn },
            });
        }
    }

    private async void OnClockIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings ||
            GetSelectedTag(ClockIntervalCombo) is not { } selected ||
            !int.TryParse(selected, out var interval))
        {
            return;
        }

        await UpdateSettingsAsync(settings => settings with
        {
            Clock = settings.Clock with { IntervalMinutes = interval },
        });
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        Cleanup();
        _windowController.CloseWindow();
    }

    private void LoadSettingsControls()
    {
        var settings = ViewModel.Settings;
        _loadingSettings = true;
        StartupToggle.IsOn = settings.LaunchOnStartup;
        SetSelectedTag(EdgeSideCombo, settings.EdgeSide.ToString());
        SetSelectedTag(AccentModeCombo, settings.Appearance.AccentMode.ToString());
        ManualAccentContainer.Visibility = settings.Appearance.AccentMode == AccentMode.Manual
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (AccentColor.TryParse(settings.Appearance.ManualAccent, out var accent))
        {
            ManualAccentPicker.Color = Color.FromArgb(255, accent.Red, accent.Green, accent.Blue);
            _pendingManualAccent = accent;
        }

        SetSelectedTag(AnimationIntensityCombo, settings.Appearance.AnimationIntensity.ToString());
        MediaToggle.IsOn = settings.Media.Enabled;
        NotificationToggle.IsOn = settings.Notifications.Enabled;
        ClockToggle.IsOn = settings.Clock.Enabled;
        SetSelectedTag(ClockIntervalCombo, settings.Clock.IntervalMinutes.ToString(CultureInfo.InvariantCulture));
        _loadingSettings = false;
    }

    private void ApplyEdgeSide() => ApplyEdgeSide(EffectiveSide);

    private void ApplyEdgeSide(EdgeSide side)
    {
        var settings = ViewModel.Settings;
        EdgeAnchor.Width = Math.Clamp(settings.Appearance.EdgeThickness * 0.09, 0.8, 2.2);
        _edgeRenderer.SetSide(side);

        if (side == EdgeSide.Right)
        {
            ExpandedSurface.CornerRadius = new CornerRadius(72, 0, 0, 72);
            ExpandedSurface.RenderTransformOrigin = new Windows.Foundation.Point(1, 0.5);
            PanelContent.Margin = new Thickness(50, 18, 34, 18);
            EdgeSurface.HorizontalAlignment = HorizontalAlignment.Right;
            EdgeAnchor.HorizontalAlignment = HorizontalAlignment.Right;
            EdgeBubbleRoot.HorizontalAlignment = HorizontalAlignment.Right;
            EdgeBubbleRoot.Margin = new Thickness(0, 0, -28, 0);
            EdgeLauncherButton.HorizontalAlignment = HorizontalAlignment.Right;
            EdgeLauncherButton.Margin = new Thickness(0, 0, -28, 0);
            IncomingNotificationPulse.HorizontalAlignment = HorizontalAlignment.Right;
            IncomingNotificationPulse.Margin = new Thickness(0, 64, 3, 0);
            BubbleSheen.HorizontalAlignment = HorizontalAlignment.Left;
            BubbleSheen.Margin = new Thickness(2, 3, 0, 0);
            BubbleSpark.HorizontalAlignment = HorizontalAlignment.Left;
            BubbleSpark.Margin = new Thickness(5, 5, 0, 0);
        }
        else
        {
            ExpandedSurface.CornerRadius = new CornerRadius(0, 72, 72, 0);
            ExpandedSurface.RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5);
            PanelContent.Margin = new Thickness(34, 18, 50, 18);
            EdgeSurface.HorizontalAlignment = HorizontalAlignment.Left;
            EdgeAnchor.HorizontalAlignment = HorizontalAlignment.Left;
            EdgeBubbleRoot.HorizontalAlignment = HorizontalAlignment.Left;
            EdgeBubbleRoot.Margin = new Thickness(-28, 0, 0, 0);
            EdgeLauncherButton.HorizontalAlignment = HorizontalAlignment.Left;
            EdgeLauncherButton.Margin = new Thickness(-28, 0, 0, 0);
            IncomingNotificationPulse.HorizontalAlignment = HorizontalAlignment.Left;
            IncomingNotificationPulse.Margin = new Thickness(3, 64, 0, 0);
            BubbleSheen.HorizontalAlignment = HorizontalAlignment.Right;
            BubbleSheen.Margin = new Thickness(0, 3, 2, 0);
            BubbleSpark.HorizontalAlignment = HorizontalAlignment.Right;
            BubbleSpark.Margin = new Thickness(0, 5, 5, 0);
        }
    }

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
            case VirtualKey.F6:
                _visualTestPlaying = !_visualTestPlaying;
                _edgeRenderer.SetPlaying(_visualTestPlaying);
                _bubbleController.SetPlaying(_visualTestPlaying);
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · {(_visualTestPlaying ? "playing" : "idle")}");
                break;
            case VirtualKey.F7:
                _edgeRenderer.TriggerNotificationPulse();
                _bubbleController.SetUnread(true);
                _bubbleController.TriggerNotification(timingScale: 4);
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
                _windowController.SetVisualInspectionStatus(
                    $"NyKur Edge QA · {(_stateMachine.State.IsPinnedOpen ? "launcher pinned" : "launcher released")}");
                break;
            default:
                return;
        }

        args.Handled = true;
    }
#endif

    private async Task LoadStartupStateAsync()
    {
        var state = await _services.Startup.GetStateAsync();
        var enabled = state is StartupState.Enabled or StartupState.EnabledByPolicy;
        _loadingSettings = true;
        StartupToggle.IsOn = enabled;
        StartupToggle.IsEnabled = state != StartupState.Unavailable;
        _loadingSettings = false;

        if (ViewModel.Settings.LaunchOnStartup != enabled)
        {
            await UpdateSettingsAsync(settings => settings with { LaunchOnStartup = enabled });
        }
    }

    private async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> update)
    {
        try
        {
            await _services.Settings.UpdateAsync(update);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Settings update failed: {exception}");
            LoadSettingsControls();
        }
    }

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
        _ = await ObserveWithResultAsync(operation);
    }

    private static async Task<bool> ObserveWithResultAsync(Task<bool> operation)
    {
        try
        {
            return await operation;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"UI operation failed: {exception}");
            return false;
        }
    }

    private static SolidColorBrush GetApplicationBrush(string key) =>
        (SolidColorBrush)Application.Current.Resources[key];

    private static void SetSelectedTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetSelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    public void Dispose() => Cleanup();
}

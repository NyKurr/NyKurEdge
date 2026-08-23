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
using NyKurEdge.Core.Notifications;
using NyKurEdge.Core.Settings;
using NyKurEdge.Core.State;
using Windows.System;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace NyKurEdge.App;

public sealed partial class MainPage : Page, IDisposable
{
    private readonly AppServices _services;
    private readonly EdgeWindowController _windowController;
    private readonly EdgeInteractionStateMachine _stateMachine = new();
    private readonly DispatcherQueueTimer _collapseTimer;
    private readonly DispatcherQueueTimer _accentSaveTimer;
    private readonly ProceduralEdgeAnimator _edgeAnimator;
    private readonly AccentTransitionController _accentController;
    private AccentColor _pendingManualAccent = AccentColor.Default;
    private bool _loadingSettings;
    private bool _settingsOpen;
    private bool _cleanedUp;

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

        _edgeAnimator = new ProceduralEdgeAnimator(EdgeWave, EdgeRail);
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

        LoadSettingsControls();
        ApplyEdgeSide();
    }

    public EdgeViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _edgeAnimator.Start();
        _edgeAnimator.SetPlaying(ViewModel.IsPlaying);
        _edgeAnimator.SetIntensity(ViewModel.Settings.Appearance.AnimationIntensity);
        ViewModel.RefreshNotificationAccess();
        _ = LoadStartupStateAsync();
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
        _collapseTimer.Stop();
        _collapseTimer.Tick -= OnCollapseTimerTick;
        _accentSaveTimer.Stop();
        _accentSaveTimer.Tick -= OnAccentSaveTimerTick;
        _edgeAnimator.Dispose();
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
        if (!_settingsOpen)
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
        var contentProgress = Math.Clamp((progress - 0.08) / 0.72, 0, 1);
        PanelContent.Opacity = contentProgress;
        var direction = ViewModel.Settings.EdgeSide == EdgeSide.Right ? 1 : -1;
        PanelTransform.TranslateX = direction * (1 - contentProgress) * 12;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EdgeViewModel.IsPlaying):
                _edgeAnimator.SetPlaying(ViewModel.IsPlaying);
                break;
            case nameof(EdgeViewModel.HasNotification):
                UnreadDot.Opacity = ViewModel.HasNotification ? 0.76 : 0;
                break;
            case nameof(EdgeViewModel.Settings):
                LoadSettingsControls();
                ApplyEdgeSide();
                _windowController.ApplySettings();
                _edgeAnimator.SetIntensity(ViewModel.Settings.Appearance.AnimationIntensity);
                break;
        }
    }

    private void OnAccentRequested(AccentColor accent)
    {
        _accentController.TransitionTo(accent);
    }

    private void OnNotificationArrived()
    {
        _edgeAnimator.TriggerNotificationPulse();
        UnreadDot.Opacity = 0.76;
        AnimateNotificationPulse();
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
            if (!_settingsOpen)
            {
                _collapseTimer.Start();
            }
        }

        AnimateGlance(visible);
    }

    private void AnimateNotificationPulse()
    {
        var pulseVisual = ElementCompositionPreview.GetElementVisual(NotificationPulse);
        pulseVisual.CenterPoint = new Vector3(
            (float)(NotificationPulse.ActualWidth / 2),
            (float)(NotificationPulse.ActualHeight / 2),
            0);
        var compositor = pulseVisual.Compositor;

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(0.5f, 0.5f, 1));
        scale.InsertKeyFrame(0.42f, new Vector3(4.6f, 4.6f, 1));
        scale.InsertKeyFrame(1, Vector3.One);
        scale.Duration = TimeSpan.FromMilliseconds(920);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(0.16f, 0.92f);
        opacity.InsertKeyFrame(0.72f, 0.24f);
        opacity.InsertKeyFrame(1, 0);
        opacity.Duration = scale.Duration;

        pulseVisual.StartAnimation("Scale", scale);
        pulseVisual.StartAnimation("Opacity", opacity);
        AnimateRippleTrace(RippleUp, originAtBottom: true);
        AnimateRippleTrace(RippleDown, originAtBottom: false);
    }

    private static void AnimateRippleTrace(FrameworkElement element, bool originAtBottom)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(
            (float)(element.ActualWidth / 2),
            originAtBottom ? (float)element.ActualHeight : 0,
            0);
        var compositor = visual.Compositor;

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(1, 0.05f, 1));
        scale.InsertKeyFrame(0.78f, new Vector3(1, 5.5f, 1));
        scale.InsertKeyFrame(1, new Vector3(1, 6.2f, 1));
        scale.Duration = TimeSpan.FromMilliseconds(830);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(0.12f, 0.46f);
        opacity.InsertKeyFrame(1, 0);
        opacity.Duration = scale.Duration;

        visual.StartAnimation("Scale", scale);
        visual.StartAnimation("Opacity", opacity);
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

        if (!open && !_stateMachine.State.IsPointerInside)
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
        _edgeAnimator.SetIntensity(intensity);
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

    private void ApplyEdgeSide()
    {
        var settings = ViewModel.Settings;
        var edgeWidth = new GridLength(settings.Appearance.EdgeThickness);
        if (settings.EdgeSide == EdgeSide.Right)
        {
            PanelColumn.Width = new GridLength(1, GridUnitType.Star);
            EdgeColumn.Width = edgeWidth;
            Grid.SetColumn(PanelContent, 0);
            Grid.SetColumn(EdgeRail, 1);
            ShellSurface.CornerRadius = new CornerRadius(22, 0, 0, 22);
        }
        else
        {
            PanelColumn.Width = edgeWidth;
            EdgeColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(EdgeRail, 0);
            Grid.SetColumn(PanelContent, 1);
            ShellSurface.CornerRadius = new CornerRadius(0, 22, 22, 0);
        }
    }

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

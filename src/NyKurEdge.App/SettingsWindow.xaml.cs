using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NyKurEdge.Core.Appearance;
using NyKurEdge.Core.Notifications;
using NyKurEdge.Core.Settings;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace NyKurEdge.App;

public sealed partial class SettingsWindow : Window, IDisposable
{
    private readonly AppServices _services;
    private readonly Action _exitApplication;
    private readonly DispatcherQueueTimer _accentSaveTimer;
    private AccentColor _pendingManualAccent = AccentColor.Default;
    private bool _loading;
    private bool _disposed;

    public SettingsWindow(AppServices services, Action exitApplication)
    {
        _services = services;
        _exitApplication = exitApplication;
        InitializeComponent();

        AppWindow.SetIcon("Assets/AppIcon.ico");
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        PositionWindow();
        _accentSaveTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _accentSaveTimer.Interval = TimeSpan.FromMilliseconds(260);
        _accentSaveTimer.IsRepeating = false;
        _accentSaveTimer.Tick += OnAccentSaveTimerTick;

        Closed += OnClosed;
        LoadControls();
        _ = LoadStartupStateAsync();
    }

    private void PositionWindow()
    {
        var display = _services.DisplayService.GetPrimaryDisplay();
        var scale = display.Dpi > 0 ? display.Dpi / 96d : 1d;
        var width = Math.Min(display.WorkArea.Width, (int)Math.Round(540 * scale));
        var height = Math.Min(display.WorkArea.Height, (int)Math.Round(720 * scale));
        var x = display.WorkArea.X + ((display.WorkArea.Width - width) / 2);
        var y = display.WorkArea.Y + ((display.WorkArea.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private async void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var state = await _services.Startup.SetEnabledAsync(StartupToggle.IsOn);
        var enabled = state is StartupState.Enabled or StartupState.EnabledByPolicy;
        _loading = true;
        StartupToggle.IsOn = enabled;
        _loading = false;
        await UpdateSettingsAsync(settings => settings with { LaunchOnStartup = enabled });
    }

    private async void OnEdgeSideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GetSelectedTag(EdgeSideCombo) is not { } selected)
        {
            return;
        }

        await UpdateSettingsAsync(settings => settings with
        {
            EdgeSide = Enum.Parse<EdgeSide>(selected),
        });
    }

    private async void OnAccentModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GetSelectedTag(AccentModeCombo) is not { } selected)
        {
            return;
        }

        var mode = Enum.Parse<AccentMode>(selected);
        ManualAccentContainer.Visibility = mode == AccentMode.Manual
            ? Visibility.Visible
            : Visibility.Collapsed;
        await UpdateSettingsAsync(settings => settings with
        {
            Appearance = settings.Appearance with { AccentMode = mode },
        });
    }

    private void OnManualAccentChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading || _services.Settings.Current.Appearance.AccentMode != AccentMode.Manual)
        {
            return;
        }

        _pendingManualAccent = new AccentColor(args.NewColor.R, args.NewColor.G, args.NewColor.B);
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
        if (_loading || GetSelectedTag(AnimationIntensityCombo) is not { } selected)
        {
            return;
        }

        await UpdateSettingsAsync(settings => settings with
        {
            Appearance = settings.Appearance with
            {
                AnimationIntensity = Enum.Parse<AnimationIntensity>(selected),
            },
        });
    }

    private async void OnMediaToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            await UpdateSettingsAsync(settings => settings with
            {
                Media = settings.Media with { Enabled = MediaToggle.IsOn },
            });
        }
    }

    private async void OnNotificationToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = NotificationToggle.IsOn;
        if (enabled && _services.Notifications.AccessState != NotificationAccessState.Allowed)
        {
            var access = await _services.Notifications.RequestAccessAsync();
            enabled = access == NotificationAccessState.Allowed;
            _loading = true;
            NotificationToggle.IsOn = enabled;
            _loading = false;
        }

        await UpdateSettingsAsync(settings => settings with
        {
            Notifications = settings.Notifications with { Enabled = enabled },
        });
        UpdateNotificationAccessCopy();
    }

    private async void OnNotificationAccessClicked(object sender, RoutedEventArgs e)
    {
        _ = await _services.Notifications.RequestAccessAsync();
        UpdateNotificationAccessCopy();
    }

    private async void OnOpenNotificationSettingsClicked(object sender, RoutedEventArgs e) =>
        _ = await Launcher.LaunchUriAsync(new Uri("ms-settings:notifications"));

    private async void OnNotificationPrivacyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GetSelectedTag(NotificationPrivacyCombo) is not { } selected)
        {
            return;
        }

        await UpdateSettingsAsync(settings => settings with
        {
            Notifications = settings.Notifications with
            {
                Privacy = Enum.Parse<NotificationPrivacy>(selected),
            },
        });
    }

    private async void OnClockToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            await UpdateSettingsAsync(settings => settings with
            {
                Clock = settings.Clock with { Enabled = ClockToggle.IsOn },
            });
        }
    }

    private async void OnClockIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading ||
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

    private void OnPreviewClockClicked(object sender, RoutedEventArgs e) =>
        _ = ObserveAsync(_services.Clock.PreviewAsync());

    private void OnExitClicked(object sender, RoutedEventArgs e) => _exitApplication();

    private void LoadControls()
    {
        var settings = _services.Settings.Current;
        _loading = true;
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
        SetSelectedTag(NotificationPrivacyCombo, settings.Notifications.Privacy.ToString());
        ClockToggle.IsOn = settings.Clock.Enabled;
        SetSelectedTag(ClockIntervalCombo, settings.Clock.IntervalMinutes.ToString(CultureInfo.InvariantCulture));
        _loading = false;
        UpdateNotificationAccessCopy();
    }

    private void UpdateNotificationAccessCopy()
    {
        NotificationAccessLabel.Text = _services.Notifications.AccessState switch
        {
            NotificationAccessState.Allowed => "Notification access is allowed.",
            NotificationAccessState.Denied => "Access is denied in Windows Settings.",
            NotificationAccessState.Unspecified => "Access has not been requested.",
            _ => "The Windows notification listener is unavailable.",
        };
    }

    private async Task LoadStartupStateAsync()
    {
        var state = await _services.Startup.GetStateAsync();
        var enabled = state is StartupState.Enabled or StartupState.EnabledByPolicy;
        _loading = true;
        StartupToggle.IsOn = enabled;
        StartupToggle.IsEnabled = state != StartupState.Unavailable;
        _loading = false;
        if (_services.Settings.Current.LaunchOnStartup != enabled)
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
            LoadControls();
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
            System.Diagnostics.Debug.WriteLine($"Settings operation failed: {exception}");
        }
    }

    private static void SetSelectedTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetSelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private void OnClosed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Closed -= OnClosed;
        _accentSaveTimer.Stop();
        _accentSaveTimer.Tick -= OnAccentSaveTimerTick;
    }
}

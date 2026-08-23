using NyKurEdge.Core.Appearance;
using NyKurEdge.Core.Glances;
using NyKurEdge.Core.Media;
using NyKurEdge.Core.Notifications;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Events;

public sealed record MediaChanged(MediaSnapshot Media);

public sealed record PlaybackStateChanged(MediaPlaybackState PlaybackState);

public sealed record NotificationReceived(NotificationSnapshot Notification);

public sealed record NotificationDismissed(uint NotificationId);

public sealed record AccentChanged(AccentColor Accent);

public sealed record GlanceRequested(GlancePresentation Glance);

public sealed record GlanceEnded(Guid GlanceId);

public sealed record SettingsChanged(AppSettings Settings);

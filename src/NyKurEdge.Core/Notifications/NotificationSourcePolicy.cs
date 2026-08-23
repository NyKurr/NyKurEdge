using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Notifications;

public static class NotificationSourcePolicy
{
    public static bool Allows(NotificationSettings settings, string sourceAppId)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Enabled &&
               (!settings.SourceOverrides.TryGetValue(sourceAppId, out var enabled) || enabled);
    }
}

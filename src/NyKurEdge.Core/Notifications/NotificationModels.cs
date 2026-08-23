namespace NyKurEdge.Core.Notifications;

public enum NotificationAccessState
{
    Unavailable,
    Unspecified,
    Denied,
    Allowed,
}

public sealed record NotificationSnapshot(
    uint Id,
    string SourceAppId,
    string ApplicationName,
    string Title,
    string Message,
    DateTimeOffset Timestamp,
    byte[]? ApplicationIcon)
{
    public string PreviewFor(Settings.NotificationPrivacy privacy) => privacy switch
    {
        Settings.NotificationPrivacy.AppOnly => string.Empty,
        Settings.NotificationPrivacy.SenderAndTitle => Title,
        _ => Message,
    };
}

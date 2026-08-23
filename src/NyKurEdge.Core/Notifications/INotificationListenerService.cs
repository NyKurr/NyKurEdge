namespace NyKurEdge.Core.Notifications;

public interface INotificationListenerService : IAsyncDisposable
{
    NotificationAccessState AccessState { get; }

    IReadOnlyCollection<string> DiscoveredSources { get; }

    Task<NotificationAccessState> RequestAccessAsync(CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

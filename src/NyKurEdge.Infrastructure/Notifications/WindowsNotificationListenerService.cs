using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using NyKurEdge.Core.Events;
using NyKurEdge.Core.Notifications;

namespace NyKurEdge.Infrastructure.Notifications;

public sealed class WindowsNotificationListenerService(IEventBus eventBus) : INotificationListenerService
{
    private const ulong MaximumIconBytes = 2 * 1024 * 1024;
    private readonly Lock _sourcesGate = new();
    private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;
    private bool _started;

    public NotificationAccessState AccessState => MapAccessState(_listener.GetAccessStatus());

    public IReadOnlyCollection<string> DiscoveredSources
    {
        get
        {
            lock (_sourcesGate)
            {
                return [.. _sources];
            }
        }
    }

    public async Task<NotificationAccessState> RequestAccessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await _listener.RequestAccessAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return MapAccessState(status);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started || AccessState != NotificationAccessState.Allowed)
        {
            return;
        }

        _listener.NotificationChanged += OnNotificationChanged;
        _started = true;

        var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
        var latest = current.OrderByDescending(notification => notification.CreationTime).FirstOrDefault();
        if (latest is not null)
        {
            var snapshot = await ParseAsync(latest, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                await eventBus.PublishAsync(new NotificationReceived(snapshot), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            _listener.NotificationChanged -= OnNotificationChanged;
            _started = false;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task HandleChangeAsync(UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Removed)
        {
            await eventBus.PublishAsync(new NotificationDismissed(args.UserNotificationId)).ConfigureAwait(false);
            return;
        }

        var notification = _listener.GetNotification(args.UserNotificationId);
        if (notification is null)
        {
            return;
        }

        var snapshot = await ParseAsync(notification, CancellationToken.None).ConfigureAwait(false);
        if (snapshot is not null)
        {
            await eventBus.PublishAsync(new NotificationReceived(snapshot)).ConfigureAwait(false);
        }
    }

    private async Task<NotificationSnapshot?> ParseAsync(
        UserNotification notification,
        CancellationToken cancellationToken)
    {
        var appInfo = notification.AppInfo;
        var sourceAppId = appInfo.AppUserModelId ?? appInfo.PackageFamilyName ?? "unknown";
        var applicationName = appInfo.DisplayInfo?.DisplayName;
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            applicationName = sourceAppId;
        }

        lock (_sourcesGate)
        {
            _sources.Add(sourceAppId);
        }

        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)
                      ?? notification.Notification.Visual.Bindings.FirstOrDefault();
        var text = binding?.GetTextElements().Select(element => element.Text?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray() ?? [];

        var title = text.ElementAtOrDefault(0) ?? applicationName;
        var message = string.Join(" · ", text.Skip(1));
        var icon = await ReadIconAsync(appInfo.DisplayInfo?.GetLogo(new Size(48, 48)), cancellationToken)
            .ConfigureAwait(false);

        return new NotificationSnapshot(
            notification.Id,
            sourceAppId,
            applicationName,
            title,
            message,
            notification.CreationTime,
            icon);
    }

    private static async Task<byte[]?> ReadIconAsync(
        RandomAccessStreamReference? iconReference,
        CancellationToken cancellationToken)
    {
        if (iconReference is null)
        {
            return null;
        }

        using var stream = await iconReference.OpenReadAsync();
        if (stream.Size is 0 or > MaximumIconBytes)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        _ = await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static NotificationAccessState MapAccessState(UserNotificationListenerAccessStatus status) => status switch
    {
        UserNotificationListenerAccessStatus.Allowed => NotificationAccessState.Allowed,
        UserNotificationListenerAccessStatus.Denied => NotificationAccessState.Denied,
        UserNotificationListenerAccessStatus.Unspecified => NotificationAccessState.Unspecified,
        _ => NotificationAccessState.Unavailable,
    };

    private async void OnNotificationChanged(
        UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        try
        {
            await HandleChangeAsync(args).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Notification update failed: {exception}");
        }
    }
}

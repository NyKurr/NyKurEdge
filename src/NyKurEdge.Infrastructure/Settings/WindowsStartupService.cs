using Windows.ApplicationModel;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.Infrastructure.Settings;

public sealed class WindowsStartupService : IStartupService
{
    public const string TaskId = "NyKurEdgeStartup";

    public async Task<StartupState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = await StartupTask.GetAsync(TaskId);
            return MapState(task.State);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Startup task query failed: {exception}");
            return StartupState.Unavailable;
        }
    }

    public async Task<StartupState> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                return MapState(await task.RequestEnableAsync());
            }

            task.Disable();
            return MapState(task.State);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Startup task update failed: {exception}");
            return StartupState.Unavailable;
        }
    }

    private static StartupState MapState(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => StartupState.Enabled,
        StartupTaskState.EnabledByPolicy => StartupState.EnabledByPolicy,
        StartupTaskState.DisabledByUser => StartupState.DisabledByUser,
        StartupTaskState.Disabled => StartupState.Disabled,
        _ => StartupState.Unavailable,
    };
}

namespace NyKurEdge.Core.Settings;

public enum StartupState
{
    Unavailable,
    Disabled,
    DisabledByUser,
    Enabled,
    EnabledByPolicy,
}

public interface IStartupService
{
    Task<StartupState> GetStateAsync(CancellationToken cancellationToken = default);

    Task<StartupState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

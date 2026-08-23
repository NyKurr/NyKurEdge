namespace NyKurEdge.Core.Glances;

public enum GlanceKind
{
    Clock,
}

public sealed record GlancePresentation(
    Guid Id,
    GlanceKind Kind,
    string Eyebrow,
    string PrimaryText,
    string SecondaryText,
    TimeSpan Duration);

public interface IGlanceCoordinator : IDisposable
{
    GlancePresentation? Current { get; }

    Task ShowAsync(GlancePresentation glance, CancellationToken cancellationToken = default);
}

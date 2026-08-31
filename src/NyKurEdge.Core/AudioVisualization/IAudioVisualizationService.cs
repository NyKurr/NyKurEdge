namespace NyKurEdge.Core.AudioVisualization;

/// <summary>
/// Provides a continuously updated, in-memory summary of system-output audio.
/// </summary>
public interface IAudioVisualizationService : IAsyncDisposable
{
    AudioSpectrumSnapshot Current { get; }

    bool IsCapturing { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

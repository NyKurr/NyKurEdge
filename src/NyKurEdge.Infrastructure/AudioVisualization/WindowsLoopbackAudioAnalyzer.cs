using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NyKurEdge.Core.AudioVisualization;

namespace NyKurEdge.Infrastructure.AudioVisualization;

/// <summary>
/// Analyzes the default Windows render endpoint in memory. Audio is never retained or written to disk.
/// </summary>
public sealed class WindowsLoopbackAudioAnalyzer : IAudioVisualizationService
{
    private const int SampleRate = 48_000;
    private const int ChannelCount = 2;
    private const int FftSize = 2_048;
    private const int HopSize = 1_024;
    private const double LowBandUpperHz = 250;
    private const double MidBandUpperHz = 2_000;
    private const double HighBandUpperHz = 12_000;
    private const double AttackSeconds = 0.055;
    private const double ReleaseSeconds = 0.32;
    private const double LevelCompression = 32;
    private const int MaximumRecoveryAttempts = 4;

    private static readonly TimeSpan[] RecoveryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1_500),
        TimeSpan.FromMilliseconds(3_000),
    ];

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _snapshotWriteGate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _fftReal = new double[FftSize];
    private readonly double[] _fftImaginary = new double[FftSize];

    private WasapiRecorder? _recorder;
    private CancellationTokenSource? _recoveryCancellation;
    private int _ringWriteIndex;
    private int _samplesBuffered;
    private int _samplesSinceAnalysis;
    private double _smoothedEnergy;
    private double _smoothedLow;
    private double _smoothedMid;
    private double _smoothedHigh;
    private int _isCapturing;
    private int _captureDesired;
    private int _remainingRecoveryAttempts;
    private bool _recoveryRequested;
    private int _disposed;

    // Readers remain lock-free. Writers are serialized because lifecycle and capture callbacks may publish concurrently.
    private int _snapshotVersion;
    private long _energyBits;
    private long _lowBits;
    private long _midBits;
    private long _highBits;
    private long _timestamp;

    public WindowsLoopbackAudioAnalyzer()
    {
        for (var index = 0; index < FftSize; index++)
        {
            _window[index] = 0.5 - (0.5 * Math.Cos((2 * Math.PI * index) / (FftSize - 1)));
        }
    }

    public AudioSpectrumSnapshot Current
    {
        get
        {
            var spin = new SpinWait();
            while (true)
            {
                var versionBefore = Volatile.Read(ref _snapshotVersion);
                if ((versionBefore & 1) != 0)
                {
                    spin.SpinOnce();
                    continue;
                }

                var energy = BitConverter.Int64BitsToDouble(Volatile.Read(ref _energyBits));
                var low = BitConverter.Int64BitsToDouble(Volatile.Read(ref _lowBits));
                var mid = BitConverter.Int64BitsToDouble(Volatile.Read(ref _midBits));
                var high = BitConverter.Int64BitsToDouble(Volatile.Read(ref _highBits));
                var timestamp = Volatile.Read(ref _timestamp);
                var versionAfter = Volatile.Read(ref _snapshotVersion);

                if (versionBefore == versionAfter)
                {
                    return new AudioSpectrumSnapshot(energy, low, mid, high, timestamp);
                }

                spin.SpinOnce();
            }
        }
    }

    public bool IsCapturing => Volatile.Read(ref _isCapturing) != 0;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Volatile.Write(ref _captureDesired, 1);
        Volatile.Write(ref _remainingRecoveryAttempts, MaximumRecoveryAttempts);
        CancelRecovery();

        try
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (IsCapturing)
                {
                    return;
                }

                if (!await TryStartRecorderAsync(cancellationToken).ConfigureAwait(false))
                {
                    ScheduleRecovery();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch
        {
            Volatile.Write(ref _captureDesired, 0);
            CancelRecovery();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _captureDesired, 0);
        Volatile.Write(ref _remainingRecoveryAttempts, 0);
        CancelRecovery();

        // Once stop intent is recorded, recorder cleanup must complete even if the
        // caller's operation was cancelled while another lifecycle transition held
        // the gate. Otherwise the capture callback can continue running indefinitely.
        _ = cancellationToken;
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var recorder = TakeRecorder();
            await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
            ResetAnalysis();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _captureDesired, 0);
        Volatile.Write(ref _remainingRecoveryAttempts, 0);
        CancelRecovery();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var recorder = TakeRecorder();
            await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
            ResetAnalysis();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        _ = devicePosition;
        _ = qpcPosition;
        if (IsCaptureDesired)
        {
            Volatile.Write(ref _remainingRecoveryAttempts, MaximumRecoveryAttempts);
        }

        try
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                PushSilentFrames(buffer.Length / (sizeof(float) * ChannelCount));
                return;
            }

            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            var completeSampleCount = samples.Length - (samples.Length % ChannelCount);
            for (var index = 0; index < completeSampleCount; index += ChannelCount)
            {
                double mono = 0;
                for (var channel = 0; channel < ChannelCount; channel++)
                {
                    var sample = samples[index + channel];
                    if (float.IsFinite(sample))
                    {
                        mono += sample;
                    }
                }

                PushSample((float)Math.Clamp(mono / ChannelCount, -1, 1));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An analyzer fault must not escape through NAudio and terminate its capture thread.
            Debug.WriteLine($"System-output audio analysis failed: {exception}");
        }
    }

    private void PushSilentFrames(int frameCount)
    {
        for (var frame = 0; frame < frameCount; frame++)
        {
            PushSample(0);
        }
    }

    private void PushSample(float sample)
    {
        _ring[_ringWriteIndex] = sample;
        _ringWriteIndex = (_ringWriteIndex + 1) & (FftSize - 1);
        _samplesBuffered = Math.Min(FftSize, _samplesBuffered + 1);
        _samplesSinceAnalysis++;

        if (_samplesBuffered == FftSize && _samplesSinceAnalysis >= HopSize)
        {
            _samplesSinceAnalysis = 0;
            AnalyzeSpectrum();
        }
    }

    private void AnalyzeSpectrum()
    {
        double timeDomainPower = 0;
        var sourceIndex = _ringWriteIndex;
        for (var index = 0; index < FftSize; index++)
        {
            var sample = _ring[sourceIndex];
            sourceIndex = (sourceIndex + 1) & (FftSize - 1);
            timeDomainPower += sample * sample;
            _fftReal[index] = sample * _window[index];
            _fftImaginary[index] = 0;
        }

        TransformInPlace(_fftReal, _fftImaginary);

        var lowPower = MeasureBandPower(20, LowBandUpperHz);
        var midPower = MeasureBandPower(LowBandUpperHz, MidBandUpperHz);
        var highPower = MeasureBandPower(MidBandUpperHz, HighBandUpperHz);
        var overallRms = Math.Sqrt(timeDomainPower / FftSize);
        var coherentWindowGain = FftSize / 2.0;

        var energy = NormalizeLevel(overallRms);
        var low = NormalizeLevel(Math.Sqrt(lowPower) / coherentWindowGain);
        var mid = NormalizeLevel(Math.Sqrt(midPower) / coherentWindowGain);
        var high = NormalizeLevel(Math.Sqrt(highPower) / coherentWindowGain);

        _smoothedEnergy = Smooth(_smoothedEnergy, energy);
        _smoothedLow = Smooth(_smoothedLow, low);
        _smoothedMid = Smooth(_smoothedMid, mid);
        _smoothedHigh = Smooth(_smoothedHigh, high);

        Publish(new AudioSpectrumSnapshot(
            _smoothedEnergy,
            _smoothedLow,
            _smoothedMid,
            _smoothedHigh,
            Stopwatch.GetTimestamp()));
    }

    private double MeasureBandPower(double lowerHz, double upperHz)
    {
        var firstBin = Math.Max(1, (int)Math.Ceiling(lowerHz * FftSize / SampleRate));
        var lastBin = Math.Min((FftSize / 2) - 1, (int)Math.Floor(upperHz * FftSize / SampleRate));
        double power = 0;

        for (var bin = firstBin; bin <= lastBin; bin++)
        {
            var real = _fftReal[bin];
            var imaginary = _fftImaginary[bin];
            power += (real * real) + (imaginary * imaginary);
        }

        return power;
    }

    private static double NormalizeLevel(double value) =>
        Math.Clamp(Math.Log10(1 + (LevelCompression * Math.Max(0, value))) / Math.Log10(1 + LevelCompression), 0, 1);

    private static double Smooth(double current, double target)
    {
        var seconds = target > current ? AttackSeconds : ReleaseSeconds;
        var blend = 1 - Math.Exp(-(HopSize / (double)SampleRate) / seconds);
        return current + ((target - current) * blend);
    }

    private static void TransformInPlace(double[] real, double[] imaginary)
    {
        var length = real.Length;
        for (int source = 1, destination = 0; source < length; source++)
        {
            var bit = length >> 1;
            while ((destination & bit) != 0)
            {
                destination ^= bit;
                bit >>= 1;
            }

            destination ^= bit;
            if (source < destination)
            {
                (real[source], real[destination]) = (real[destination], real[source]);
                (imaginary[source], imaginary[destination]) = (imaginary[destination], imaginary[source]);
            }
        }

        for (var blockSize = 2; blockSize <= length; blockSize <<= 1)
        {
            var angle = -2 * Math.PI / blockSize;
            var phaseStepReal = Math.Cos(angle);
            var phaseStepImaginary = Math.Sin(angle);
            var halfBlock = blockSize >> 1;

            for (var block = 0; block < length; block += blockSize)
            {
                double phaseReal = 1;
                double phaseImaginary = 0;
                for (var offset = 0; offset < halfBlock; offset++)
                {
                    var even = block + offset;
                    var odd = even + halfBlock;
                    var oddReal = (phaseReal * real[odd]) - (phaseImaginary * imaginary[odd]);
                    var oddImaginary = (phaseReal * imaginary[odd]) + (phaseImaginary * real[odd]);

                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;

                    var nextPhaseReal = (phaseReal * phaseStepReal) - (phaseImaginary * phaseStepImaginary);
                    phaseImaginary = (phaseReal * phaseStepImaginary) + (phaseImaginary * phaseStepReal);
                    phaseReal = nextPhaseReal;
                }
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        var shouldRecover = false;
        lock (_stateGate)
        {
            if (!ReferenceEquals(sender, _recorder))
            {
                return;
            }

            Volatile.Write(ref _isCapturing, 0);
            Publish(default);
            shouldRecover = IsCaptureDesired;
        }

        if (args.Exception is not null)
        {
            Debug.WriteLine($"System-output audio capture stopped: {args.Exception}");
        }

        if (shouldRecover)
        {
            ScheduleRecovery();
        }
    }

    private async Task<bool> TryStartRecorderAsync(CancellationToken cancellationToken)
    {
        var previousRecorder = TakeRecorder();
        await ReleaseRecorderAsync(previousRecorder).ConfigureAwait(false);
        ResetAnalysis();
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsCaptureDesired || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        WasapiRecorder? recorder = null;
        try
        {
            recorder = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .WithEventSync()
                .WithBufferLength(20)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, ChannelCount))
                .WithMmcssThreadPriority("Audio")
                .Build();

            cancellationToken.ThrowIfCancellationRequested();
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;
            lock (_stateGate)
            {
                if (!IsCaptureDesired || Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                _recorder = recorder;
                Volatile.Write(ref _isCapturing, 1);
            }

            recorder.StartRecording();
            return IsCapturing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearRecorderIfCurrent(recorder);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ClearRecorderIfCurrent(recorder);
            Publish(default);
            Debug.WriteLine($"System-output audio capture could not start: {exception}");
            return false;
        }
        finally
        {
            if (recorder is not null && !ReferenceEquals(recorder, GetRecorder()))
            {
                await ReleaseRecorderAsync(recorder).ConfigureAwait(false);
            }
        }
    }

    private void ScheduleRecovery()
    {
        CancellationTokenSource recoveryCancellation;
        lock (_stateGate)
        {
            if (!IsCaptureDesired || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_recoveryCancellation is { IsCancellationRequested: false })
            {
                _recoveryRequested = true;
                return;
            }

            recoveryCancellation = new CancellationTokenSource();
            _recoveryCancellation = recoveryCancellation;
            _recoveryRequested = false;
        }

        _ = RecoverAsync(recoveryCancellation);
    }

    private async Task RecoverAsync(CancellationTokenSource recoveryCancellation)
    {
        try
        {
            for (var attempt = 0; attempt < MaximumRecoveryAttempts; attempt++)
            {
                if (!TryConsumeRecoveryAttempt())
                {
                    break;
                }

                await Task.Delay(RecoveryDelays[attempt], recoveryCancellation.Token).ConfigureAwait(false);
                await _lifecycleGate.WaitAsync(recoveryCancellation.Token).ConfigureAwait(false);
                try
                {
                    if (!IsCaptureDesired || Volatile.Read(ref _disposed) != 0 || IsCapturing)
                    {
                        return;
                    }

                    if (await TryStartRecorderAsync(recoveryCancellation.Token).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                finally
                {
                    _lifecycleGate.Release();
                }
            }

            Debug.WriteLine($"System-output audio capture recovery stopped after {MaximumRecoveryAttempts} attempts.");
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"System-output audio capture recovery failed: {exception}");
        }
        finally
        {
            var shouldReschedule = false;
            lock (_stateGate)
            {
                if (ReferenceEquals(_recoveryCancellation, recoveryCancellation))
                {
                    _recoveryCancellation = null;
                    shouldReschedule = _recoveryRequested
                        && IsCaptureDesired
                        && !IsCapturing
                        && Volatile.Read(ref _remainingRecoveryAttempts) > 0;
                    _recoveryRequested = false;
                }
            }

            recoveryCancellation.Dispose();

            if (shouldReschedule)
            {
                ScheduleRecovery();
            }
        }
    }

    private void CancelRecovery()
    {
        CancellationTokenSource? recoveryCancellation;
        lock (_stateGate)
        {
            recoveryCancellation = _recoveryCancellation;
        }

        try
        {
            recoveryCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The recovery task completed between observing and cancelling its token source.
        }
    }

    private bool TryConsumeRecoveryAttempt()
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _remainingRecoveryAttempts);
            if (remaining <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _remainingRecoveryAttempts, remaining - 1, remaining) == remaining)
            {
                return true;
            }
        }
    }

    private async ValueTask ReleaseRecorderAsync(WasapiRecorder? recorder)
    {
        if (recorder is null)
        {
            return;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        try
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"System-output audio capture cleanup failed: {exception}");
        }
    }

    private WasapiRecorder? TakeRecorder()
    {
        lock (_stateGate)
        {
            var recorder = _recorder;
            _recorder = null;
            Volatile.Write(ref _isCapturing, 0);
            return recorder;
        }
    }

    private WasapiRecorder? GetRecorder()
    {
        lock (_stateGate)
        {
            return _recorder;
        }
    }

    private void ClearRecorderIfCurrent(WasapiRecorder? recorder)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_recorder, recorder))
            {
                return;
            }

            _recorder = null;
            Volatile.Write(ref _isCapturing, 0);
        }
    }

    private void ResetAnalysis()
    {
        Array.Clear(_ring);
        Array.Clear(_fftReal);
        Array.Clear(_fftImaginary);
        _ringWriteIndex = 0;
        _samplesBuffered = 0;
        _samplesSinceAnalysis = 0;
        _smoothedEnergy = 0;
        _smoothedLow = 0;
        _smoothedMid = 0;
        _smoothedHigh = 0;
        Publish(default);
    }

    private void Publish(AudioSpectrumSnapshot snapshot)
    {
        lock (_snapshotWriteGate)
        {
            Interlocked.Increment(ref _snapshotVersion);
            Volatile.Write(ref _energyBits, BitConverter.DoubleToInt64Bits(snapshot.Energy));
            Volatile.Write(ref _lowBits, BitConverter.DoubleToInt64Bits(snapshot.LowBand));
            Volatile.Write(ref _midBits, BitConverter.DoubleToInt64Bits(snapshot.MidBand));
            Volatile.Write(ref _highBits, BitConverter.DoubleToInt64Bits(snapshot.HighBand));
            Volatile.Write(ref _timestamp, snapshot.Timestamp);
            Interlocked.Increment(ref _snapshotVersion);
        }
    }

    private bool IsCaptureDesired => Volatile.Read(ref _captureDesired) != 0;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

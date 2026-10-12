using Pulse.Core.Collectors;

namespace Pulse.Core;

/// <summary>
/// Owns the collectors and polls them on a background task. Each collector runs in its own
/// <see cref="CollectorSlot{T}"/> so a failure, warm-up or backoff in one never affects the others.
/// While not visible it polls at most every <see cref="BackgroundInterval"/> and skips the process scan,
/// unless <see cref="ProcessScanInBackground"/> is set (needed by alerts for incident context).
/// </summary>
public sealed class MetricsEngine : IDisposable
{
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ProcessScanInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BackgroundProcessScanInterval = TimeSpan.FromSeconds(30);

    private readonly IClock _clock;
    private readonly CpuCollector _cpu = new();
    private readonly MemoryCollector _memory = new();
    private readonly DriveCollector _drives = new();
    private readonly DiskIoCollector _diskIo;
    private readonly NetworkCollector _network = new();
    private readonly ProcessCollector _processes;
    private readonly HardwareTelemetryCollector _hardware = new();

    private readonly CollectorSlot<CpuReading> _cpuSlot;
    private readonly CollectorSlot<MemoryReading> _memorySlot;
    private readonly CollectorSlot<IReadOnlyList<DriveSample>> _driveSlot;
    private readonly CollectorSlot<DiskIoSample> _diskSlot;
    private readonly CollectorSlot<NetworkSample> _networkSlot;
    private readonly CollectorSlot<ProcessObservation> _processSlot;
    private readonly CollectorSlot<ThermalReadings> _thermalSlot;
    private readonly CollectorSlot<GpuActivity> _gpuSlot;
    private readonly CollectorSlot<BatterySample> _batterySlot;
    private readonly ICollectorSlot[] _slots;

    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile bool _visible = true;
    private volatile bool _paused;
    private volatile bool _processScanInBackground;
    private long _intervalTicks = TimeSpan.FromSeconds(2).Ticks;
    private volatile bool _resetRequested;
    private long _epoch;
    private string _resetReason = "reset";
    private bool _disposed;

    public MetricsEngine(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _diskIo = new DiskIoCollector(_clock);
        _processes = new ProcessCollector(_clock);
        var d = Diagnostics;
        var step = TimeSpan.FromSeconds(2);
        _cpuSlot = new("cpu", () => _cpu.Sample() is { } v ? CollectResult<CpuReading>.Ok(new(v)) : CollectResult<CpuReading>.Warming(), _clock, step, d);
        _memorySlot = new("memory", () =>
        {
            var m = _memory.Sample(out var counts);
            return m is null ? CollectResult<MemoryReading>.Failed("GlobalMemoryStatusEx failed") : CollectResult<MemoryReading>.Ok(new(m, counts));
        }, _clock, step, d);
        _driveSlot = new("drives", () => CollectResult<IReadOnlyList<DriveSample>>.Ok(_drives.Sample()), _clock, step, d);
        _diskSlot = new("disk-io", () => _diskIo.Sample(_driveSlot.Value ?? []), _clock, step, d);
        _networkSlot = new("network", _network.Sample, _clock, step, d);
        _processSlot = new("processes", () => _processes.Sample() is { } o ? CollectResult<ProcessObservation>.Ok(o) : CollectResult<ProcessObservation>.Failed("Process list unavailable"), _clock, ProcessScanInterval, d);
        _thermalSlot = new("thermal", _hardware.SampleThermal, _clock, ProcessScanInterval, d);
        _gpuSlot = new("gpu", _hardware.SampleGpu, _clock, ProcessScanInterval, d);
        _batterySlot = new("battery", () => SystemInfo.Battery() is { } b ? CollectResult<BatterySample>.Ok(b) : CollectResult<BatterySample>.Unsupported("No battery present"), _clock, BackgroundInterval, d);
        _slots = [_cpuSlot, _memorySlot, _driveSlot, _diskSlot, _networkSlot, _processSlot, _thermalSlot, _gpuSlot, _batterySlot];
    }

    public DiagnosticsLog Diagnostics { get; } = new();

    public event Action<MetricsSnapshot>? SnapshotReady;

    public TimeSpan Interval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));
        set { Interlocked.Exchange(ref _intervalTicks, value.Ticks); Wake(); }
    }

    public bool Visible
    {
        get => _visible;
        set { if (_visible != value) { _visible = value; Wake(); } }
    }

    /// <summary>While paused nothing is polled. Resuming resets rate baselines so no rate spans the pause.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (!value) ResetBaselines("resumed from pause");
            else Wake();
        }
    }

    public bool ProcessScanInBackground
    {
        get => _processScanInBackground;
        set { _processScanInBackground = value; Wake(); }
    }

    /// <summary>The most recent process observation, even if old; pair with <see cref="LastProcessObservationUtc"/>.</summary>
    public ProcessObservation? LastProcessObservation => _processSlot.LastKnown;
    public DateTime? LastProcessObservationUtc => _processSlot.LastSuccessUtc;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Call after sleep/resume or network changes so no rate is computed across the gap.</summary>
    public void ResetBaselines(string reason = "reset")
    {
        _resetReason = reason;
        _resetRequested = true;
        Wake();
    }

    private void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            if (!_paused)
            {
                try
                {
                    if (_resetRequested)
                    {
                        _resetRequested = false;
                        ResetCollectors();
                        first = true;
                    }
                    SnapshotReady?.Invoke(Collect());
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    Diagnostics.Record(_clock.UtcNow, "engine", CollectorState.Failing, e.GetType().Name + ": " + e.Message);
                }
            }

            var delay = _paused ? Timeout.InfiniteTimeSpan
                : first ? TimeSpan.FromMilliseconds(700)
                : EffectiveInterval;
            first = false;
            try { await _wake.WaitAsync(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Actual polling cadence: the chosen interval while visible, at least <see cref="BackgroundInterval"/> otherwise.</summary>
    public TimeSpan EffectiveInterval => _visible ? Interval : (Interval > BackgroundInterval ? Interval : BackgroundInterval);

    private void ResetCollectors()
    {
        Interlocked.Increment(ref _epoch);
        _cpu.Reset();
        _diskIo.Reset();
        _network.Reset();
        _processes.Reset();
        foreach (var slot in _slots) slot.Reset();
        Diagnostics.Record(_clock.UtcNow, "engine", null, "Baselines reset: " + _resetReason);
    }

    private static void Pace<T>(CollectorSlot<T> slot, TimeSpan interval, TimeSpan minimum) where T : class
    {
        slot.Interval = interval > minimum ? interval : minimum;
        var stale = TimeSpan.FromTicks(slot.Interval.Ticks * 3);
        slot.StaleAfter = stale > TimeSpan.FromSeconds(15) ? stale : TimeSpan.FromSeconds(15);
    }

    private void ApplySchedule()
    {
        var interval = EffectiveInterval;
        Pace(_cpuSlot, interval, TimeSpan.Zero);
        Pace(_memorySlot, interval, TimeSpan.Zero);
        Pace(_networkSlot, interval, TimeSpan.Zero);
        Pace(_diskSlot, interval, TimeSpan.Zero);
        Pace(_driveSlot, interval, TimeSpan.FromSeconds(10));
        Pace(_batterySlot, interval, BackgroundInterval);
        Pace(_thermalSlot, interval, ProcessScanInterval);
        Pace(_gpuSlot, interval, ProcessScanInterval);
        Pace(_processSlot, interval, _visible ? ProcessScanInterval : BackgroundProcessScanInterval);
        _processSlot.Enabled = _visible || _processScanInBackground;
        _thermalSlot.Enabled = _gpuSlot.Enabled = _visible;
    }

    private MetricsSnapshot Collect()
    {
        ApplySchedule();
        foreach (var slot in _slots) slot.Run();

        var now = _clock.UtcNow;
        var memory = _memorySlot.Value;
        var statuses = _slots.Select(s => s.GetStatus(now)).ToList();
        return new MetricsSnapshot(
            now.ToLocalTime(), _cpuSlot.Value?.Percent, memory?.Memory, _driveSlot.Value ?? [], _diskSlot.Value,
            _networkSlot.Value, SystemInfo.Uptime, memory?.Counts, _batterySlot.Value, _processSlot.Value?.ToTop(),
            _thermalSlot.Value, _gpuSlot.Value, statuses, Interlocked.Read(ref _epoch));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _diskIo.Dispose();
        _wake.Dispose();
        _cts.Dispose();
    }
}

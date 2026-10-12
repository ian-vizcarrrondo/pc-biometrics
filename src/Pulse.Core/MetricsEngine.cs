using Pulse.Core.Collectors;

namespace Pulse.Core;

/// <summary>
/// Owns the collectors and polls them on a background task. While not visible it polls at most every
/// <see cref="BackgroundInterval"/> and skips the process scan.
/// </summary>
public sealed class MetricsEngine : IDisposable
{
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessScanInterval = TimeSpan.FromSeconds(5);

    private readonly CpuCollector _cpu = new();
    private readonly MemoryCollector _memory = new();
    private readonly DriveCollector _drives = new();
    private readonly DiskIoCollector _diskIo = new();
    private readonly NetworkCollector _network = new();
    private readonly ProcessCollector _processes = new();
    private readonly HardwareTelemetryCollector _hardware = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile bool _visible = true;
    private long _intervalTicks = TimeSpan.FromSeconds(2).Ticks;
    private volatile bool _resetRequested;
    private DateTime _lastProcessScan = DateTime.MinValue;
    private TopProcesses? _lastTop;

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

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Call after sleep/resume or network changes so no rate is computed across the gap.</summary>
    public void ResetBaselines() { _resetRequested = true; Wake(); }

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
            try
            {
                if (_resetRequested) { _resetRequested = false; ResetCollectors(); first = true; }
                SnapshotReady?.Invoke(Collect());
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }

            var delay = first ? TimeSpan.FromMilliseconds(700)
                : _visible ? Interval : (Interval > BackgroundInterval ? Interval : BackgroundInterval);
            first = false;
            try { await _wake.WaitAsync(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ResetCollectors()
    {
        _cpu.Reset();
        _diskIo.Reset();
        _network.Reset();
        _processes.Reset();
        _lastProcessScan = DateTime.MinValue;
    }

    private MetricsSnapshot Collect()
    {
        var now = DateTime.Now;
        var cpu = Safe(_cpu.Sample);
        SystemCounts? counts = null;
        var memory = Safe(() => _memory.Sample(out counts));
        var drives = Safe(_drives.Sample) ?? [];
        var diskIo = Safe(() => _diskIo.Sample(drives));
        var net = Safe(_network.Sample);

        if (_visible && now - _lastProcessScan >= ProcessScanInterval)
        {
            _lastTop = Safe(_processes.Sample);
            _lastProcessScan = now;
        }
        else if (!_visible)
        {
            _lastTop = null;
            _processes.Reset();
            _lastProcessScan = DateTime.MinValue;
        }

        return new MetricsSnapshot(now, cpu, memory, drives, diskIo, net, SystemInfo.Uptime, counts,
            Safe(SystemInfo.Battery), _lastTop, Safe(_hardware.Sample));
    }

    private static T? Safe<T>(Func<T?> f)
    {
        try { return f(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return default; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _diskIo.Dispose();
        _wake.Dispose();
        _cts.Dispose();
    }
}

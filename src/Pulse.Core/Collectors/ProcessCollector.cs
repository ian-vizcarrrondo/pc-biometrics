using System.ComponentModel;
using System.Diagnostics;

namespace Pulse.Core.Collectors;

/// <summary>Per-process CPU baselines keyed by PID plus creation time, so PID reuse can never inherit another process's baseline.</summary>
public sealed class ProcessCpuTracker
{
    private Dictionary<ProcessKey, TimeSpan> _previous = new();

    public void Reset() => _previous = new();

    /// <summary>Returns the percentage of total CPU capacity since the previous observation, or null when no valid baseline exists.</summary>
    public double? Observe(ProcessKey key, TimeSpan cpuTime, double elapsedSeconds, int cores, Dictionary<ProcessKey, TimeSpan> next)
    {
        next[key] = cpuTime;
        return elapsedSeconds > 0 && _previous.TryGetValue(key, out var before)
            ? RateMath.ProcessCpuPercent(before, cpuTime, elapsedSeconds, cores)
            : null;
    }

    public void Commit(Dictionary<ProcessKey, TimeSpan> next) => _previous = next;
}

public sealed class ProcessCollector
{
    private readonly IClock _clock;
    private readonly ProcessCpuTracker _cpu = new();
    private readonly Queue<(DateTime TimeUtc, bool Started)> _churn = new();
    private HashSet<ProcessKey>? _previousKeys;
    private long _lastTicks;

    public ProcessCollector(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    public void Reset()
    {
        _cpu.Reset();
        _previousKeys = null;
        _churn.Clear();
        _lastTicks = 0;
    }

    /// <summary>One scan of running processes. Processes that exit mid-scan or deny access are tolerated, not fatal.</summary>
    public ProcessObservation? Sample()
    {
        var ticks = Stopwatch.GetTimestamp();
        var elapsed = _lastTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTicks, ticks).TotalSeconds;
        var cores = Environment.ProcessorCount;
        var items = new List<ProcessItem>();
        var nextCpu = new Dictionary<ProcessKey, TimeSpan>();
        var keys = new HashSet<ProcessKey>();

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return null; }

        foreach (var p in all)
        {
            try
            {
                if (p.Id is 0 or 4) continue;
                string name;
                try { name = p.ProcessName; }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { continue; }

                long start = 0;
                var limited = false;
                try { start = p.StartTime.ToUniversalTime().Ticks; }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { limited = true; }

                var key = new ProcessKey(p.Id, start);
                keys.Add(key);

                double? pct = null;
                if (!limited)
                {
                    try { pct = _cpu.Observe(key, p.TotalProcessorTime, elapsed, cores, nextCpu); }
                    catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { limited = true; }
                }

                ulong? mem = null;
                try { mem = (ulong)p.WorkingSet64; }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { limited = true; }

                items.Add(new ProcessItem(key, name, pct, mem, limited));
            }
            finally { p.Dispose(); }
        }

        _cpu.Commit(nextCpu);
        _lastTicks = ticks;
        var now = _clock.UtcNow;

        if (_previousKeys is not null)
        {
            foreach (var _ in keys.Except(_previousKeys)) _churn.Enqueue((now, true));
            foreach (var _ in _previousKeys.Except(keys)) _churn.Enqueue((now, false));
        }
        _previousKeys = keys;

        var cutoff = now.AddMinutes(-1);
        while (_churn.TryPeek(out var change) && change.TimeUtc < cutoff) _churn.Dequeue();

        return new ProcessObservation(now, items, _churn.Count(e => e.Started), _churn.Count(e => !e.Started), elapsed > 0);
    }
}

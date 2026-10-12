using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Pulse.Core;

public enum CollectorState { WarmingUp, Current, Stale, Unsupported, AccessDenied, Failing }

public enum CollectOutcome { Ok, WarmingUp, Unsupported, AccessDenied, Failed }

public readonly record struct CollectResult<T>(CollectOutcome Outcome, T? Value = default, string? Detail = null)
{
    public static CollectResult<T> Ok(T value) => new(CollectOutcome.Ok, value);
    public static CollectResult<T> Warming() => new(CollectOutcome.WarmingUp);
    public static CollectResult<T> Unsupported(string detail) => new(CollectOutcome.Unsupported, default, detail);
    public static CollectResult<T> Denied(string detail) => new(CollectOutcome.AccessDenied, default, detail);
    public static CollectResult<T> Failed(string detail) => new(CollectOutcome.Failed, default, detail);
}

/// <param name="Interval">Nominal time between fresh samples for this collector.</param>
public sealed record CollectorStatus(
    string Name,
    CollectorState State,
    DateTime? LastSuccessUtc,
    DateTime? LastAttemptUtc,
    string? Detail,
    int ConsecutiveFailures,
    TimeSpan Interval)
{
    public TimeSpan? Age(DateTime nowUtc) => LastSuccessUtc is { } t ? nowUtc - t : null;
}

public interface ICollectorSlot
{
    string Name { get; }
    CollectorStatus GetStatus(DateTime nowUtc);
    bool Run();
    void Reset();
}

public static class CollectorNames
{
    public const string Cpu = "cpu", Memory = "memory", Drives = "drives", DiskIo = "disk-io", Network = "network",
        Processes = "processes", Thermal = "thermal", Gpu = "gpu", Battery = "battery";
}

/// <summary>
/// Runs one collector on its own schedule. A failure never affects other slots, a failed or warming
/// reading is never kept as a value, and retries back off so a broken source is not hammered.
/// </summary>
public sealed class CollectorSlot<T> : ICollectorSlot where T : class
{
    private static readonly TimeSpan WarmRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnsupportedRetry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxDeniedBackoff = TimeSpan.FromMinutes(5);

    private readonly Func<CollectResult<T>> _collect;
    private readonly IClock _clock;
    private readonly DiagnosticsLog? _log;
    private T? _value, _lastGood;
    private DateTime? _lastSuccess, _lastAttempt, _nextDue;
    private CollectorState _state = CollectorState.WarmingUp;
    private string? _detail;
    private int _failures;

    public CollectorSlot(string name, Func<CollectResult<T>> collect, IClock clock, TimeSpan interval, DiagnosticsLog? log = null)
    {
        Name = name;
        _collect = collect;
        _clock = clock;
        Interval = interval;
        StaleAfter = TimeSpan.FromSeconds(15);
        _log = log;
    }

    public string Name { get; }
    public TimeSpan Interval { get; set; }
    public TimeSpan StaleAfter { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>The last good reading, or null when warming up, failing, unsupported, or older than <see cref="StaleAfter"/>.</summary>
    public T? Value => _state == CollectorState.Current && IsFresh(_clock.UtcNow) ? _value : null;

    public DateTime? LastSuccessUtc => _lastSuccess;

    /// <summary>Most recent good reading regardless of age; callers must show <see cref="LastSuccessUtc"/> alongside it.</summary>
    public T? LastKnown => _lastGood;

    private bool IsFresh(DateTime now) => _lastSuccess is { } t && now - t <= StaleAfter;

    /// <summary>Collects if scheduled. Returns true when the collector actually ran.</summary>
    public bool Run()
    {
        var now = _clock.UtcNow;
        // Tolerate wake-up jitter so a collector on the loop's own cadence is not skipped by a few milliseconds.
        var tolerance = TimeSpan.FromTicks(Interval.Ticks / 5);
        if (!Enabled || (_nextDue is { } due && now < due - tolerance)) return false;

        CollectResult<T> result;
        try { result = _collect(); }
        catch (Exception e) when (e is not OutOfMemoryException) { result = Classify(e); }

        _lastAttempt = now;
        var previousState = _state;
        var previousDetail = _detail;
        switch (result.Outcome)
        {
            case CollectOutcome.Ok when result.Value is not null:
                _value = _lastGood = result.Value; _lastSuccess = now; _state = CollectorState.Current;
                _failures = 0; _detail = null; _nextDue = now + Interval;
                break;
            case CollectOutcome.Ok:
            case CollectOutcome.WarmingUp:
                _value = null; _state = CollectorState.WarmingUp; _detail = result.Detail;
                _nextDue = now + (Interval < WarmRetry ? Interval : WarmRetry);
                break;
            case CollectOutcome.Unsupported:
                _value = null; _state = CollectorState.Unsupported; _detail = result.Detail;
                _failures++; _nextDue = now + UnsupportedRetry;
                break;
            case CollectOutcome.AccessDenied:
                _value = null; _state = CollectorState.AccessDenied; _detail = result.Detail;
                _failures++; _nextDue = now + Backoff(TimeSpan.FromSeconds(30), MaxDeniedBackoff);
                break;
            default:
                _value = null; _state = CollectorState.Failing; _detail = result.Detail;
                _failures++; _nextDue = now + Backoff(TimeSpan.FromSeconds(Math.Max(1, Interval.TotalSeconds)), MaxBackoff);
                break;
        }

        if (_log is not null && (_state != previousState || _detail != previousDetail)
            && _state is not (CollectorState.Current or CollectorState.WarmingUp))
            _log.Record(now, Name, _state, _detail);
        else if (_log is not null && previousState is not (CollectorState.Current or CollectorState.WarmingUp)
            && _state == CollectorState.Current)
            _log.Record(now, Name, CollectorState.Current, "Recovered");
        return true;
    }

    /// <summary>Forgets the value and schedule so rate-based collectors warm up again (sleep/resume, network change).</summary>
    public void Reset()
    {
        _value = null;
        _state = CollectorState.WarmingUp;
        _detail = null;
        _nextDue = null;
    }

    public CollectorStatus GetStatus(DateTime nowUtc)
    {
        var state = _state == CollectorState.Current && !IsFresh(nowUtc) ? CollectorState.Stale : _state;
        return new CollectorStatus(Name, state, _lastSuccess, _lastAttempt, _detail, _failures, Interval);
    }

    private TimeSpan Backoff(TimeSpan baseDelay, TimeSpan max)
    {
        var factor = Math.Pow(2, Math.Min(_failures - 1, 10));
        var seconds = Math.Min(max.TotalSeconds, baseDelay.TotalSeconds * factor);
        return TimeSpan.FromSeconds(seconds);
    }

    public static CollectResult<T> Classify(Exception e) => e switch
    {
        UnauthorizedAccessException => CollectResult<T>.Denied("Access denied"),
        Win32Exception { NativeErrorCode: 5 } => CollectResult<T>.Denied("Access denied"),
        COMException { HResult: unchecked((int)0x80041003) or unchecked((int)0x80070005) } =>
            CollectResult<T>.Denied("Windows denied access to this sensor"),
        PlatformNotSupportedException or NotSupportedException => CollectResult<T>.Unsupported("Not supported on this system"),
        _ => CollectResult<T>.Failed($"{e.GetType().Name}: {e.Message}"),
    };
}

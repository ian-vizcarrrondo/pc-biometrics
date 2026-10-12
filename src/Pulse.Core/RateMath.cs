namespace Pulse.Core;

public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

/// <summary>Pure calculations shared by collectors. Null means "no valid value for this interval".</summary>
public static class RateMath
{
    /// <summary>Bytes per second between two cumulative counters. Null when the counter reset or time did not advance.</summary>
    public static double? PerSecond(ulong previous, ulong current, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0 || current < previous) return null;
        return (current - previous) / elapsedSeconds;
    }

    /// <summary>CPU busy percentage from GetSystemTimes-style counters (kernel time includes idle time).</summary>
    public static double? CpuPercent(CpuTimes previous, CpuTimes current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) return null;
        var idle = current.Idle - previous.Idle;
        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (total == 0 || idle > total) return null;
        return Clamp((total - idle) * 100.0 / total);
    }

    /// <summary>Disk busy percentage from idle-time (100 ns units) and wall-clock deltas (100 ns units).</summary>
    public static double? DiskActivePercent(ulong previousIdle, ulong currentIdle, ulong previousQuery, ulong currentQuery)
    {
        if (currentIdle < previousIdle || currentQuery <= previousQuery) return null;
        var idle = (double)(currentIdle - previousIdle);
        var elapsed = (double)(currentQuery - previousQuery);
        return Clamp(100.0 - idle * 100.0 / elapsed);
    }

    /// <summary>Per-process CPU share, normalized to all logical processors like Task Manager.</summary>
    public static double? ProcessCpuPercent(TimeSpan previous, TimeSpan current, double elapsedSeconds, int logicalProcessors)
    {
        if (elapsedSeconds <= 0 || logicalProcessors < 1 || current < previous) return null;
        return Clamp((current - previous).TotalSeconds / (elapsedSeconds * logicalProcessors) * 100.0);
    }

    public static double Percent(double part, double whole) => whole <= 0 ? 0 : Clamp(part * 100.0 / whole);

    private static double Clamp(double v) => v < 0 ? 0 : v > 100 ? 100 : v;
}

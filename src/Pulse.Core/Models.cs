namespace Pulse.Core;

public sealed record MemorySample(
    ulong TotalBytes,
    ulong UsedBytes,
    ulong CommitUsedBytes,
    ulong CommitLimitBytes,
    ulong CachedBytes,
    ulong PagedPoolBytes = 0,
    ulong NonPagedPoolBytes = 0)
{
    public double UsedPercent => RateMath.Percent(UsedBytes, TotalBytes);
    public double CommitPercent => RateMath.Percent(CommitUsedBytes, CommitLimitBytes);
}

public sealed record DriveSample(string Name, string Label, ulong TotalBytes, ulong UsedBytes)
{
    public ulong FreeBytes => TotalBytes - UsedBytes;
    public double UsedPercent => RateMath.Percent(UsedBytes, TotalBytes);
}

public sealed record DiskIoSample(double ReadBytesPerSecond, double WriteBytesPerSecond, double ActivePercent, uint QueueDepth = 0);

public sealed record AdapterSample(string Name, double DownBytesPerSecond, double UpBytesPerSecond, long LinkSpeedBitsPerSecond);

public sealed record NetworkSample(IReadOnlyList<AdapterSample> Adapters, double DownBytesPerSecond, double UpBytesPerSecond, ulong SessionDownBytes, ulong SessionUpBytes);

public sealed record SystemCounts(int Processes, int Threads, int Handles);

public sealed record BatterySample(int? Percent, bool OnAc, bool Charging, TimeSpan? Remaining);

public sealed record ProcessUsage(string Name, int Instances, double CpuPercent, ulong WorkingSetBytes);

public sealed record TopProcesses(
    IReadOnlyList<ProcessUsage> ByCpu,
    IReadOnlyList<ProcessUsage> ByMemory,
    int StartedLastMinute = 0,
    int ExitedLastMinute = 0);

public sealed record ThermalReading(string Name, double Celsius);

public sealed record GpuActivity(string Engine, double UtilizationPercent, int ActiveEngines);

public sealed record HardwareSample(
    IReadOnlyList<ThermalReading> ThermalReadings,
    GpuActivity? Gpu,
    string? ThermalError = null,
    string? GpuError = null);

/// <summary>One refresh of every metric. A null member means that metric was unavailable or has no valid rate yet.</summary>
public sealed record MetricsSnapshot(
    DateTime Timestamp,
    double? CpuPercent,
    MemorySample? Memory,
    IReadOnlyList<DriveSample> Drives,
    DiskIoSample? DiskIo,
    NetworkSample? Network,
    TimeSpan Uptime,
    SystemCounts? Counts,
    BatterySample? Battery,
    TopProcesses? Top,
    HardwareSample? Hardware = null);

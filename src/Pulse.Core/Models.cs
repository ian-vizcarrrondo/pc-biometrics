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

/// <summary>A process is identified by PID plus creation time, so a reused PID is a different process.</summary>
public readonly record struct ProcessKey(int Pid, long StartTicks);

/// <param name="CpuPercent">Share of total CPU capacity (Task Manager style), or null when not yet measurable or access was denied.</param>
/// <param name="WorkingSetBytes">Null when Windows denied the read.</param>
public sealed record ProcessItem(ProcessKey Key, string Name, double? CpuPercent, ulong? WorkingSetBytes, bool AccessLimited);

public sealed record ProcessObservation(
    DateTime ObservedUtc,
    IReadOnlyList<ProcessItem> Items,
    int StartedLastMinute,
    int ExitedLastMinute,
    bool CpuValid)
{
    public IReadOnlyList<ProcessUsage> Groups()
    {
        return Items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUsage(g.Key, g.Count(), g.Sum(i => i.CpuPercent ?? 0), (ulong)g.Sum(i => (double)(i.WorkingSetBytes ?? 0))))
            .ToList();
    }

    public TopProcesses ToTop()
    {
        var usage = Groups();
        var byCpu = CpuValid ? usage.Where(u => u.CpuPercent >= 0.1).OrderByDescending(u => u.CpuPercent).Take(3).ToList() : [];
        var byMem = usage.OrderByDescending(u => u.WorkingSetBytes).Take(3).ToList();
        return new TopProcesses(byCpu, byMem, StartedLastMinute, ExitedLastMinute);
    }
}

public sealed record TopProcesses(
    IReadOnlyList<ProcessUsage> ByCpu,
    IReadOnlyList<ProcessUsage> ByMemory,
    int StartedLastMinute = 0,
    int ExitedLastMinute = 0);

public sealed record ThermalReading(string Name, double Celsius);

public sealed record ThermalReadings(IReadOnlyList<ThermalReading> Zones);

public sealed record CpuReading(double Percent);

public sealed record MemoryReading(MemorySample Memory, SystemCounts? Counts);

public sealed record GpuActivity(string Engine, double UtilizationPercent, int ActiveEngines);

/// <summary>One refresh of every metric. A null member means that metric was unavailable or has no valid rate yet;
/// <see cref="Statuses"/> says which, and how old each underlying reading is.</summary>
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
    ThermalReadings? Thermal = null,
    GpuActivity? Gpu = null,
    IReadOnlyList<CollectorStatus>? Statuses = null,
    long Epoch = 0)
{
    public DateTime TimestampUtc => Timestamp.ToUniversalTime();

    public CollectorStatus? StatusOf(string name) => Statuses?.FirstOrDefault(s => s.Name == name);
}


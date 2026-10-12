namespace Pulse.Core;

public enum SampleQuality : byte { Valid = 0, WarmingUp = 1, Stale = 2, Unavailable = 3 }

public enum MetricGroup { Core, Disk, Network, Gpu, Thermal }

/// <param name="Id">Stable identifier used in history, recordings and exports. Never localized or renumbered.</param>
public sealed record MetricDefinition(string Id, string Name, string Unit, MetricGroup Group, string CollectorName);

public readonly record struct MetricValue(string Id, double? Value, SampleQuality Quality);

public static class MetricIds
{
    public const string Cpu = "cpu.total", Memory = "memory.used", Commit = "memory.commit",
        DiskRead = "disk.read", DiskWrite = "disk.write", DiskActive = "disk.active",
        NetDown = "net.down", NetUp = "net.up", Gpu = "gpu.engine.peak", Thermal = "thermal.acpi.max";

    /// <summary>Stable per-volume id, e.g. <c>drive.C:.used</c>, derived from the drive letter.</summary>
    public static string DriveUsed(string driveName) => $"drive.{driveName}.used";
}

public static class MetricCatalog
{
    public static readonly IReadOnlyList<MetricDefinition> Fixed =
    [
        new(MetricIds.Cpu, "CPU", "%", MetricGroup.Core, CollectorNames.Cpu),
        new(MetricIds.Memory, "Memory in use", "%", MetricGroup.Core, CollectorNames.Memory),
        new(MetricIds.Commit, "Commit charge", "%", MetricGroup.Core, CollectorNames.Memory),
        new(MetricIds.Gpu, "GPU busiest engine", "%", MetricGroup.Gpu, CollectorNames.Gpu),
        new(MetricIds.Thermal, "ACPI thermal zone (highest)", "°C", MetricGroup.Thermal, CollectorNames.Thermal),
        new(MetricIds.DiskRead, "Disk read", "B/s", MetricGroup.Disk, CollectorNames.DiskIo),
        new(MetricIds.DiskWrite, "Disk write", "B/s", MetricGroup.Disk, CollectorNames.DiskIo),
        new(MetricIds.DiskActive, "Disk busiest volume", "%", MetricGroup.Disk, CollectorNames.DiskIo),
        new(MetricIds.NetDown, "Network download", "B/s", MetricGroup.Network, CollectorNames.Network),
        new(MetricIds.NetUp, "Network upload", "B/s", MetricGroup.Network, CollectorNames.Network),
    ];

    public static MetricDefinition? Find(string id) => Fixed.FirstOrDefault(m => m.Id == id)
        ?? (id.StartsWith("drive.", StringComparison.Ordinal) ? new(id, id[6..^5] + " used", "%", MetricGroup.Disk, CollectorNames.Drives) : null);

    /// <summary>
    /// Turns a snapshot into one value per metric. A reading that is missing keeps a null value and a quality
    /// explaining why; it is never converted to zero.
    /// </summary>
    public static IReadOnlyList<MetricValue> Extract(MetricsSnapshot s)
    {
        var list = new List<MetricValue>(Fixed.Count + s.Drives.Count);
        void Add(string id, double? value, string collector) => list.Add(new(id, value, QualityOf(s, collector, value)));

        Add(MetricIds.Cpu, s.CpuPercent, CollectorNames.Cpu);
        Add(MetricIds.Memory, s.Memory?.UsedPercent, CollectorNames.Memory);
        Add(MetricIds.Commit, s.Memory?.CommitPercent, CollectorNames.Memory);
        Add(MetricIds.Gpu, s.Gpu?.UtilizationPercent, CollectorNames.Gpu);
        Add(MetricIds.Thermal, s.Thermal is { Zones.Count: > 0 } t ? t.Zones.Max(z => z.Celsius) : null, CollectorNames.Thermal);
        Add(MetricIds.DiskRead, s.DiskIo?.ReadBytesPerSecond, CollectorNames.DiskIo);
        Add(MetricIds.DiskWrite, s.DiskIo?.WriteBytesPerSecond, CollectorNames.DiskIo);
        Add(MetricIds.DiskActive, s.DiskIo?.ActivePercent, CollectorNames.DiskIo);
        Add(MetricIds.NetDown, s.Network?.DownBytesPerSecond, CollectorNames.Network);
        Add(MetricIds.NetUp, s.Network?.UpBytesPerSecond, CollectorNames.Network);
        foreach (var d in s.Drives) Add(MetricIds.DriveUsed(d.Name), d.UsedPercent, CollectorNames.Drives);
        return list;
    }

    private static SampleQuality QualityOf(MetricsSnapshot s, string collector, double? value)
    {
        if (value is not null) return SampleQuality.Valid;
        return s.StatusOf(collector)?.State switch
        {
            CollectorState.WarmingUp or null => SampleQuality.WarmingUp,
            CollectorState.Stale => SampleQuality.Stale,
            _ => SampleQuality.Unavailable,
        };
    }
}

namespace Pulse.Core.Alerts;

public enum AlertState { Pending, Active, Acknowledged, Recovered }

/// <summary>
/// A sustained-threshold rule. All timing is by elapsed wall-clock time between consecutive valid readings.
/// A <see cref="MetricId"/> containing <c>*</c> (for example <c>drive.*.used</c>) is evaluated once per matching metric.
/// </summary>
public sealed record AlertRule(
    string Id,
    string Name,
    string MetricId,
    double Threshold,
    double ClearThreshold,
    TimeSpan Duration,
    TimeSpan RecoverHold,
    TimeSpan Cooldown,
    bool Enabled = true,
    string Unit = "%")
{
    public bool Matches(string metricId)
    {
        var star = MetricId.IndexOf('*');
        if (star < 0) return metricId == MetricId;
        var prefix = MetricId[..star];
        var suffix = MetricId[(star + 1)..];
        return metricId.Length >= prefix.Length + suffix.Length
            && metricId.StartsWith(prefix, StringComparison.Ordinal)
            && metricId.EndsWith(suffix, StringComparison.Ordinal);
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("Rule needs an id.");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Rule needs a name.");
        if (!double.IsFinite(Threshold) || !double.IsFinite(ClearThreshold)) errors.Add("Thresholds must be finite numbers.");
        else
        {
            if (Unit == "%" && (Threshold is < 1 or > 100)) errors.Add("Threshold must be between 1 and 100 percent.");
            if (ClearThreshold >= Threshold) errors.Add("Clear threshold must be lower than the alert threshold (hysteresis).");
            if (ClearThreshold < 0) errors.Add("Clear threshold cannot be negative.");
        }
        if (Duration < TimeSpan.FromSeconds(5) || Duration > TimeSpan.FromHours(1)) errors.Add("Duration must be between 5 seconds and 1 hour.");
        if (RecoverHold < TimeSpan.Zero || RecoverHold > TimeSpan.FromHours(1)) errors.Add("Recovery hold must be between 0 and 1 hour.");
        if (Cooldown < TimeSpan.Zero || Cooldown > TimeSpan.FromHours(24)) errors.Add("Cooldown must be between 0 and 24 hours.");
        return errors;
    }

    public static IReadOnlyList<AlertRule> Defaults() =>
    [
        new("cpu-high", "CPU sustained high", MetricIds.Cpu, 90, 75, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5)),
        new("memory-high", "Memory sustained high", MetricIds.Memory, 90, 80, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)),
        new("commit-high", "Commit charge high", MetricIds.Commit, 90, 80, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)),
        new("disk-busy", "Disk sustained busy", MetricIds.DiskActive, 95, 80, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5)),
        new("drive-full", "Drive low on free space", "drive.*.used", 95, 92, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromHours(1)),
    ];
}

public sealed record ContextMetric(string MetricId, string Name, string Unit, double? Min, double? Average, double? Max, double? Latest);

public sealed record ContextProcess(string Name, double? CpuPercent, ulong? WorkingSetBytes);

/// <summary>Surrounding telemetry and the latest process observation when an alert became active.</summary>
public sealed record IncidentContext(
    IReadOnlyList<ContextMetric> Telemetry,
    IReadOnlyList<ContextProcess> Processes,
    DateTime? ProcessObservedUtc,
    TimeSpan? ProcessAge);

public sealed class Incident
{
    public required string Id { get; init; }
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public required string MetricId { get; init; }
    public required double Threshold { get; init; }
    public required string Unit { get; init; }
    public AlertState State { get; internal set; }
    public DateTime PendingSinceUtc { get; init; }
    public DateTime? ActivatedUtc { get; internal set; }
    public DateTime? AcknowledgedUtc { get; internal set; }
    public DateTime? RecoveredUtc { get; internal set; }
    public double PeakValue { get; internal set; }
    public double LastValue { get; internal set; }
    public string Explanation { get; internal set; } = "";
    public IncidentContext? Context { get; internal set; }
}

public sealed record AlertEvent(DateTime TimeUtc, string IncidentId, AlertState State, string Message);

namespace Pulse.Core;

public enum HealthLevel { Good, Notice, Warning, Unknown }

public sealed record Health(HealthLevel Level, string Message);

/// <summary>
/// Plain threshold rules applied to real readings; there is no hidden score.
/// Thresholds: CPU &gt; 90%, memory &gt; 90%, commit &gt; 90%, disk active &gt; 90%, drive free &lt; 10%.
/// A missing CPU or memory reading never produces a calm verdict, and missing disk readings are called out.
/// </summary>
public static class HealthAssessment
{
    public static Health Evaluate(MetricsSnapshot s)
    {
        var issues = new List<string>();
        if (s.CpuPercent is > 90) issues.Add("CPU is saturated");
        if (s.Memory is { UsedPercent: > 90 }) issues.Add("physical memory is nearly full");
        else if (s.Memory is { CommitPercent: > 90 }) issues.Add("commit charge is near its limit");
        if (s.DiskIo is { ActivePercent: > 90 }) issues.Add("disk is saturated");
        foreach (var d in s.Drives)
            if (d.TotalBytes > 0 && d.FreeBytes * 10 < d.TotalBytes) issues.Add($"{d.Name} has under 10% free");

        var missing = new List<string>();
        if (s.CpuPercent is null) missing.Add(Describe(s, CollectorNames.Cpu, "CPU"));
        if (s.Memory is null) missing.Add(Describe(s, CollectorNames.Memory, "memory"));
        var coreMissing = missing.Count;
        if (s.DiskIo is null) missing.Add(Describe(s, CollectorNames.DiskIo, "disk activity"));
        if (s.Drives.Count == 0 && s.StatusOf(CollectorNames.Drives) is { State: not CollectorState.Current })
            missing.Add(Describe(s, CollectorNames.Drives, "drive space"));
        var gaps = missing.Count == 0 ? "" : " (" + string.Join(", ", missing) + ")";

        if (issues.Count > 0)
        {
            var text = string.Join(", ", issues);
            return new Health(HealthLevel.Warning, char.ToUpperInvariant(text[0]) + text[1..] + (gaps.Length > 0 ? "; partial data" + gaps : ""));
        }

        if (coreMissing > 0)
            return new Health(HealthLevel.Unknown, "Not enough data yet" + gaps);

        if (missing.Count > 0)
            return new Health(HealthLevel.Notice, "No problems in the readings available" + gaps);

        var busy = s.CpuPercent is > 70 || s.Memory is { UsedPercent: > 80 };
        return busy
            ? new Health(HealthLevel.Notice, "Under load, but within limits")
            : new Health(HealthLevel.Good, "All systems relaxed");
    }

    private static string Describe(MetricsSnapshot s, string name, string label) => s.StatusOf(name)?.State switch
    {
        CollectorState.WarmingUp or null => label + " warming up",
        CollectorState.Stale => label + " stale",
        CollectorState.AccessDenied => label + " access denied",
        CollectorState.Unsupported => label + " unsupported",
        _ => label + " unavailable",
    };
}

namespace Pulse.Core;

public enum HealthLevel { Good, Notice, Warning }

public sealed record Health(HealthLevel Level, string Message);

/// <summary>
/// Plain threshold rules applied to real readings; there is no hidden score.
/// Thresholds: CPU &gt; 90%, memory &gt; 90%, commit &gt; 90%, disk active &gt; 90%, drive free &lt; 10%.
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

        if (issues.Count == 0)
        {
            var busy = s.CpuPercent is > 70 || s.Memory is { UsedPercent: > 80 };
            return busy
                ? new Health(HealthLevel.Notice, "Under load, but within limits")
                : new Health(HealthLevel.Good, "All systems relaxed");
        }

        var text = string.Join(", ", issues);
        return new Health(HealthLevel.Warning, char.ToUpperInvariant(text[0]) + text[1..]);
    }
}

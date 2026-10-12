using Pulse.Core;

namespace Pulse.Tests;

public class RateMathTests
{
    [Fact]
    public void PerSecond_ComputesRate() =>
        Assert.Equal(500.0, RateMath.PerSecond(1_000, 2_000, 2.0));

    [Fact]
    public void PerSecond_CounterReset_ReturnsNull() =>
        Assert.Null(RateMath.PerSecond(5_000, 100, 1.0));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PerSecond_NoElapsedTime_ReturnsNull(double elapsed) =>
        Assert.Null(RateMath.PerSecond(0, 10, elapsed));

    [Fact]
    public void PerSecond_NoTraffic_IsZero() =>
        Assert.Equal(0.0, RateMath.PerSecond(42, 42, 2.0));

    [Fact]
    public void PerSecond_HandlesLargeCounters() =>
        Assert.Equal(1000.0, RateMath.PerSecond(ulong.MaxValue - 5000, ulong.MaxValue - 3000, 2.0));

    [Fact]
    public void CpuPercent_UsesKernelIncludingIdle()
    {
        var prev = new CpuTimes(100, 200, 100);
        var cur = new CpuTimes(150, 300, 200); // total 200, idle 50 -> 75% busy
        Assert.Equal(75.0, RateMath.CpuPercent(prev, cur));
    }

    [Fact]
    public void CpuPercent_Reset_ReturnsNull() =>
        Assert.Null(RateMath.CpuPercent(new CpuTimes(100, 200, 100), new CpuTimes(10, 20, 10)));

    [Fact]
    public void CpuPercent_NoTimeAdvance_ReturnsNull()
    {
        var t = new CpuTimes(1, 2, 3);
        Assert.Null(RateMath.CpuPercent(t, t));
    }

    [Fact]
    public void DiskActive_ComputesBusyShare() =>
        Assert.Equal(25.0, RateMath.DiskActivePercent(0, 750, 0, 1000));

    [Fact]
    public void DiskActive_IdleCounterReset_ReturnsNull() =>
        Assert.Null(RateMath.DiskActivePercent(1000, 10, 0, 1000));

    [Fact]
    public void DiskActive_IsClampedToRange() =>
        Assert.Equal(0.0, RateMath.DiskActivePercent(0, 2000, 0, 1000));

    [Fact]
    public void ProcessCpu_NormalizesByLogicalProcessors() =>
        Assert.Equal(25.0, RateMath.ProcessCpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(2), 2.0, 4));

    [Fact]
    public void ProcessCpu_Reset_ReturnsNull() =>
        Assert.Null(RateMath.ProcessCpuPercent(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 2.0, 4));
}

public class BoundedHistoryTests
{
    [Fact]
    public void KeepsOnlyMostRecentSamples()
    {
        var h = new BoundedHistory<int>();
        for (var i = 0; i < 200; i++) h.Add(i);
        Assert.Equal(60, h.Count);
        Assert.Equal(140, h[0]);
        Assert.Equal(199, h[59]);
        Assert.Equal(Enumerable.Range(140, 60), h);
    }

    [Fact]
    public void PreservesOrderBeforeFull()
    {
        var h = new BoundedHistory<int>(5);
        h.Add(1); h.Add(2); h.Add(3);
        Assert.Equal([1, 2, 3], h);
    }

    [Fact]
    public void Clear_EmptiesHistory()
    {
        var h = new BoundedHistory<int>(3);
        h.Add(1); h.Add(2); h.Add(3); h.Add(4);
        h.Clear();
        Assert.Equal(0, h.Count);
        h.Add(9);
        Assert.Equal([9], h);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var h = new BoundedHistory<int>(3);
        h.Add(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => h[1]);
    }

    [Fact]
    public void InvalidCapacity_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedHistory<int>(0));
}

public class FormatAndHealthTests
{
    [Fact]
    public void Rate_ScalesUnits()
    {
        Assert.EndsWith("MB/s", Format.Rate(5 * 1024 * 1024));
        Assert.EndsWith("B/s", Format.Rate(12));
    }

    [Fact]
    public void Duration_FormatsDays() =>
        Assert.Equal("2d 3h 4m", Format.Duration(new TimeSpan(2, 3, 4, 5)));

    private static MetricsSnapshot Snap(double? cpu, MemorySample? mem = null, params DriveSample[] drives) =>
        new(DateTime.Now, cpu, mem, drives, null, null, TimeSpan.Zero, null, null, null);

    [Fact]
    public void Health_Relaxed() =>
        Assert.Equal(HealthLevel.Good, HealthAssessment.Evaluate(Snap(10)).Level);

    [Fact]
    public void Health_SaturatedCpuWarns() =>
        Assert.Equal(HealthLevel.Warning, HealthAssessment.Evaluate(Snap(97)).Level);

    [Fact]
    public void Health_LowDiskSpaceWarns()
    {
        var h = HealthAssessment.Evaluate(Snap(5, null, new DriveSample("C:", "", 1000, 950)));
        Assert.Equal(HealthLevel.Warning, h.Level);
        Assert.Contains("C:", h.Message);
    }

    [Fact]
    public void Health_UnavailableMetricsDoNotWarn() =>
        Assert.Equal(HealthLevel.Good, HealthAssessment.Evaluate(Snap(null)).Level);
}

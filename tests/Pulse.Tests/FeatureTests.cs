using System.Globalization;
using Pulse.Core;
using Pulse.Core.Alerts;
using Pulse.Core.Recording;
using Pulse.Core.Settings;
using Xunit;

namespace Pulse.Tests;

public sealed class ManualClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan t) => UtcNow += t;
}

public class CollectorSlotTests
{
    [Fact]
    public void WarmingUpIsNeverAValue()
    {
        var clock = new ManualClock();
        var slot = new CollectorSlot<string>("x", () => CollectResult<string>.Warming(), clock, TimeSpan.FromSeconds(2));
        slot.Run();
        Assert.Null(slot.Value);
        Assert.Equal(CollectorState.WarmingUp, slot.GetStatus(clock.UtcNow).State);
    }

    [Fact]
    public void ValueGoesStaleAndKeepsTrueAge()
    {
        var clock = new ManualClock();
        var slot = new CollectorSlot<string>("x", () => CollectResult<string>.Ok("v"), clock, TimeSpan.FromSeconds(2));
        slot.Run();
        Assert.Equal("v", slot.Value);
        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Null(slot.Value);
        var status = slot.GetStatus(clock.UtcNow);
        Assert.Equal(CollectorState.Stale, status.State);
        Assert.Equal(TimeSpan.FromSeconds(40), status.Age(clock.UtcNow));
        Assert.Equal("v", slot.LastKnown);
    }

    [Fact]
    public void FailuresBackOffAndRecover()
    {
        var clock = new ManualClock();
        var fail = true;
        var runs = 0;
        var slot = new CollectorSlot<string>("x", () => { runs++; return fail ? CollectResult<string>.Failed("boom") : CollectResult<string>.Ok("v"); }, clock, TimeSpan.FromSeconds(2));
        Assert.True(slot.Run());
        Assert.False(slot.Run());
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(slot.Run());
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(slot.Run());
        Assert.Equal(2, runs);
        fail = false;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(slot.Run());
        Assert.Equal(CollectorState.Current, slot.GetStatus(clock.UtcNow).State);
        Assert.Equal(0, slot.GetStatus(clock.UtcNow).ConsecutiveFailures);
    }

    [Fact]
    public void ExceptionsAreClassified()
    {
        var clock = new ManualClock();
        var slot = new CollectorSlot<string>("x", () => throw new UnauthorizedAccessException(), clock, TimeSpan.FromSeconds(2));
        slot.Run();
        Assert.Equal(CollectorState.AccessDenied, slot.GetStatus(clock.UtcNow).State);
    }

    [Fact]
    public void ResetReturnsToWarmingUp()
    {
        var clock = new ManualClock();
        var slot = new CollectorSlot<string>("x", () => CollectResult<string>.Ok("v"), clock, TimeSpan.FromSeconds(2));
        slot.Run();
        slot.Reset();
        Assert.Null(slot.Value);
        Assert.Equal(CollectorState.WarmingUp, slot.GetStatus(clock.UtcNow).State);
        Assert.True(slot.Run());
    }

    [Fact]
    public void DiagnosticsLogIsBoundedAndScrubbed()
    {
        var log = new DiagnosticsLog();
        for (var i = 0; i < 250; i++) log.Record(DateTime.UtcNow, "t", CollectorState.Failing, $"failure {i}");
        Assert.Equal(DiagnosticsLog.MaxEntries, log.Entries().Count);
        var scrubbed = DiagnosticsLog.Scrub(@"failed at C:\Users\someone\secret\app.exe --token=abc");
        Assert.DoesNotContain("someone", scrubbed);
    }
}

public class HistoryTests
{
    private static readonly DateTime T0 = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MetricsSnapshot Snap(DateTime utc, double? cpu, long epoch = 0) =>
        new(utc.ToLocalTime(), cpu, new MemorySample(1000, 500, 100, 1000, 0), [], null, null, TimeSpan.Zero, null, null, null, Epoch: epoch);

    [Fact]
    public void QueryReturnsOnlyWindowAndKeepsRealTimestamps()
    {
        var store = new HistoryStore();
        foreach (var s in new[] { 0, 2, 3, 9 }) store.Append(Snap(T0.AddSeconds(s), 10 + s), TimeSpan.FromSeconds(2));
        var pts = store.Query(MetricIds.Cpu, T0.AddSeconds(2), T0.AddSeconds(9));
        Assert.Equal(3, pts.Count);
        Assert.Equal(T0.AddSeconds(2), pts[0].TimeUtc);
    }

    [Fact]
    public void NullSamplesAreNotZeroAndStatsIgnoreThem()
    {
        var store = new HistoryStore();
        store.Append(Snap(T0, 10), TimeSpan.FromSeconds(2));
        store.Append(Snap(T0.AddSeconds(2), null), TimeSpan.FromSeconds(2));
        store.Append(Snap(T0.AddSeconds(4), 30), TimeSpan.FromSeconds(2));
        var pts = store.Query(MetricIds.Cpu, T0, T0.AddSeconds(5));
        var stats = SeriesMath.Stats(pts)!;
        Assert.Equal(2, stats.Count);
        Assert.Equal(10, stats.Min);
        Assert.Equal(20, stats.Average);
        Assert.Equal(30, stats.Max);
    }

    [Fact]
    public void StatsOfNothingIsNull() => Assert.Null(SeriesMath.Stats([new HistoryPoint(T0, null, true)]));

    [Fact]
    public void DownsamplePreservesPeaksAndGaps()
    {
        var pts = new List<HistoryPoint>();
        for (var i = 0; i < 200; i++) pts.Add(new HistoryPoint(T0.AddSeconds(i), i == 100 ? 99 : 1));
        pts.RemoveRange(150, 20);
        pts[150] = pts[150] with { Gap = true };
        var chart = SeriesMath.Downsample(pts, T0, T0.AddSeconds(200), 20);
        Assert.True(chart.Count <= 60);
        Assert.Contains(chart, p => p.Value == 99);
        Assert.Contains(chart.Skip(1), p => p.StartsSegment);
    }

    [Fact]
    public void LongGapBreaksTheLine()
    {
        var store = new HistoryStore();
        store.Append(Snap(T0, 10), TimeSpan.FromSeconds(2));
        store.Append(Snap(T0.AddSeconds(60), 10), TimeSpan.FromSeconds(2));
        var chart = SeriesMath.Downsample(store.Query(MetricIds.Cpu, T0, T0.AddSeconds(61)), T0, T0.AddSeconds(61), 100);
        Assert.Equal(2, chart.Count(c => c.StartsSegment));
    }

    [Fact]
    public void ClearEmptiesHistory()
    {
        var store = new HistoryStore();
        store.Append(Snap(T0, 10), TimeSpan.FromSeconds(2));
        store.Clear();
        Assert.Empty(store.Query(MetricIds.Cpu, T0.AddMinutes(-1), T0.AddMinutes(1)));
    }

    [Fact]
    public void MetricExtractionMarksMissingValuesUnavailable()
    {
        var values = MetricCatalog.Extract(Snap(T0, null));
        var cpu = values.Single(v => v.Id == MetricIds.Cpu);
        Assert.Null(cpu.Value);
        Assert.NotEqual(SampleQuality.Valid, cpu.Quality);
        Assert.Equal(50, values.Single(v => v.Id == MetricIds.Memory).Value);
    }
}

public class AlertTests
{
    private static (AlertEngine Engine, ManualClock Clock) Make(AlertRule? rule = null)
    {
        var clock = new ManualClock();
        rule ??= new AlertRule("r", "CPU high", MetricIds.Cpu, 90, 75, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));
        return (new AlertEngine(clock, [rule], TimeSpan.FromSeconds(45)), clock);
    }

    private static void Tick(AlertEngine e, ManualClock c, double? value, int seconds = 5, SampleQuality? q = null)
    {
        c.Advance(TimeSpan.FromSeconds(seconds));
        e.Evaluate([new MetricValue(MetricIds.Cpu, value, q ?? (value is null ? SampleQuality.Unavailable : SampleQuality.Valid))]);
    }

    [Fact]
    public void BriefSpikeNeverActivates()
    {
        var (e, c) = Make();
        Tick(e, c, 95);
        Tick(e, c, 95);
        Tick(e, c, 10);
        Tick(e, c, 10);
        Assert.Empty(e.ActiveIncidents());
    }

    [Fact]
    public void SustainedBreachBecomesActiveAndRecovers()
    {
        var (e, c) = Make();
        for (var i = 0; i < 10; i++) Tick(e, c, 95);
        var incident = Assert.Single(e.ActiveIncidents());
        Assert.Equal(AlertState.Active, incident.State);
        for (var i = 0; i < 10; i++) Tick(e, c, 20);
        Assert.Empty(e.ActiveIncidents());
        Assert.Single(e.RecoveredIncidents());
        Assert.NotEmpty(e.Timeline());
    }

    [Fact]
    public void HysteresisKeepsAlertBetweenThresholds()
    {
        var (e, c) = Make();
        for (var i = 0; i < 10; i++) Tick(e, c, 95);
        for (var i = 0; i < 20; i++) Tick(e, c, 80);
        Assert.Single(e.ActiveIncidents());
    }

    [Fact]
    public void StaleDataDoesNotAdvanceTimers()
    {
        var (e, c) = Make();
        Tick(e, c, 95);
        Tick(e, c, 95);
        for (var i = 0; i < 20; i++) Tick(e, c, null, 5, SampleQuality.Stale);
        Tick(e, c, 95);
        Assert.DoesNotContain(e.ActiveIncidents(), i => i.State == AlertState.Active);
    }

    [Fact]
    public void CooldownSuppressesImmediateRetrigger()
    {
        var (e, c) = Make();
        for (var i = 0; i < 10; i++) Tick(e, c, 95);
        for (var i = 0; i < 10; i++) Tick(e, c, 20);
        for (var i = 0; i < 12; i++) Tick(e, c, 95);
        Assert.Empty(e.ActiveIncidents());
    }

    [Fact]
    public void AcknowledgeAndMute()
    {
        var (e, c) = Make();
        for (var i = 0; i < 10; i++) Tick(e, c, 95);
        var incident = e.ActiveIncidents().Single();
        Assert.True(e.Acknowledge(incident.Id));
        Assert.Equal(AlertState.Acknowledged, incident.State);
        e.Mute("r", TimeSpan.FromHours(1));
        Assert.True(e.IsMuted("r"));
        e.Unmute("r");
        Assert.False(e.IsMuted("r"));
    }

    [Fact]
    public void DisabledRuleNeverFires()
    {
        var (e, c) = Make(new AlertRule("r", "CPU", MetricIds.Cpu, 90, 75, TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.Zero, Enabled: false));
        for (var i = 0; i < 20; i++) Tick(e, c, 99);
        Assert.Empty(e.ActiveIncidents());
    }

    [Fact]
    public void InvalidRangesAreRejected()
    {
        var bad = new AlertRule("r", "x", MetricIds.Cpu, 50, 60, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero);
        Assert.NotEmpty(bad.Validate());
        Assert.Empty(AlertRule.Defaults().SelectMany(r => r.Validate()));
    }

    [Fact]
    public void WildcardRuleMatchesDrives()
    {
        var rule = AlertRule.Defaults().Single(r => r.Id == "drive-full");
        Assert.True(rule.Matches(MetricIds.DriveUsed("C:")));
        Assert.False(rule.Matches(MetricIds.Cpu));
    }
}

public class RecordingTests
{
    private static Pulse.Core.Recording.Recording Make()
    {
        var clock = new ManualClock();
        var rec = new SessionRecorder(clock, 1000);
        rec.Start("test, \"quoted\"", TimeSpan.FromSeconds(2));
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            rec.Append(clock.UtcNow, 0, [new MetricValue(MetricIds.Cpu, i == 1 ? null : 12.5 + i, SampleQuality.Valid)]);
        }
        rec.Bookmark("mark");
        return rec.Stop()!;
    }

    [Fact]
    public void JsonRoundTripsAndRejectsNewerVersion()
    {
        var original = Make();
        var back = RecordingJson.Deserialize(RecordingJson.Serialize(original));
        Assert.Equal(original.Samples.Count, back.Samples.Count);
        Assert.Null(back.Samples[1].V[0]);
        Assert.Single(back.Marks);
        original.Version = 999;
        Assert.ThrowsAny<Exception>(() => RecordingJson.Deserialize(RecordingJson.Serialize(original)));
    }

    [Fact]
    public void CsvIsCultureInvariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var csv = RecordingCsv.Write(Make());
            Assert.Contains("12.5", csv);
            Assert.DoesNotContain("12,5", csv);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void RecorderStopsAtCap()
    {
        var clock = new ManualClock();
        var rec = new SessionRecorder(clock, 3);
        var capped = false;
        rec.CapReached += () => capped = true;
        rec.Start("cap", TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++) { clock.Advance(TimeSpan.FromSeconds(1)); rec.Append(clock.UtcNow, 0, [new MetricValue(MetricIds.Cpu, 1, SampleQuality.Valid)]); }
        Assert.True(capped);
        Assert.True(rec.Stop()!.Truncated);
    }

    [Fact]
    public void StoreSavesListsAndEnforcesRetention()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pulse-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RecordingStore(dir, maxFiles: 2);
            for (var i = 0; i < 4; i++)
            {
                var r = Make();
                r.Id = Guid.NewGuid().ToString("N");
                store.Save(r);
            }
            store.EnforceRetention();
            Assert.True(store.List().Count <= 2);
            Assert.Equal(store.List().Count, store.DeleteAll());
            Assert.Empty(store.List());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class SettingsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "pulse-settings-" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var r = new SettingsStore(TempFile()).Load();
        Assert.False(r.Recovered);
        Assert.False(r.Settings.DiskRecordingEnabled);
    }

    [Fact]
    public void CorruptFileRecoversToDefaults()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var r = new SettingsStore(path).Load();
        Assert.True(r.Recovered);
        Assert.NotNull(r.Message);
        Assert.Equal(2, r.Settings.RefreshSeconds);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void SaveRoundTripsAndNormalizesRanges()
    {
        var path = TempFile();
        var store = new SettingsStore(path);
        var s = new PulseSettings { RefreshSeconds = 7, CloseToTray = true, RetentionDays = -5, ChartWindow = "bogus" };
        store.Save(s.Normalize());
        var back = store.Load().Settings;
        Assert.True(back.CloseToTray);
        Assert.Contains(back.RefreshSeconds, new[] { 1, 2, 5, 10 });
        Assert.True(back.RetentionDays >= 1);
        Assert.Equal("1m", back.ChartWindow);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class ProcessInspectorTests
{
    private static ProcessItem Item(int pid, long start, string name, double? cpu, ulong? mem = 1000) =>
        new(new ProcessKey(pid, start), name, cpu, mem, false);

    private static ProcessObservation Obs(params ProcessItem[] items) =>
        new(DateTime.UtcNow, items, 0, 0, true);

    [Fact]
    public void ReusedPidIsADifferentProcess() =>
        Assert.NotEqual(ProcessView.IdOf(new ProcessKey(10, 1)), ProcessView.IdOf(new ProcessKey(10, 2)));

    [Fact]
    public void GroupsSearchesAndSorts()
    {
        var obs = Obs(Item(1, 1, "chrome", 5), Item(2, 1, "chrome", 7), Item(3, 1, "code", 20));
        var grouped = ProcessView.Build(obs, true, null, ProcessSort.Cpu, true);
        Assert.Equal(2, grouped.Count);
        Assert.Equal("code", grouped[0].Name);
        Assert.Equal(2, grouped.Single(r => r.Name == "chrome").Instances);
        var search = ProcessView.Build(obs, false, "COD", ProcessSort.Name, false);
        Assert.Single(search);
    }

    [Fact]
    public void NullCpuIsNotShownAsZero()
    {
        var row = ProcessView.Build(Obs(Item(1, 1, "locked", null, null)), false, null, ProcessSort.Name, false).Single();
        Assert.Null(row.CpuPercent);
        Assert.Null(row.WorkingSetBytes);
    }

    [Fact]
    public void SelectionHistoryIsBoundedAndMarksExit()
    {
        var h = new SelectedProcessHistory();
        var id = ProcessView.IdOf(new ProcessKey(1, 1));
        h.Select(id);
        for (var i = 0; i < SelectedProcessHistory.MaxSamples + 50; i++) Assert.True(h.Observe(Obs(Item(1, 1, "a", i))));
        Assert.Equal(SelectedProcessHistory.MaxSamples, h.Samples.Count);
        Assert.False(h.Observe(Obs(Item(2, 1, "b", 1))));
        Assert.True(h.Exited);
    }
}

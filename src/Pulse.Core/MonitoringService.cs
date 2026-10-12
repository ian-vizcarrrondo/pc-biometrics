using Pulse.Core.Alerts;
using Pulse.Core.Recording;
using RecordingData = Pulse.Core.Recording.Recording;
using Pulse.Core.Settings;

namespace Pulse.Core;

/// <summary>
/// UI-independent pipeline: engine snapshot -> history -> alerts -> recorder. It keeps running while the window is
/// hidden (tray), so alerts and recordings do not depend on any control being alive.
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private static readonly string[] ContextMetrics =
        [MetricIds.Cpu, MetricIds.Memory, MetricIds.Commit, MetricIds.DiskActive, MetricIds.DiskRead, MetricIds.DiskWrite, MetricIds.NetDown, MetricIds.NetUp, MetricIds.Gpu];

    private readonly MetricsEngine _engine;
    private readonly IClock _clock;
    private bool _disposed;
    private volatile bool _alertsEnabled = true;

    public MonitoringService(MetricsEngine engine, IClock? clock = null, int recorderMaxSamples = SessionRecorder.DefaultMaxSamples)
    {
        _engine = engine;
        _clock = clock ?? SystemClock.Instance;
        History = new HistoryStore();
        Alerts = new AlertEngine(_clock, maxGap: TimeSpan.FromSeconds(45)) { ContextProvider = BuildContext };
        Recorder = new SessionRecorder(_clock, recorderMaxSamples);
        _engine.SnapshotReady += OnSnapshot;
        Alerts.EventRaised += OnAlertEvent;
        Recorder.CapReached += OnRecorderCap;
    }

    public MetricsEngine Engine => _engine;
    public HistoryStore History { get; }
    public AlertEngine Alerts { get; }
    public SessionRecorder Recorder { get; }
    public MetricsSnapshot? Latest { get; private set; }

    /// <summary>Raised on a background thread after history, alerts and recording have consumed the snapshot.</summary>
    public event Action<MetricsSnapshot>? SnapshotProcessed;
    /// <summary>Raised when recording stops on its own because the sample cap was reached.</summary>
    public event Action<RecordingData>? RecordingAutoStopped;

    public void ApplySettings(PulseSettings settings)
    {
        _engine.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
        var errors = Alerts.SetRules(settings.AlertRules);
        if (errors.Count > 0) _engine.Diagnostics.Record(_clock.UtcNow, "alerts", null, "Alert rules rejected: " + errors[0]);
        SetAlertsEnabled(settings.AlertsEnabled);
    }

    public void SetAlertsEnabled(bool enabled)
    {
        if (_alertsEnabled && !enabled) Alerts.Reset();
        _alertsEnabled = enabled;
        _engine.ProcessScanInBackground = enabled;
    }

    private void OnSnapshot(MetricsSnapshot s)
    {
        if (_disposed) return;
        Latest = s;
        History.Append(s, _engine.EffectiveInterval);
        var values = MetricCatalog.Extract(s);
        if (_alertsEnabled) Alerts.Evaluate(values);
        Recorder.Append(s.TimestampUtc, s.Epoch, values);
        SnapshotProcessed?.Invoke(s);
    }

    private void OnAlertEvent(AlertEvent e)
    {
        if (e.State is AlertState.Active or AlertState.Acknowledged or AlertState.Recovered)
            Recorder.Bookmark(e.Message, "alert");
    }

    private void OnRecorderCap()
    {
        var rec = Recorder.Stop();
        if (rec is not null) RecordingAutoStopped?.Invoke(rec);
    }

    private IncidentContext BuildContext(AlertRule rule, string metricId, DateTime nowUtc)
    {
        var from = nowUtc - TimeSpan.FromMinutes(2);
        var telemetry = new List<ContextMetric>();
        foreach (var id in ContextMetrics.Append(metricId).Distinct())
        {
            var pts = History.Query(id, from, nowUtc);
            var stats = SeriesMath.Stats(pts);
            if (stats is null) continue;
            var def = MetricCatalog.Find(id);
            telemetry.Add(new ContextMetric(id, def?.Name ?? id, def?.Unit ?? "", stats.Min, stats.Average, stats.Max,
                pts.LastOrDefault(p => p.Value is not null).Value));
        }

        var obs = _engine.LastProcessObservation;
        var observedUtc = _engine.LastProcessObservationUtc;
        var processes = new List<ContextProcess>();
        if (obs is not null)
        {
            var byCpu = obs.CpuValid ? obs.Items.Where(i => i.CpuPercent > 0).OrderByDescending(i => i.CpuPercent).Take(5) : [];
            var byMem = obs.Items.OrderByDescending(i => i.WorkingSetBytes ?? 0).Take(5);
            foreach (var p in byCpu.Concat(byMem).DistinctBy(p => p.Key))
                processes.Add(new ContextProcess(p.Name, p.CpuPercent, p.WorkingSetBytes));
        }
        return new IncidentContext(telemetry, processes, observedUtc, observedUtc is { } o ? nowUtc - o : null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.SnapshotReady -= OnSnapshot;
        Alerts.EventRaised -= OnAlertEvent;
        Recorder.CapReached -= OnRecorderCap;
    }
}


using System.Globalization;

namespace Pulse.Core.Alerts;

/// <summary>
/// Stateful alert evaluation. Pending -> Active -> (Acknowledged) -> Recovered, driven purely by elapsed time
/// between consecutive valid readings. Missing, warming or stale readings never advance a breach timer and never
/// count toward recovery; a gap longer than <see cref="MaxGap"/> restarts a pending breach and the recovery hold.
/// </summary>
public sealed class AlertEngine
{
    public const int MaxTimeline = 200;
    public const int MaxRecovered = 100;

    private enum Phase { Idle, Pending, Active }

    private sealed class Instance
    {
        public Phase Phase;
        public Incident? Incident;
        public DateTime? LastValidUtc;
        public TimeSpan Breach;
        public TimeSpan Clear;
        public DateTime? RecoveredUtc;
    }

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Dictionary<string, Instance> _instances = new();
    private readonly Dictionary<string, DateTime> _mutedUntil = new();
    private readonly List<AlertEvent> _timeline = [];
    private readonly List<Incident> _recovered = [];
    private IReadOnlyList<AlertRule> _rules;
    private long _nextId;

    public AlertEngine(IClock? clock = null, IEnumerable<AlertRule>? rules = null, TimeSpan? maxGap = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _rules = (rules ?? AlertRule.Defaults()).ToList();
        MaxGap = maxGap ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan MaxGap { get; }

    /// <summary>Supplies surrounding telemetry and the latest process observation when an alert activates.</summary>
    public Func<AlertRule, string, DateTime, IncidentContext?>? ContextProvider { get; set; }

    public event Action<AlertEvent>? EventRaised;
    /// <summary>Raised once per activation unless the rule is muted.</summary>
    public event Action<Incident>? NotificationRequested;

    public IReadOnlyList<AlertRule> Rules
    {
        get { lock (_gate) return _rules; }
    }

    /// <summary>Returns validation errors (empty on success) and applies the rules only when all are valid.</summary>
    public IReadOnlyList<string> SetRules(IEnumerable<AlertRule> rules)
    {
        var list = rules.ToList();
        var errors = list.SelectMany(r => r.Validate().Select(e => $"{r.Name}: {e}")).ToList();
        if (errors.Count > 0) return errors;
        lock (_gate) _rules = list;
        return errors;
    }

    public IReadOnlyList<Incident> ActiveIncidents()
    {
        lock (_gate)
            return _instances.Values.Where(i => i.Incident is not null).Select(i => i.Incident!)
                .OrderBy(i => i.PendingSinceUtc).ToList();
    }

    public IReadOnlyList<Incident> RecoveredIncidents()
    {
        lock (_gate) return _recovered.AsEnumerable().Reverse().ToList();
    }

    public IReadOnlyList<AlertEvent> Timeline()
    {
        lock (_gate) return _timeline.AsEnumerable().Reverse().ToList();
    }

    public bool IsMuted(string ruleId)
    {
        lock (_gate) return _mutedUntil.TryGetValue(ruleId, out var until) && until > _clock.UtcNow;
    }

    public void Mute(string ruleId, TimeSpan duration)
    {
        lock (_gate) _mutedUntil[ruleId] = _clock.UtcNow + duration;
    }

    public void Unmute(string ruleId)
    {
        lock (_gate) _mutedUntil.Remove(ruleId);
    }

    public bool Acknowledge(string incidentId)
    {
        AlertEvent? ev = null;
        lock (_gate)
        {
            var inc = _instances.Values.Select(i => i.Incident).FirstOrDefault(i => i?.Id == incidentId);
            if (inc is not { State: AlertState.Active }) return false;
            inc.State = AlertState.Acknowledged;
            inc.AcknowledgedUtc = _clock.UtcNow;
            ev = Log(inc, "Acknowledged");
        }
        EventRaised?.Invoke(ev);
        return true;
    }

    /// <summary>Drops all state, e.g. when the user deletes history or turns alerts off.</summary>
    public void Reset()
    {
        lock (_gate) { _instances.Clear(); _timeline.Clear(); _recovered.Clear(); }
    }

    public void Evaluate(IReadOnlyList<MetricValue> values)
    {
        var raised = new List<AlertEvent>();
        var notify = new List<Incident>();
        var now = _clock.UtcNow;
        lock (_gate)
        {
            foreach (var rule in _rules)
            {
                if (!rule.Enabled)
                {
                    DisableRule(rule, now, raised);
                    continue;
                }
                foreach (var v in values)
                {
                    if (!rule.Matches(v.Id)) continue;
                    if (v.Value is not { } value || v.Quality != SampleQuality.Valid || !double.IsFinite(value)) continue;
                    Step(rule, v.Id, value, now, raised, notify);
                }
            }
        }
        foreach (var e in raised) EventRaised?.Invoke(e);
        foreach (var i in notify) NotificationRequested?.Invoke(i);
    }

    private void DisableRule(AlertRule rule, DateTime now, List<AlertEvent> raised)
    {
        foreach (var key in _instances.Keys.Where(k => k.StartsWith(rule.Id + "|", StringComparison.Ordinal)).ToList())
        {
            var inst = _instances[key];
            if (inst.Incident is { } inc && inc.State != AlertState.Pending)
                Recover(inst, inc, now, raised, "Rule disabled");
            _instances.Remove(key);
        }
    }

    private void Step(AlertRule rule, string metricId, double value, DateTime now, List<AlertEvent> raised, List<Incident> notify)
    {
        var key = rule.Id + "|" + metricId;
        if (!_instances.TryGetValue(key, out var inst)) _instances[key] = inst = new Instance();

        var dt = inst.LastValidUtc is { } last ? now - last : TimeSpan.Zero;
        inst.LastValidUtc = now;
        if (dt < TimeSpan.Zero) dt = TimeSpan.Zero;
        var gap = dt > MaxGap;
        if (gap)
        {
            if (inst.Phase == Phase.Pending) { inst.Incident = null; inst.Phase = Phase.Idle; }
            inst.Breach = TimeSpan.Zero;
            inst.Clear = TimeSpan.Zero;
            dt = TimeSpan.Zero;
        }

        switch (inst.Phase)
        {
            case Phase.Idle:
                if (value >= rule.Threshold && (inst.RecoveredUtc is not { } r || now - r >= rule.Cooldown))
                {
                    inst.Phase = Phase.Pending;
                    inst.Breach = TimeSpan.Zero;
                    inst.Incident = new Incident
                    {
                        Id = "inc-" + (++_nextId).ToString(CultureInfo.InvariantCulture),
                        RuleId = rule.Id, RuleName = rule.Name, MetricId = metricId,
                        Threshold = rule.Threshold, Unit = rule.Unit,
                        State = AlertState.Pending, PendingSinceUtc = now, PeakValue = value, LastValue = value,
                    };
                }
                break;

            case Phase.Pending:
            {
                var inc = inst.Incident!;
                if (value < rule.Threshold)
                {
                    inst.Phase = Phase.Idle; inst.Incident = null; inst.Breach = TimeSpan.Zero;
                    break;
                }
                inst.Breach += dt;
                inc.LastValue = value;
                inc.PeakValue = Math.Max(inc.PeakValue, value);
                if (inst.Breach >= rule.Duration)
                {
                    inst.Phase = Phase.Active;
                    inst.Clear = TimeSpan.Zero;
                    inc.State = AlertState.Active;
                    inc.ActivatedUtc = now;
                    inc.Context = ContextProvider?.Invoke(rule, metricId, now);
                    inc.Explanation = Explain(rule, inc, inst.Breach);
                    raised.Add(Log(inc, $"{rule.Name}: {Describe(metricId, value, rule.Unit)} for {FormatSpan(inst.Breach)}"));
                    if (!(_mutedUntil.TryGetValue(rule.Id, out var until) && until > now)) notify.Add(inc);
                }
                break;
            }

            case Phase.Active:
            {
                var inc = inst.Incident!;
                inc.LastValue = value;
                inc.PeakValue = Math.Max(inc.PeakValue, value);
                if (value <= rule.ClearThreshold)
                {
                    inst.Clear += dt;
                    if (inst.Clear >= rule.RecoverHold) Recover(inst, inc, now, raised, "Back below " + rule.ClearThreshold.ToString("0.##", CultureInfo.InvariantCulture) + rule.Unit);
                }
                else inst.Clear = TimeSpan.Zero;
                break;
            }
        }
    }

    private void Recover(Instance inst, Incident inc, DateTime now, List<AlertEvent> raised, string why)
    {
        inc.State = AlertState.Recovered;
        inc.RecoveredUtc = now;
        raised.Add(Log(inc, "Recovered. " + why));
        _recovered.Add(inc);
        if (_recovered.Count > MaxRecovered) _recovered.RemoveAt(0);
        inst.Incident = null;
        inst.Phase = Phase.Idle;
        inst.Breach = TimeSpan.Zero;
        inst.Clear = TimeSpan.Zero;
        inst.RecoveredUtc = now;
    }

    private AlertEvent Log(Incident inc, string message)
    {
        var ev = new AlertEvent(_clock.UtcNow, inc.Id, inc.State, message);
        _timeline.Add(ev);
        if (_timeline.Count > MaxTimeline) _timeline.RemoveAt(0);
        return ev;
    }

    private static string Describe(string metricId, double value, string unit)
    {
        var name = MetricCatalog.Find(metricId)?.Name ?? metricId;
        return $"{name} {value.ToString("0.#", CultureInfo.InvariantCulture)}{unit}";
    }

    internal static string FormatSpan(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalSeconds} s";

    /// <summary>Plain-language description. Process lists are presented as observations, never as a diagnosed cause.</summary>
    internal static string Explain(AlertRule rule, Incident inc, TimeSpan sustained)
    {
        var name = MetricCatalog.Find(inc.MetricId)?.Name ?? inc.MetricId;
        var text = $"{name} stayed at or above {rule.Threshold.ToString("0.##", CultureInfo.InvariantCulture)}{rule.Unit} for {FormatSpan(sustained)} (latest {inc.LastValue.ToString("0.#", CultureInfo.InvariantCulture)}{rule.Unit}).";
        var ctx = inc.Context;
        if (ctx?.ProcessObservedUtc is null || ctx.Processes.Count == 0)
            return text + " No process scan was available at that moment, so no processes are listed.";

        var memory = inc.MetricId.StartsWith("memory.", StringComparison.Ordinal);
        var top = (memory ? ctx.Processes.OrderByDescending(p => p.WorkingSetBytes ?? 0) : ctx.Processes.OrderByDescending(p => p.CpuPercent ?? 0))
            .Take(3).Select(p => memory ? $"{p.Name} ({Format.Bytes(p.WorkingSetBytes ?? 0)})" : $"{p.Name} ({(p.CpuPercent ?? 0).ToString("0.#", CultureInfo.InvariantCulture)}% CPU)");
        var age = ctx.ProcessAge is { } a ? FormatSpan(a) + " old" : "age unknown";
        return text + $" The most recent process scan ({age}) listed {string.Join(", ", top)} as the largest {(memory ? "memory" : "CPU")} users at that time. This is an observation, not a confirmed cause.";
    }
}

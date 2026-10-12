namespace Pulse.Core;

/// <param name="Gap">True when this point starts a new line segment: the previous sample was too old, a reset happened, or the reading was missing.</param>
public readonly record struct HistoryPoint(DateTime TimeUtc, double? Value, bool Gap = false);

public readonly record struct ChartPoint(DateTime TimeUtc, double Value, bool StartsSegment);

public sealed record SeriesStats(int Count, double Min, double Average, double Max);

/// <summary>
/// Timestamped, bounded in-memory history per metric id. Retention is by elapsed time and by point count.
/// Missing readings are stored as null so charts show gaps instead of zeros.
/// </summary>
public sealed class HistoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<HistoryPoint>> _series = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastEpoch = new(StringComparer.Ordinal);
    private TimeSpan _retention;

    public HistoryStore(TimeSpan? retention = null, int maxPointsPerMetric = 8000)
    {
        _retention = retention ?? TimeSpan.FromHours(1);
        MaxPointsPerMetric = Math.Max(10, maxPointsPerMetric);
    }

    public int MaxPointsPerMetric { get; }

    public TimeSpan Retention
    {
        get { lock (_gate) return _retention; }
        set { lock (_gate) _retention = value < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : value; }
    }

    /// <summary>Appends every metric in the snapshot. <paramref name="expectedInterval"/> sets the gap threshold (2.5x).</summary>
    public void Append(MetricsSnapshot snapshot, TimeSpan expectedInterval)
    {
        var t = snapshot.TimestampUtc;
        var gapAfter = TimeSpan.FromTicks((long)(Math.Max(expectedInterval.TotalSeconds, 1) * 2.5 * TimeSpan.TicksPerSecond));
        var values = MetricCatalog.Extract(snapshot);
        lock (_gate)
        {
            foreach (var m in values)
            {
                if (!_series.TryGetValue(m.Id, out var list)) _series[m.Id] = list = new List<HistoryPoint>();
                var epochChanged = _lastEpoch.TryGetValue(m.Id, out var e) && e != snapshot.Epoch;
                _lastEpoch[m.Id] = snapshot.Epoch;
                var gap = list.Count == 0 || epochChanged || (m.Value is null) != (list[^1].Value is null) && m.Value is not null
                          || t - list[^1].TimeUtc > gapAfter;
                if (list.Count > 0 && t <= list[^1].TimeUtc) continue;
                // Consecutive missing readings add no information; keep one marker per hole.
                if (m.Value is null && list.Count > 0 && list[^1].Value is null) continue;
                list.Add(new HistoryPoint(t, m.Value, gap));
            }
            Trim(t);
        }
    }

    private void Trim(DateTime now)
    {
        var cutoff = now - _retention;
        foreach (var list in _series.Values)
        {
            var drop = 0;
            while (drop < list.Count - 1 && list[drop].TimeUtc < cutoff) drop++;
            if (list.Count - drop > MaxPointsPerMetric) drop = list.Count - MaxPointsPerMetric;
            if (drop > 0)
            {
                list.RemoveRange(0, drop);
                if (list.Count > 0 && list[0].Value is not null) list[0] = list[0] with { Gap = true };
            }
        }
    }

    public IReadOnlyList<string> MetricIds()
    {
        lock (_gate) return _series.Keys.ToList();
    }

    public IReadOnlyList<HistoryPoint> Query(string id, DateTime fromUtc, DateTime toUtc)
    {
        lock (_gate)
        {
            if (!_series.TryGetValue(id, out var list)) return [];
            var result = new List<HistoryPoint>();
            var prevNull = false;
            foreach (var p in list)
            {
                if (p.TimeUtc < fromUtc) { prevNull = p.Value is null; continue; }
                if (p.TimeUtc > toUtc) break;
                result.Add(result.Count == 0 && !prevNull && p.Value is not null ? p with { Gap = true } : p);
            }
            return result;
        }
    }

    public HistoryPoint? Latest(string id)
    {
        lock (_gate) return _series.TryGetValue(id, out var l) && l.Count > 0 ? l[^1] : null;
    }

    public void Clear()
    {
        lock (_gate) { _series.Clear(); _lastEpoch.Clear(); }
    }
}

public static class SeriesMath
{
    /// <summary>Min/average/max over valid (non-null, finite) samples only. Null when there are none.</summary>
    public static SeriesStats? Stats(IEnumerable<HistoryPoint> points)
    {
        int n = 0; double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (var p in points)
        {
            if (p.Value is not { } v || !double.IsFinite(v)) continue;
            n++; sum += v; if (v < min) min = v; if (v > max) max = v;
        }
        return n == 0 ? null : new SeriesStats(n, min, sum / n, max);
    }

    /// <summary>
    /// Reduces points to roughly <paramref name="buckets"/> time buckets across [from, to]. Each bucket keeps its
    /// minimum and maximum (in time order) so short peaks survive; segment breaks from gaps or missing readings are kept.
    /// </summary>
    public static IReadOnlyList<ChartPoint> Downsample(IReadOnlyList<HistoryPoint> points, DateTime fromUtc, DateTime toUtc, int buckets)
    {
        var result = new List<ChartPoint>();
        var span = (toUtc - fromUtc).Ticks;
        if (span <= 0 || buckets < 1) return result;

        var segment = 0;
        var started = false;
        var breakPending = true;
        (int bucket, int segment)? current = null;
        HistoryPoint min = default, max = default;

        void Flush()
        {
            if (current is null) return;
            var first = !started || breakPending;
            if (min.TimeUtc == max.TimeUtc || min.Value == max.Value)
                result.Add(new ChartPoint(min.TimeUtc, min.Value!.Value, first));
            else if (min.TimeUtc < max.TimeUtc)
            {
                result.Add(new ChartPoint(min.TimeUtc, min.Value!.Value, first));
                result.Add(new ChartPoint(max.TimeUtc, max.Value!.Value, false));
            }
            else
            {
                result.Add(new ChartPoint(max.TimeUtc, max.Value!.Value, first));
                result.Add(new ChartPoint(min.TimeUtc, min.Value!.Value, false));
            }
            started = true;
            breakPending = false;
            current = null;
        }

        foreach (var p in points)
        {
            if (p.TimeUtc < fromUtc || p.TimeUtc > toUtc) continue;
            if (p.Value is not { } v || !double.IsFinite(v))
            {
                Flush();
                breakPending = true;
                segment++;
                continue;
            }
            if (p.Gap && (current is not null || started)) { Flush(); breakPending = true; segment++; }
            var b = (int)Math.Min(buckets - 1, (p.TimeUtc - fromUtc).Ticks * buckets / span);
            if (current is { } c && (c.bucket != b || c.segment != segment)) Flush();
            if (current is null) { current = (b, segment); min = max = p; }
            else
            {
                if (v < min.Value) min = p;
                if (v > max.Value) max = p;
            }
        }
        Flush();
        return result;
    }
}

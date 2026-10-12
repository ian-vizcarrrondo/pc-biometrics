using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Core.Recording;

public sealed class RecordingMetric
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
}

public sealed class RecordingSample
{
    /// <summary>UTC timestamp, ISO 8601.</summary>
    [JsonPropertyName("t")] public DateTime T { get; set; }
    /// <summary>Baseline epoch; changes when collectors were reset (sleep, network change, pause), which breaks lines.</summary>
    [JsonPropertyName("e")] public long Epoch { get; set; }
    /// <summary>One value per entry in the file's metric list, in order. Null means unavailable, never zero.</summary>
    [JsonPropertyName("v")] public double?[] V { get; set; } = [];
}

public sealed class RecordingMark
{
    [JsonPropertyName("t")] public DateTime T { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "bookmark";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

/// <summary>Versioned on-disk and export format. Increment <see cref="CurrentVersion"/> on breaking changes.</summary>
public sealed class Recording
{
    public const string FormatName = "pulse-recording";
    public const int CurrentVersion = 1;

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("startedUtc")] public DateTime StartedUtc { get; set; }
    [JsonPropertyName("endedUtc")] public DateTime EndedUtc { get; set; }
    [JsonPropertyName("intervalSeconds")] public double IntervalSeconds { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("metrics")] public List<RecordingMetric> Metrics { get; set; } = [];
    [JsonPropertyName("samples")] public List<RecordingSample> Samples { get; set; } = [];
    [JsonPropertyName("marks")] public List<RecordingMark> Marks { get; set; } = [];

    [JsonIgnore] public TimeSpan Duration => EndedUtc - StartedUtc;

    /// <summary>Converts one metric into history points so playback and live charts share the same renderer.</summary>
    public IReadOnlyList<HistoryPoint> Series(string metricId)
    {
        var index = Metrics.FindIndex(m => m.Id == metricId);
        var list = new List<HistoryPoint>();
        if (index < 0) return list;
        var gapAfter = TimeSpan.FromSeconds(Math.Max(IntervalSeconds, 1) * 2.5);
        RecordingSample? prev = null;
        foreach (var s in Samples)
        {
            var v = index < s.V.Length ? s.V[index] : null;
            var gap = prev is null || prev.Epoch != s.Epoch || s.T - prev.T > gapAfter;
            list.Add(new HistoryPoint(s.T, v, gap));
            prev = s;
        }
        return list;
    }
}

public static class RecordingJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(Recording r) => JsonSerializer.Serialize(r, Options);

    /// <summary>Throws <see cref="InvalidDataException"/> for malformed files and for formats this version cannot read.</summary>
    public static Recording Deserialize(string json)
    {
        Recording? r;
        try { r = JsonSerializer.Deserialize<Recording>(json, Options); }
        catch (JsonException e) { throw new InvalidDataException("Recording file is not valid JSON: " + e.Message, e); }
        if (r is null || r.Format != Recording.FormatName) throw new InvalidDataException("Not a Pulse recording.");
        if (r.Version > Recording.CurrentVersion) throw new InvalidDataException($"Recording version {r.Version} is newer than this app understands (max {Recording.CurrentVersion}).");
        if (r.Samples.Any(s => s.V.Length > r.Metrics.Count)) throw new InvalidDataException("Recording has more values than metrics.");
        return r;
    }
}

public static class RecordingCsv
{
    /// <summary>Culture-invariant CSV: ISO 8601 UTC timestamps, '.' decimals, empty cells for unavailable readings.</summary>
    public static string Write(Recording r)
    {
        var sb = new StringBuilder();
        sb.Append("timestamp_utc,epoch");
        foreach (var m in r.Metrics) sb.Append(',').Append(Escape($"{m.Id} ({m.Unit})"));
        sb.Append(",mark\r\n");

        var marks = r.Marks.OrderBy(m => m.T).ToList();
        var mi = 0;
        foreach (var s in r.Samples)
        {
            sb.Append(s.T.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            sb.Append(',').Append(s.Epoch.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < r.Metrics.Count; i++)
            {
                sb.Append(',');
                if (i < s.V.Length && s.V[i] is { } v) sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
            }
            var text = new List<string>();
            while (mi < marks.Count && marks[mi].T <= s.T) { text.Add($"{marks[mi].Kind}: {marks[mi].Text}"); mi++; }
            sb.Append(',').Append(Escape(string.Join(" | ", text))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        // A leading =,+,-,@ would be evaluated as a formula by spreadsheets.
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>Records snapshots in memory with a hard cap; nothing touches disk until the caller saves.</summary>
public sealed class SessionRecorder
{
    public const int DefaultMaxSamples = 20_000;
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Dictionary<string, int> _index = new();
    private readonly List<RecordingMetric> _metrics = [];
    private readonly List<(DateTime t, long epoch, Dictionary<int, double> v)> _rows = [];
    private readonly List<RecordingMark> _marks = [];
    private string _label = "";
    private DateTime _start;
    private double _interval;
    private bool _active;

    public SessionRecorder(IClock? clock = null, int maxSamples = DefaultMaxSamples)
    {
        _clock = clock ?? SystemClock.Instance;
        MaxSamples = Math.Max(10, maxSamples);
    }

    public int MaxSamples { get; }
    /// <summary>Raised when the sample cap is reached; the owner should call <see cref="Stop"/>.</summary>
    public event Action? CapReached;

    public bool IsRecording { get { lock (_gate) return _active; } }
    public int SampleCount { get { lock (_gate) return _rows.Count; } }
    public DateTime? StartedUtc { get { lock (_gate) return _active ? _start : null; } }

    public void Start(string label, TimeSpan interval)
    {
        lock (_gate)
        {
            if (_active) return;
            _index.Clear(); _metrics.Clear(); _rows.Clear(); _marks.Clear();
            _label = SanitizeLabel(label);
            _interval = interval.TotalSeconds;
            _start = _clock.UtcNow;
            _active = true;
        }
    }

    public void Bookmark(string text, string kind = "bookmark")
    {
        lock (_gate)
        {
            if (!_active || _marks.Count >= 1000) return;
            _marks.Add(new RecordingMark { T = _clock.UtcNow, Kind = kind, Text = SanitizeLabel(text) });
        }
    }

    public void Append(DateTime timeUtc, long epoch, IReadOnlyList<MetricValue> values)
    {
        bool full;
        lock (_gate)
        {
            if (!_active) return;
            var row = new Dictionary<int, double>();
            foreach (var m in values)
            {
                if (!_index.TryGetValue(m.Id, out var i))
                {
                    i = _metrics.Count;
                    _index[m.Id] = i;
                    var def = MetricCatalog.Find(m.Id);
                    _metrics.Add(new RecordingMetric { Id = m.Id, Name = def?.Name ?? m.Id, Unit = def?.Unit ?? "" });
                }
                if (m.Value is { } v && double.IsFinite(v)) row[i] = v;
            }
            _rows.Add((timeUtc, epoch, row));
            full = _rows.Count >= MaxSamples;
        }
        if (full) CapReached?.Invoke();
    }

    public Recording? Stop()
    {
        lock (_gate)
        {
            if (!_active) return null;
            _active = false;
            var rec = new Recording
            {
                Id = _start.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
                Label = _label,
                StartedUtc = _start,
                EndedUtc = _clock.UtcNow,
                IntervalSeconds = _interval,
                Truncated = _rows.Count >= MaxSamples,
                Metrics = _metrics.ToList(),
                Marks = _marks.ToList(),
            };
            foreach (var (t, epoch, v) in _rows)
            {
                var arr = new double?[_metrics.Count];
                foreach (var kv in v) arr[kv.Key] = kv.Value;
                rec.Samples.Add(new RecordingSample { T = t, Epoch = epoch, V = arr });
            }
            _rows.Clear();
            return rec;
        }
    }

    internal static string SanitizeLabel(string? s)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > 120 ? s[..120] : s;
    }
}

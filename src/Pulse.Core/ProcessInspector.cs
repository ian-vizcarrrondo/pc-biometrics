namespace Pulse.Core;

public enum ProcessSort { Name, Cpu, Memory, Pid }

/// <summary>A row in the process list: one process, or a group of same-named processes with <see cref="Children"/>.</summary>
public sealed record ProcessRow(
    string RowId,
    string Name,
    int Instances,
    int? Pid,
    double? CpuPercent,
    ulong? WorkingSetBytes,
    bool AccessLimited,
    IReadOnlyList<ProcessRow> Children);

public sealed record ProcessSample(DateTime TimeUtc, double? CpuPercent, ulong? WorkingSetBytes);

public static class ProcessView
{
    /// <summary>Stable id for a single process: PID plus creation time, so a recycled PID is a different row.</summary>
    public static string IdOf(ProcessKey k) => $"p{k.Pid}:{k.StartTicks}";
    public static string GroupId(string name) => "g:" + name.ToLowerInvariant();

    public static IReadOnlyList<ProcessRow> Build(ProcessObservation obs, bool grouped, string? search, ProcessSort sort, bool descending)
    {
        var items = obs.Items.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            items = items.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || i.Key.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) == q);
        }

        IEnumerable<ProcessRow> rows;
        if (grouped)
        {
            rows = items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                var kids = Sort(g.Select(ToRow), sort, descending).ToList();
                // A sum is only meaningful when at least one member has a reading; otherwise stay null.
                double? cpu = g.Any(i => i.CpuPercent is not null) ? g.Sum(i => i.CpuPercent ?? 0) : null;
                ulong? mem = g.Any(i => i.WorkingSetBytes is not null) ? (ulong)g.Sum(i => (double)(i.WorkingSetBytes ?? 0)) : null;
                return new ProcessRow(GroupId(g.Key), g.Key, kids.Count, kids.Count == 1 ? kids[0].Pid : null, cpu, mem,
                    g.Any(i => i.AccessLimited), kids.Count > 1 ? kids : []);
            });
        }
        else rows = items.Select(ToRow);
        return Sort(rows, sort, descending).ToList();
    }

    private static ProcessRow ToRow(ProcessItem i) =>
        new(IdOf(i.Key), i.Name, 1, i.Key.Pid, i.CpuPercent, i.WorkingSetBytes, i.AccessLimited, []);

    private static IEnumerable<ProcessRow> Sort(IEnumerable<ProcessRow> rows, ProcessSort sort, bool desc)
    {
        // Unknown values always sort last so a missing reading is never ranked as zero.
        IOrderedEnumerable<ProcessRow> ordered = sort switch
        {
            ProcessSort.Name => desc ? rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            ProcessSort.Pid => desc ? rows.OrderByDescending(r => r.Pid ?? int.MinValue) : rows.OrderBy(r => r.Pid ?? int.MaxValue),
            ProcessSort.Memory => (desc ? rows.OrderByDescending(r => r.WorkingSetBytes.HasValue).ThenByDescending(r => r.WorkingSetBytes ?? 0)
                                        : rows.OrderByDescending(r => r.WorkingSetBytes.HasValue).ThenBy(r => r.WorkingSetBytes ?? 0)),
            _ => (desc ? rows.OrderByDescending(r => r.CpuPercent.HasValue).ThenByDescending(r => r.CpuPercent ?? 0)
                       : rows.OrderByDescending(r => r.CpuPercent.HasValue).ThenBy(r => r.CpuPercent ?? 0)),
        };
        return ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Bounded history for the one selected row. Samples are recorded only when an observation actually contains the
/// process; when it is gone the tracker reports it as exited and stops collecting.
/// </summary>
public sealed class SelectedProcessHistory
{
    public const int MaxSamples = 600;
    private readonly List<ProcessSample> _samples = [];
    private string? _rowId;

    public string? RowId => _rowId;
    public bool Exited { get; private set; }
    public IReadOnlyList<ProcessSample> Samples => _samples;

    public void Select(string? rowId)
    {
        if (rowId == _rowId) return;
        _rowId = rowId;
        _samples.Clear();
        Exited = false;
    }

    /// <summary>Returns false once the selected process is no longer in the observation.</summary>
    public bool Observe(ProcessObservation obs)
    {
        if (_rowId is null || Exited) return false;
        var matches = _rowId.StartsWith("g:", StringComparison.Ordinal)
            ? obs.Items.Where(i => ProcessView.GroupId(i.Name) == _rowId).ToList()
            : obs.Items.Where(i => ProcessView.IdOf(i.Key) == _rowId).ToList();
        if (matches.Count == 0) { Exited = true; return false; }
        double? cpu = matches.Any(m => m.CpuPercent is not null) ? matches.Sum(m => m.CpuPercent ?? 0) : null;
        ulong? mem = matches.Any(m => m.WorkingSetBytes is not null) ? (ulong)matches.Sum(m => (double)(m.WorkingSetBytes ?? 0)) : null;
        _samples.Add(new ProcessSample(obs.ObservedUtc, cpu, mem));
        if (_samples.Count > MaxSamples) _samples.RemoveAt(0);
        return true;
    }
}

using System.Text.RegularExpressions;

namespace Pulse.Core;

public sealed record DiagnosticEntry(DateTime TimeUtc, string Source, CollectorState? State, string Message);

/// <summary>Bounded, in-memory record of recent collector problems. Messages are scrubbed of paths and user names.</summary>
public sealed class DiagnosticsLog
{
    public const int MaxEntries = 100;
    private const int MaxMessageLength = 160;

    private static readonly Regex DrivePath = new(@"[A-Za-z]:\\[^\s""'<>|]*", RegexOptions.Compiled);
    private static readonly Regex UncPath = new(@"\\\\[^\s""'<>|]+", RegexOptions.Compiled);
    private static readonly Regex UnixHome = new(@"/(?:home|Users)/[^\s""'<>|]+", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Queue<DiagnosticEntry> _entries = new();

    public void Record(DateTime timeUtc, string source, CollectorState? state, string? message)
    {
        var entry = new DiagnosticEntry(timeUtc, source, state, Scrub(message ?? state?.ToString() ?? "event"));
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries) _entries.Dequeue();
        }
    }

    public IReadOnlyList<DiagnosticEntry> Entries()
    {
        lock (_gate) return _entries.Reverse().ToList();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public static string Scrub(string message)
    {
        var text = UncPath.Replace(DrivePath.Replace(message, "<path>"), "<path>");
        text = UnixHome.Replace(text, "<path>");
        var user = Environment.UserName;
        if (user.Length > 2) text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length > MaxMessageLength ? text[..MaxMessageLength] + "…" : text;
    }
}

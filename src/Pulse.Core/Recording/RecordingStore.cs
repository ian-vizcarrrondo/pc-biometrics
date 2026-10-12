using System.Globalization;

namespace Pulse.Core.Recording;

public sealed record RecordingInfo(string Path, string Id, string Label, DateTime StartedUtc, TimeSpan Duration, long Bytes);

/// <summary>
/// Local recording folder with retention limits. Only used when the user opted in to disk recording.
/// </summary>
public sealed class RecordingStore
{
    public const string Extension = ".pulse.json";

    public RecordingStore(string directory, int maxFiles = 50, long maxTotalBytes = 200L * 1024 * 1024, TimeSpan? maxAge = null)
    {
        Directory_ = directory;
        MaxFiles = maxFiles;
        MaxTotalBytes = maxTotalBytes;
        MaxAge = maxAge ?? TimeSpan.FromDays(30);
    }

    private string Directory_ { get; }
    public string Directory => Directory_;
    public int MaxFiles { get; }
    public long MaxTotalBytes { get; }
    public TimeSpan MaxAge { get; }

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "Recordings");

    public string Save(Recording recording)
    {
        var path = Path.Combine(Directory_, recording.Id + Extension);
        AtomicFile.WriteAllText(path, RecordingJson.Serialize(recording));
        EnforceRetention(path);
        return path;
    }

    public Recording Load(string path) => RecordingJson.Deserialize(File.ReadAllText(path));

    public IReadOnlyList<RecordingInfo> List()
    {
        if (!System.IO.Directory.Exists(Directory_)) return [];
        var result = new List<RecordingInfo>();
        foreach (var file in new DirectoryInfo(Directory_).EnumerateFiles("*" + Extension))
        {
            try
            {
                var rec = Load(file.FullName);
                result.Add(new RecordingInfo(file.FullName, rec.Id, rec.Label, rec.StartedUtc, rec.Duration, file.Length));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Unreadable or newer-version files are skipped rather than breaking the list.
            }
        }
        return result.OrderByDescending(r => r.StartedUtc).ToList();
    }

    public void Delete(string path)
    {
        if (IsInside(path) && File.Exists(path)) File.Delete(path);
    }

    public int DeleteAll()
    {
        if (!System.IO.Directory.Exists(Directory_)) return 0;
        var n = 0;
        foreach (var f in System.IO.Directory.EnumerateFiles(Directory_, "*" + Extension))
        {
            try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>Removes recordings older than the age limit, then oldest-first until count and total size fit.</summary>
    public int EnforceRetention(string? keep = null)
    {
        if (!System.IO.Directory.Exists(Directory_)) return 0;
        var files = new DirectoryInfo(Directory_).EnumerateFiles("*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var removed = 0;
        long total = 0;
        var kept = 0;
        foreach (var f in files)
        {
            var isKeep = keep is not null && string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase);
            var tooOld = DateTime.UtcNow - f.LastWriteTimeUtc > MaxAge;
            var overLimit = kept >= MaxFiles || total + f.Length > MaxTotalBytes;
            if (!isKeep && (tooOld || overLimit))
            {
                try { f.Delete(); removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                continue;
            }
            kept++;
            total += f.Length;
        }
        return removed;
    }

    public long TotalBytes() =>
        System.IO.Directory.Exists(Directory_) ? new DirectoryInfo(Directory_).EnumerateFiles("*" + Extension).Sum(f => f.Length) : 0;

    private bool IsInside(string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(Directory_) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

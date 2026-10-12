using System.Runtime.InteropServices;

namespace Pulse.Core.Collectors;

public sealed class CpuCollector
{
    private CpuTimes? _previous;

    public void Reset() => _previous = null;

    /// <summary>Returns overall CPU percent since the previous call, or null for the first sample / a counter reset.</summary>
    public double? Sample()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var current = new CpuTimes(idle.Value, kernel.Value, user.Value);
        var previous = _previous;
        _previous = current;
        return previous is null ? null : RateMath.CpuPercent(previous.Value, current);
    }
}

public sealed class MemoryCollector
{
    public MemorySample? Sample(out SystemCounts? counts)
    {
        counts = null;
        var status = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) return null;

        ulong commitUsed = status.TotalPageFile - status.AvailPageFile, commitLimit = status.TotalPageFile;
        ulong cached = 0, pagedPool = 0, nonPagedPool = 0;
        if (Native.GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<Native.PerformanceInformation>()))
        {
            var page = (ulong)perf.PageSize;
            commitUsed = (ulong)perf.CommitTotal * page;
            commitLimit = (ulong)perf.CommitLimit * page;
            cached = (ulong)perf.SystemCache * page;
            pagedPool = (ulong)perf.KernelPaged * page;
            nonPagedPool = (ulong)perf.KernelNonpaged * page;
            counts = new SystemCounts((int)perf.ProcessCount, (int)perf.ThreadCount, (int)perf.HandleCount);
        }

        return new MemorySample(status.TotalPhys, status.TotalPhys - status.AvailPhys, commitUsed, commitLimit, cached, pagedPool, nonPagedPool);
    }
}

public sealed class DriveCollector
{
    public IReadOnlyList<DriveSample> Sample()
    {
        var result = new List<DriveSample>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var total = (ulong)d.TotalSize;
                var free = (ulong)d.AvailableFreeSpace;
                result.Add(new DriveSample(d.Name.TrimEnd('\\'), d.VolumeLabel, total, total - Math.Min(free, total)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
}

/// <summary>Disk throughput and busy time from the volume performance IOCTL (no admin rights needed).</summary>
public sealed class DiskIoCollector : IDisposable
{
    private sealed class Volume(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        public Microsoft.Win32.SafeHandles.SafeFileHandle Handle = handle;
        public Native.DiskPerformance? Previous;
    }

    private sealed record Retry(DateTime At, int Failures, bool Denied);

    private readonly Dictionary<string, Volume> _volumes = new();
    private readonly Dictionary<string, Retry> _failed = new();
    private readonly IClock _clock;

    public DiskIoCollector(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    public void Reset()
    {
        foreach (var v in _volumes.Values) v.Previous = null;
        _failed.Clear();
    }

    private void MarkFailed(string name, bool denied)
    {
        var failures = _failed.TryGetValue(name, out var r) ? r.Failures + 1 : 1;
        var seconds = Math.Min(600, 30 * Math.Pow(2, Math.Min(failures - 1, 6)));
        _failed[name] = new Retry(_clock.UtcNow.AddSeconds(seconds), failures, denied);
    }

    public CollectResult<DiskIoSample> Sample(IReadOnlyList<DriveSample> drives)
    {
        if (drives.Count == 0) return CollectResult<DiskIoSample>.Unsupported("No fixed drives found");
        double read = 0, write = 0, active = 0;
        uint queueDepth = 0;
        var any = false;
        var opened = 0;
        var live = new HashSet<string>();

        foreach (var drive in drives)
        {
            live.Add(drive.Name);
            if (!_volumes.TryGetValue(drive.Name, out var volume))
            {
                if (_failed.TryGetValue(drive.Name, out var retry) && _clock.UtcNow < retry.At) continue;
                var handle = Native.CreateFileW($@"\\.\{drive.Name}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    MarkFailed(drive.Name, Marshal.GetLastWin32Error() == 5);
                    handle.Dispose();
                    continue;
                }
                volume = new Volume(handle);
                _volumes[drive.Name] = volume;
                _failed.Remove(drive.Name);
            }

            if (!Native.DeviceIoControl(volume.Handle, Native.IoctlDiskPerformance, IntPtr.Zero, 0, out var cur,
                    (uint)Marshal.SizeOf<Native.DiskPerformance>(), out _, IntPtr.Zero))
            {
                volume.Handle.Dispose();
                _volumes.Remove(drive.Name);
                MarkFailed(drive.Name, false);
                continue;
            }

            opened++;
            queueDepth += cur.QueueDepth;
            var prev = volume.Previous;
            volume.Previous = cur;
            if (prev is null) continue;
            var p = prev.Value;

            var elapsed = (cur.QueryTime - p.QueryTime) / 1e7;
            var r = RateMath.PerSecond((ulong)p.BytesRead, (ulong)cur.BytesRead, elapsed);
            var w = RateMath.PerSecond((ulong)p.BytesWritten, (ulong)cur.BytesWritten, elapsed);
            var a = RateMath.DiskActivePercent((ulong)p.IdleTime, (ulong)cur.IdleTime, (ulong)p.QueryTime, (ulong)cur.QueryTime);
            if (r is null || w is null || a is null) continue;

            any = true;
            read += r.Value;
            write += w.Value;
            active = Math.Max(active, a.Value);
        }

        foreach (var gone in _volumes.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _volumes[gone].Handle.Dispose();
            _volumes.Remove(gone);
        }
        foreach (var gone in _failed.Keys.Where(k => !live.Contains(k)).ToList()) _failed.Remove(gone);

        if (any) return CollectResult<DiskIoSample>.Ok(new DiskIoSample(read, write, active, queueDepth));
        if (opened > 0) return CollectResult<DiskIoSample>.Warming();
        var denied = _failed.Count > 0 && _failed.Values.All(f => f.Denied);
        return denied
            ? CollectResult<DiskIoSample>.Denied("Windows denied access to volume performance counters")
            : CollectResult<DiskIoSample>.Failed("Volume performance counters could not be opened");
    }

    public void Dispose()
    {
        foreach (var v in _volumes.Values) v.Handle.Dispose();
        _volumes.Clear();
    }
}

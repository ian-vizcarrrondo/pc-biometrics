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

    private readonly Dictionary<string, Volume> _volumes = new();
    private readonly HashSet<string> _failed = new();

    public void Reset()
    {
        foreach (var v in _volumes.Values) v.Previous = null;
    }

    public DiskIoSample? Sample(IReadOnlyList<DriveSample> drives)
    {
        double read = 0, write = 0, active = 0;
        uint queueDepth = 0;
        var any = false;
        var live = new HashSet<string>();

        foreach (var drive in drives)
        {
            live.Add(drive.Name);
            if (!_volumes.TryGetValue(drive.Name, out var volume))
            {
                if (_failed.Contains(drive.Name)) continue;
                var handle = Native.CreateFileW($@"\\.\{drive.Name}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); _failed.Add(drive.Name); continue; }
                volume = new Volume(handle);
                _volumes[drive.Name] = volume;
            }

            if (!Native.DeviceIoControl(volume.Handle, Native.IoctlDiskPerformance, IntPtr.Zero, 0, out var cur,
                    (uint)Marshal.SizeOf<Native.DiskPerformance>(), out _, IntPtr.Zero))
            {
                volume.Handle.Dispose();
                _volumes.Remove(drive.Name);
                _failed.Add(drive.Name);
                continue;
            }

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

        return any ? new DiskIoSample(read, write, active, queueDepth) : null;
    }

    public void Dispose()
    {
        foreach (var v in _volumes.Values) v.Handle.Dispose();
        _volumes.Clear();
    }
}

using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Pulse.Core.Collectors;

public sealed class NetworkCollector
{
    private static readonly string[] VirtualMarkers =
        ["virtual", "vpn", "hyper-v", "vethernet", "loopback", "tap-", "tunnel", "vmware", "vmnet", "virtualbox", "pseudo", "wan miniport", "bluetooth"];

    private sealed record Counters(ulong Received, ulong Sent, long Ticks);

    private readonly Dictionary<string, Counters> _previous = new();
    private ulong _sessionDown, _sessionUp;

    /// <summary>Forgets baselines so the next sample cannot produce a spike after sleep or an adapter change.</summary>
    public void Reset() => _previous.Clear();

    public static bool IsActivePhysical(NetworkInterface nic) =>
        nic.OperationalStatus == OperationalStatus.Up
        && (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx)
        && !IsVirtualName(nic.Description) && !IsVirtualName(nic.Name);

    public static bool IsVirtualName(string name) =>
        VirtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    public NetworkSample? Sample()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return null; }

        var now = Stopwatch.GetTimestamp();
        var adapters = new List<AdapterSample>();
        var seen = new HashSet<string>();
        double down = 0, up = 0;

        foreach (var nic in nics)
        {
            try
            {
                if (!IsActivePhysical(nic)) continue;
                var stats = nic.GetIPStatistics();
                var received = (ulong)Math.Max(0, stats.BytesReceived);
                var sent = (ulong)Math.Max(0, stats.BytesSent);
                seen.Add(nic.Id);

                var had = _previous.TryGetValue(nic.Id, out var prev);
                _previous[nic.Id] = new Counters(received, sent, now);
                if (!had) { adapters.Add(new AdapterSample(nic.Name, 0, 0, nic.Speed)); continue; }

                var elapsed = Stopwatch.GetElapsedTime(prev!.Ticks, now).TotalSeconds;
                var d = RateMath.PerSecond(prev.Received, received, elapsed);
                var u = RateMath.PerSecond(prev.Sent, sent, elapsed);
                if (received >= prev.Received) _sessionDown += received - prev.Received;
                if (sent >= prev.Sent) _sessionUp += sent - prev.Sent;

                adapters.Add(new AdapterSample(nic.Name, d ?? 0, u ?? 0, nic.Speed));
                down += d ?? 0;
                up += u ?? 0;
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }
        }

        foreach (var gone in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) _previous.Remove(gone);
        return new NetworkSample(adapters, down, up, _sessionDown, _sessionUp);
    }
}

public sealed class ProcessCollector
{
    private readonly Dictionary<int, TimeSpan> _cpu = new();
    private readonly Queue<(DateTime Timestamp, bool Started)> _churn = new();
    private HashSet<int>? _previousIds;
    private long _lastTicks;

    public void Reset()
    {
        _cpu.Clear();
        _previousIds = null;
        _churn.Clear();
        _lastTicks = 0;
    }

    /// <summary>Top three process names by CPU and by working set; instances of one program are combined.</summary>
    public TopProcesses? Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds;
        var cores = Environment.ProcessorCount;
        var groups = new Dictionary<string, (int n, double cpu, ulong mem)>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<int, TimeSpan>();
        var currentIds = new HashSet<int>();

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (InvalidOperationException) { return null; }

        foreach (var p in all)
        {
            try
            {
                if (p.Id is 0 or 4) continue;
                currentIds.Add(p.Id);
                var cpuTime = p.TotalProcessorTime;
                var mem = (ulong)p.WorkingSet64;
                current[p.Id] = cpuTime;
                var pct = elapsed > 0 && _cpu.TryGetValue(p.Id, out var prev)
                    ? RateMath.ProcessCpuPercent(prev, cpuTime, elapsed, cores) ?? 0 : 0;
                groups.TryGetValue(p.ProcessName, out var g);
                groups[p.ProcessName] = (g.n + 1, g.cpu + pct, g.mem + mem);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally { p.Dispose(); }
        }

        _cpu.Clear();
        foreach (var kv in current) _cpu[kv.Key] = kv.Value;
        _lastTicks = now;

        if (_previousIds is not null)
        {
            foreach (var _ in currentIds.Except(_previousIds)) _churn.Enqueue((DateTime.Now, true));
            foreach (var _ in _previousIds.Except(currentIds)) _churn.Enqueue((DateTime.Now, false));
        }
        _previousIds = currentIds;

        var cutoff = DateTime.Now.AddMinutes(-1);
        while (_churn.TryPeek(out var change) && change.Timestamp < cutoff) _churn.Dequeue();

        var usage = groups.Select(kv => new ProcessUsage(kv.Key, kv.Value.n, kv.Value.cpu, kv.Value.mem)).ToList();
        var byCpu = elapsed > 0 ? usage.Where(u => u.CpuPercent >= 0.1).OrderByDescending(u => u.CpuPercent).Take(3).ToList() : [];
        var byMem = usage.OrderByDescending(u => u.WorkingSetBytes).Take(3).ToList();
        return new TopProcesses(byCpu, byMem, _churn.Count(e => e.Started), _churn.Count(e => !e.Started));
    }
}

public static class SystemInfo
{
    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static BatterySample? Battery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return null;
        const byte noBattery = 128, unknown = 255;
        if (s.BatteryFlag == unknown || (s.BatteryFlag & noBattery) != 0) return null;
        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        TimeSpan? remaining = s.BatteryLifeTime != uint.MaxValue ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new BatterySample(percent, s.AcLineStatus == 1, (s.BatteryFlag & 8) != 0, remaining);
    }
}

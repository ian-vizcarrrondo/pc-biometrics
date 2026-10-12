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

    public CollectResult<NetworkSample> Sample()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException e) { return CollectResult<NetworkSample>.Failed(e.Message); }

        var now = Stopwatch.GetTimestamp();
        var adapters = new List<AdapterSample>();
        var seen = new HashSet<string>();
        var warming = 0;
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
                if (!had) { warming++; continue; }

                var elapsed = Stopwatch.GetElapsedTime(prev!.Ticks, now).TotalSeconds;
                var d = RateMath.PerSecond(prev.Received, received, elapsed);
                var u = RateMath.PerSecond(prev.Sent, sent, elapsed);
                if (received >= prev.Received) _sessionDown += received - prev.Received;
                if (sent >= prev.Sent) _sessionUp += sent - prev.Sent;
                if (d is null || u is null) { warming++; continue; }

                adapters.Add(new AdapterSample(nic.Name, d.Value, u.Value, nic.Speed));
                down += d.Value;
                up += u.Value;
            }
            catch (NetworkInformationException) { }
            catch (PlatformNotSupportedException) { }
        }

        foreach (var gone in _previous.Keys.Where(k => !seen.Contains(k)).ToList()) _previous.Remove(gone);
        if (adapters.Count == 0 && warming > 0) return CollectResult<NetworkSample>.Warming();
        return CollectResult<NetworkSample>.Ok(new NetworkSample(adapters, down, up, _sessionDown, _sessionUp));
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

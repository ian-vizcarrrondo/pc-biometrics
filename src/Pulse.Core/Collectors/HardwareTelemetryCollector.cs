using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pulse.Core.Collectors;

/// <summary>
/// Reads only sensors that Windows exposes through WMI. ACPI thermal zones are identified as such;
/// they are not assumed to be CPU package or GPU temperatures.
/// </summary>
public sealed class HardwareTelemetryCollector
{
    public CollectResult<ThermalReadings> SampleThermal()
    {
        if (!OperatingSystem.IsWindows()) return CollectResult<ThermalReadings>.Unsupported("Windows only");
        var zones = ReadThermalZones();
        return zones.Count == 0
            ? CollectResult<ThermalReadings>.Unsupported("Windows reports no ACPI thermal zones")
            : CollectResult<ThermalReadings>.Ok(new ThermalReadings(zones));
    }

    public CollectResult<GpuActivity> SampleGpu()
    {
        if (!OperatingSystem.IsWindows()) return CollectResult<GpuActivity>.Unsupported("Windows only");
        var gpu = ReadGpuActivity();
        return gpu is null
            ? CollectResult<GpuActivity>.Unsupported("No GPU engine counters exposed")
            : CollectResult<GpuActivity>.Ok(gpu);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ThermalReading> ReadThermalZones()
    {
        var readings = new List<ThermalReading>();
        foreach (var item in Query(
                     @"root\WMI",
                     "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
                     "InstanceName",
                     "CurrentTemperature"))
        {
            if (item.Value is null) continue;
            var celsius = Convert.ToDouble(item.Value, CultureInfo.InvariantCulture) / 10.0 - 273.15;
            if (!double.IsFinite(celsius) || celsius is < -40 or > 150) continue;
            var zoneName = string.IsNullOrWhiteSpace(item.Name) ? $"Thermal zone {readings.Count + 1}" : item.Name;
            readings.Add(new ThermalReading(zoneName, celsius));
        }

        return readings;
    }

    [SupportedOSPlatform("windows")]
    private static GpuActivity? ReadGpuActivity()
    {
        var maxByEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var activeInstances = 0;
        foreach (var item in Query(
                     @"root\CIMV2",
                     "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine",
                     "Name",
                     "UtilizationPercentage"))
        {
            if (item.Value is null) continue;
            var percent = Convert.ToDouble(item.Value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(percent) || percent is < 0 or > 100) continue;
            if (percent >= 1) activeInstances++;
            var name = item.Name ?? "GPU engine";
            var marker = name.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            var engine = marker >= 0
                ? name[(marker + "engtype_".Length)..].Split('_', 2)[0]
                : "GPU engine";
            if (!maxByEngine.TryGetValue(engine, out var previous) || percent > previous)
                maxByEngine[engine] = percent;
        }

        if (maxByEngine.Count == 0) return null;
        var peak = maxByEngine.MaxBy(pair => pair.Value);
        return new GpuActivity(peak.Key, peak.Value, activeInstances);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<WmiSensor> Query(string scope, string query, string nameProperty, string valueProperty)
    {
        object? locator = null, service = null, results = null;
        var sensors = new List<WmiSensor>();
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null) return sensors;
            locator = Activator.CreateInstance(locatorType);
            if (locator is null) return sensors;

            dynamic locatorDispatch = locator;
            service = locatorDispatch.ConnectServer(".", scope);
            dynamic serviceDispatch = service;
            results = serviceDispatch.ExecQuery(query, "WQL", 48);

            foreach (var rawItem in (IEnumerable)results)
            {
                if (rawItem is null) continue;
                try
                {
                    var itemName = GetProperty(rawItem, nameProperty)?.ToString();
                    var value = GetProperty(rawItem, valueProperty);
                    sensors.Add(new WmiSensor(itemName, value));
                }
                finally
                {
                    Release(rawItem);
                }
            }
        }
        finally
        {
            Release(results);
            Release(service);
            Release(locator);
        }

        return sensors;
    }

    [SupportedOSPlatform("windows")]
    private static object? GetProperty(object item, string name)
    {
        dynamic dispatch = item;
        object? properties = null, property = null;
        try
        {
            properties = dispatch.Properties_;
            dynamic propertyCollection = properties!;
            property = propertyCollection.Item(name);
            dynamic propertyDispatch = property;
            return propertyDispatch.Value;
        }
        finally
        {
            Release(property);
            Release(properties);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    private sealed record WmiSensor(string? Name, object? Value);
}

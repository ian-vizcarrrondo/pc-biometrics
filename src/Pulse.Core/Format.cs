using System.Globalization;

namespace Pulse.Core;

public static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(double bytes, int decimals = 1)
    {
        var (value, unit) = Scale(bytes);
        return value.ToString(unit == "B" ? "0" : "F" + decimals, CultureInfo.CurrentCulture) + " " + unit;
    }

    public static string Rate(double bytesPerSecond)
    {
        var (value, unit) = Scale(bytesPerSecond);
        var text = value >= 100 || unit == "B" ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.0", CultureInfo.CurrentCulture);
        return text + " " + unit + "/s";
    }

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }

    private static (double, string) Scale(double value)
    {
        if (double.IsNaN(value) || value < 0) value = 0;
        var i = 0;
        while (value >= 1024 && i < ByteUnits.Length - 1) { value /= 1024; i++; }
        return (value, ByteUnits[i]);
    }
}

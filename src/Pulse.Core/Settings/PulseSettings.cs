using System.Text.Json;
using System.Text.Json.Serialization;
using Pulse.Core.Alerts;

namespace Pulse.Core.Settings;

public sealed class AlertRuleSetting
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MetricId { get; set; } = "";
    public double Threshold { get; set; }
    public double ClearThreshold { get; set; }
    public double DurationSeconds { get; set; }
    public double RecoverHoldSeconds { get; set; }
    public double CooldownSeconds { get; set; }
    public bool Enabled { get; set; } = true;

    public static AlertRuleSetting From(AlertRule r) => new()
    {
        Id = r.Id, Name = r.Name, MetricId = r.MetricId, Threshold = r.Threshold, ClearThreshold = r.ClearThreshold,
        DurationSeconds = r.Duration.TotalSeconds, RecoverHoldSeconds = r.RecoverHold.TotalSeconds,
        CooldownSeconds = r.Cooldown.TotalSeconds, Enabled = r.Enabled,
    };

    public AlertRule ToRule() => new(Id, Name, MetricId, Threshold, ClearThreshold,
        TimeSpan.FromSeconds(DurationSeconds), TimeSpan.FromSeconds(RecoverHoldSeconds), TimeSpan.FromSeconds(CooldownSeconds), Enabled);
}

public sealed class PulseSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int RefreshSeconds { get; set; } = 2;
    /// <summary>Chart window: 1m, 15m or 1h.</summary>
    public string ChartWindow { get; set; } = "1m";
    public bool AlertsEnabled { get; set; } = true;
    public bool NotificationsEnabled { get; set; }
    public bool CloseToTray { get; set; }
    public bool LaunchAtSignIn { get; set; }
    public bool? ReduceMotion { get; set; }
    /// <summary>Recordings are written to disk only when this is true.</summary>
    public bool DiskRecordingEnabled { get; set; }
    public int RetentionDays { get; set; } = 30;
    public int MaxRecordings { get; set; } = 50;
    public int MaxRecordingsMegabytes { get; set; } = 200;
    public List<AlertRuleSetting> Rules { get; set; } = AlertRule.Defaults().Select(AlertRuleSetting.From).ToList();

    [JsonIgnore]
    public IReadOnlyList<AlertRule> AlertRules => Rules.Select(r => r.ToRule()).ToList();

    /// <summary>Clamps every value into a safe range; invalid rules fall back to the defaults.</summary>
    public PulseSettings Normalize()
    {
        RefreshSeconds = RefreshSeconds is 1 or 2 or 5 or 10 ? RefreshSeconds : 2;
        if (ChartWindow is not ("1m" or "15m" or "1h")) ChartWindow = "1m";
        RetentionDays = Math.Clamp(RetentionDays, 1, 365);
        MaxRecordings = Math.Clamp(MaxRecordings, 1, 500);
        MaxRecordingsMegabytes = Math.Clamp(MaxRecordingsMegabytes, 1, 5000);
        if (Rules is null || Rules.Count == 0 || Rules.Any(r => r.ToRule().Validate().Count > 0))
            Rules = AlertRule.Defaults().Select(AlertRuleSetting.From).ToList();
        return this;
    }

    public TimeSpan ChartSpan => ChartWindow switch { "15m" => TimeSpan.FromMinutes(15), "1h" => TimeSpan.FromHours(1), _ => TimeSpan.FromMinutes(1) };
}

public sealed record SettingsLoadResult(PulseSettings Settings, bool Recovered, string? Message);

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public string Path => _path;

    public static string DefaultPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulse", "settings.json");

    public SettingsLoadResult Load()
    {
        if (!File.Exists(_path)) return new(new PulseSettings(), false, null);
        try
        {
            var s = JsonSerializer.Deserialize<PulseSettings>(File.ReadAllText(_path), Options)
                    ?? throw new JsonException("Settings file was empty.");
            if (s.Version > PulseSettings.CurrentVersion) throw new JsonException($"Settings version {s.Version} is newer than this app.");
            return new(s.Normalize(), false, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string? moved = null;
            try
            {
                moved = _path + ".corrupt";
                File.Move(_path, moved, overwrite: true);
            }
            catch (Exception) { moved = null; }
            return new(new PulseSettings(), true,
                "Settings could not be read and were reset to defaults" + (moved is null ? "." : $"; the old file was kept as {System.IO.Path.GetFileName(moved)}."));
        }
    }

    public void Save(PulseSettings settings) =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings.Normalize(), Options));
}

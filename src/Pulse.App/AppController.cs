using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using Microsoft.Win32;
using Pulse.Core;
using Pulse.Core.Alerts;
using Pulse.Core.Recording;
using Pulse.Core.Settings;
using RecordingData = Pulse.Core.Recording.Recording;

namespace Pulse.App;

/// <summary>
/// Owns the engine, monitoring pipeline, settings, recording storage and tray. Windows only render and forward user intent,
/// so closing or hiding a window never changes what is being collected.
/// </summary>
public sealed class AppController : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Pulse";

    private readonly SettingsStore _settingsStore = new();
    private System.Threading.Timer? _networkDebounce;
    private TrayIcon? _tray;
    private bool _disposed;

    public AppController()
    {
        var load = _settingsStore.Load();
        Settings = load.Settings;
        SettingsMessage = load.Message;
        Engine = new MetricsEngine();
        Monitor = new MonitoringService(Engine);
        RecordingStore = CreateStore();
        Monitor.ApplySettings(Settings);

        Monitor.RecordingAutoStopped += OnRecordingAutoStopped;
        Monitor.Alerts.NotificationRequested += OnNotificationRequested;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        _tray = new TrayIcon(this);
    }

    public PulseSettings Settings { get; private set; }
    public string? SettingsMessage { get; }
    public MetricsEngine Engine { get; }
    public MonitoringService Monitor { get; }
    public RecordingStore RecordingStore { get; private set; }
    public RecordingData? LastRecording { get; private set; }
    public string? LastRecordingPath { get; private set; }
    public bool IsExiting { get; private set; }

    public bool Paused => Engine.Paused;

    public bool ReduceMotion => Settings.ReduceMotion ?? !SystemParameters.ClientAreaAnimation;

    public event Action? PausedChanged;
    public event Action? SettingsChanged;
    public event Action? RecordingChanged;
    public event Action? ShowRequested;

    public void Start() => Engine.Start();

    private readonly HashSet<object> _viewers = [];

    /// <summary>Windows report whether they are on screen; the engine polls at the chosen rate only while one is.</summary>
    public void SetViewing(object owner, bool viewing)
    {
        if (viewing) _viewers.Add(owner); else _viewers.Remove(owner);
        Engine.Visible = _viewers.Count > 0;
    }

    public void RequestShow() => ShowRequested?.Invoke();

    public void SetPaused(bool paused)
    {
        if (Engine.Paused == paused) return;
        Engine.Paused = paused;
        Monitor.Recorder.Bookmark(paused ? "Collection paused" : "Collection resumed", "pause");
        _tray?.Refresh();
        PausedChanged?.Invoke();
    }

    public void TogglePaused() => SetPaused(!Paused);

    /// <summary>Validates, saves and applies settings. Returns an error message, or null on success.</summary>
    public string? ApplySettings(PulseSettings updated)
    {
        foreach (var rule in updated.AlertRules)
        {
            var errors = rule.Validate();
            if (errors.Count > 0) return $"{rule.Name}: {errors[0]}";
        }
        updated.Normalize();
        try { _settingsStore.Save(updated); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Settings could not be saved: " + e.Message;
        }

        Settings = updated;
        RecordingStore = CreateStore();
        Monitor.ApplySettings(updated);
        if (updated.DiskRecordingEnabled) SafeRetention();
        SetLaunchAtSignIn(updated.LaunchAtSignIn);
        SettingsChanged?.Invoke();
        return null;
    }

    public void StartRecording(string label)
    {
        Monitor.Recorder.Start(label, Engine.EffectiveInterval);
        RecordingChanged?.Invoke();
    }

    public RecordingData? StopRecording()
    {
        var rec = Monitor.Recorder.Stop();
        FinishRecording(rec);
        return rec;
    }

    public void Bookmark(string text) => Monitor.Recorder.Bookmark(text);

    public int DeleteHistory()
    {
        Monitor.History.Clear();
        var deleted = 0;
        try { deleted = RecordingStore.DeleteAll(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        LastRecording = null;
        LastRecordingPath = null;
        RecordingChanged?.Invoke();
        return deleted;
    }

    private void OnRecordingAutoStopped(RecordingData rec) => Application.Current?.Dispatcher.BeginInvoke(() => FinishRecording(rec));

    private void FinishRecording(RecordingData? rec)
    {
        LastRecording = rec;
        LastRecordingPath = null;
        if (rec is not null && Settings.DiskRecordingEnabled)
        {
            try { LastRecordingPath = RecordingStore.Save(rec); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Engine.Diagnostics.Record(SystemClock.Instance.UtcNow, "recording", null, "Could not save recording: " + e.Message);
            }
        }
        RecordingChanged?.Invoke();
    }

    private RecordingStore CreateStore() => new(RecordingStore.DefaultDirectory(), Settings.MaxRecordings,
        Settings.MaxRecordingsMegabytes * 1024L * 1024L, TimeSpan.FromDays(Settings.RetentionDays));

    private void SafeRetention()
    {
        try { RecordingStore.EnforceRetention(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void OnNotificationRequested(Incident incident)
    {
        if (!Settings.NotificationsEnabled || _disposed) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
            _tray?.Notify("Pulse observed: " + incident.RuleName, incident.Explanation));
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Engine.ResetBaselines("resumed from sleep");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // Network changes arrive in bursts; coalesce them into one baseline reset.
        _networkDebounce?.Dispose();
        _networkDebounce = new System.Threading.Timer(_ =>
        {
            if (!_disposed) Engine.ResetBaselines("network changed");
        }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private static void SetLaunchAtSignIn(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enabled && Environment.ProcessPath is { } path) key.SetValue(RunValueName, $"\"{path}\" --tray");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    /// <summary>Stops all polling and releases every subscription, then ends the process.</summary>
    public void Exit()
    {
        if (IsExiting) return;
        IsExiting = true;
        Dispose();
        Application.Current?.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        Monitor.RecordingAutoStopped -= OnRecordingAutoStopped;
        Monitor.Alerts.NotificationRequested -= OnNotificationRequested;
        _networkDebounce?.Dispose();
        _tray?.Dispose();
        _tray = null;
        Monitor.Dispose();
        Engine.Dispose();
    }
}

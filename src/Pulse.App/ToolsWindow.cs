using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Pulse.App.Controls;
using Pulse.Core;
using Pulse.Core.Alerts;
using Pulse.Core.Recording;
using Pulse.Core.Settings;
using RecordingData = Pulse.Core.Recording.Recording;

namespace Pulse.App;

/// <summary>Single-instance window hosting alerts, history/recording, process inspection, diagnostics and settings.</summary>
public sealed class ToolsWindow : Window
{
    private static ToolsWindow? _instance;
    private readonly AppController _c;
    private readonly TabControl _tabs = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    // Alerts
    private readonly StackPanel _alertsPanel = new();
    private readonly ListBox _timeline = new();
    private readonly StackPanel _ruleEditor = new();
    private readonly List<(AlertRuleSetting Rule, TextBox Threshold, TextBox Clear, TextBox Duration, CheckBox Enabled)> _ruleBoxes = [];
    private readonly TextBlock _ruleError = new();

    // History
    private readonly TimeChart _chart = new() { Height = 220, Stroke = Color.FromRgb(0x68, 0xD6, 0xE4), Margin = new Thickness(0, 8, 0, 8) };
    private readonly ComboBox _metric = new() { MinWidth = 200 };
    private readonly ComboBox _span = new() { MinWidth = 80 };
    private readonly TextBlock _stats = new();
    private readonly TextBlock _banner = new();
    private readonly TextBox _label = new() { MinWidth = 160, ToolTip = "Recording label / bookmark text" };
    private readonly Button _record = new() { Content = "Start recording" };
    private readonly TextBlock _recStatus = new();
    private readonly ComboBox _playback = new() { MinWidth = 260 };
    private RecordingData? _playing;
    private List<RecordingInfo> _saved = [];

    // Processes
    private readonly ListView _procs = new();
    private readonly TextBox _search = new() { MinWidth = 160 };
    private readonly CheckBox _grouped = new() { Content = "Group by name", IsChecked = true };
    private readonly ComboBox _sort = new() { MinWidth = 110 };
    private readonly TextBlock _procInfo = new();
    private readonly TimeChart _procChart = new() { Height = 120, Stroke = Color.FromRgb(0xBA, 0xA3, 0xFF), Unit = "%" };
    private readonly SelectedProcessHistory _selHistory = new();
    private readonly HashSet<string> _expanded = [];
    private bool _restoringSelection;

    // Diagnostics
    private readonly TextBlock _collectors = new() { FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap };
    private readonly TextBox _log = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 180 };

    // Settings
    private readonly StackPanel _settings = new();

    private static readonly string[] TabNames = ["Alerts", "History", "Processes", "Diagnostics", "Settings"];

    public static void ShowTab(AppController c, string tab, Window? owner)
    {
        if (_instance is null)
        {
            _instance = new ToolsWindow(c);
            _instance.Closed += (_, _) => _instance = null;
        }
        var w = _instance;
        var index = Math.Max(0, Array.IndexOf(TabNames, tab));
        w._tabs.SelectedIndex = index;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Refresh();
    }

    private ToolsWindow(AppController c)
    {
        _c = c;
        Title = "Pulse — tools";
        Width = 980;
        Height = 700;
        MinWidth = 760;
        MinHeight = 520;
        Background = Brush("#0B1117");
        Foreground = Brush("#DCE5EC");
        Resources.Add(typeof(TextBlock), new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.ForegroundProperty, Brush("#DCE5EC")) } });
        Resources.Add(typeof(CheckBox), new Style(typeof(CheckBox)) { Setters = { new Setter(Control.ForegroundProperty, Brush("#DCE5EC")) } });
        FontFamily = new FontFamily("Segoe UI");
        AutomationProperties_Name(this, "Pulse tools");

        _banner.Foreground = Brush("#F3BD72");
        _banner.FontWeight = FontWeights.SemiBold;
        _banner.Visibility = Visibility.Collapsed;

        _tabs.Background = Brush("#0B1117");
        _tabs.Foreground = Brush("#DCE5EC");
        _tabs.Items.Add(Tab("Alerts", BuildAlerts()));
        _tabs.Items.Add(Tab("History", BuildHistory()));
        _tabs.Items.Add(Tab("Processes", BuildProcesses()));
        _tabs.Items.Add(Tab("Diagnostics", BuildDiagnostics()));
        _tabs.Items.Add(Tab("Settings", BuildSettings()));
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) Refresh(); };
        Content = _tabs;

        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            _c.SetViewing(this, IsVisible && WindowState != WindowState.Minimized);
            if (IsVisible) _timer.Start(); else _timer.Stop();
        };
        StateChanged += (_, _) => _c.SetViewing(this, IsVisible && WindowState != WindowState.Minimized);
        Closed += (_, _) =>
        {
            _timer.Stop();
            _c.SetViewing(this, false);
            _c.RecordingChanged -= OnRecordingChanged;
            _c.SettingsChanged -= OnSettingsChanged;
        };
        _c.RecordingChanged += OnRecordingChanged;
        _c.SettingsChanged += OnSettingsChanged;
    }

    private static void AutomationProperties_Name(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);

    private static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private static TabItem Tab(string header, UIElement content) => new()
    {
        Header = header,
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) }
    };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };
    private static TextBlock Note(string text) => new() { Text = text, Foreground = Brush("#91A2B0"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
    private static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var i in items)
        {
            if (i is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 0);
            p.Children.Add(i);
        }
        return p;
    }

    private void OnRecordingChanged() => Dispatcher.BeginInvoke(new Action(() => { Refresh(); LoadSaved(); }));
    private void OnSettingsChanged() => Dispatcher.BeginInvoke(new Action(() => { BuildSettingsContent(); BuildRuleEditor(); }));

    // ---------------- Alerts ----------------
    private UIElement BuildAlerts()
    {
        var root = new StackPanel();
        root.Children.Add(Note("Alerts are observations about this PC, not diagnoses. A rule must stay past its threshold for its full duration before it becomes active, and stale data never advances a timer."));
        root.Children.Add(Heading("Active alerts"));
        root.Children.Add(_alertsPanel);
        root.Children.Add(Heading("Timeline (most recent 200 events)"));
        _timeline.MaxHeight = 220;
        _timeline.Background = Brush("#121B24");
        _timeline.Foreground = Brush("#DCE5EC");
        root.Children.Add(_timeline);
        root.Children.Add(Heading("Rules"));
        root.Children.Add(_ruleEditor);
        _ruleError.Foreground = Brush("#F18C83");
        _ruleError.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_ruleError);
        BuildRuleEditor();
        return root;
    }

    private void BuildRuleEditor()
    {
        _ruleEditor.Children.Clear();
        _ruleBoxes.Clear();
        foreach (var rule in _c.Settings.Rules)
        {
            var thr = new TextBox { Width = 70, Text = rule.Threshold.ToString(CultureInfo.InvariantCulture) };
            var clr = new TextBox { Width = 70, Text = rule.ClearThreshold.ToString(CultureInfo.InvariantCulture) };
            var dur = new TextBox { Width = 70, Text = rule.DurationSeconds.ToString(CultureInfo.InvariantCulture) };
            var en = new CheckBox { IsChecked = rule.Enabled, Content = rule.Name, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(thr, rule.Name + " threshold");
            System.Windows.Automation.AutomationProperties.SetName(clr, rule.Name + " clear threshold");
            System.Windows.Automation.AutomationProperties.SetName(dur, rule.Name + " duration seconds");
            _ruleBoxes.Add((rule, thr, clr, dur, en));
            _ruleEditor.Children.Add(Row(en, Label("trigger"), thr, Label("clear"), clr, Label("seconds"), dur));
        }
        var save = new Button { Content = "Save rules", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += (_, _) => SaveRules();
        _ruleEditor.Children.Add(save);
    }

    private static TextBlock Label(string t) => new() { Text = t, Foreground = Brush("#91A2B0"), VerticalAlignment = VerticalAlignment.Center };

    private void SaveRules()
    {
        var s = _c.Settings;
        foreach (var (rule, thr, clr, dur, en) in _ruleBoxes)
        {
            if (!double.TryParse(thr.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ||
                !double.TryParse(clr.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cl) ||
                !double.TryParse(dur.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                _ruleError.Text = $"{rule.Name}: enter numbers using '.' as the decimal separator.";
                return;
            }
            rule.Threshold = t;
            rule.ClearThreshold = cl;
            rule.DurationSeconds = d;
            rule.Enabled = en.IsChecked == true;
        }
        var error = _c.ApplySettings(s);
        _ruleError.Text = error ?? "";
        if (error is not null) { _c.ApplySettings(new SettingsStore().Load().Settings); }
    }

    private void RefreshAlerts()
    {
        var alerts = _c.Monitor.Alerts;
        _alertsPanel.Children.Clear();
        var active = alerts.ActiveIncidents();
        if (active.Count == 0) _alertsPanel.Children.Add(Note(_c.Settings.AlertsEnabled ? "No active alerts." : "Alerts are turned off in Settings."));
        foreach (var i in active)
        {
            var box = new Border { Background = Brush("#121B24"), BorderBrush = Brush(i.State == AlertState.Active ? "#F18C83" : "#F3BD72"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 4) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = $"{i.RuleName} — {i.State}", FontWeight = FontWeights.SemiBold });
            sp.Children.Add(Note(i.Explanation));
            if (i.Context is { } ctx)
            {
                var lines = ctx.Telemetry.Select(m => $"{m.Name}: min {m.Min:0.#} · avg {m.Average:0.#} · max {m.Max:0.#} {m.Unit}");
                sp.Children.Add(Note("Telemetry (observed): " + string.Join(" | ", lines)));
                var procs = ctx.Processes.Count == 0
                    ? "no process observation available"
                    : $"{string.Join(", ", ctx.Processes.Select(p => $"{p.Name} {(p.CpuPercent is { } cp ? cp.ToString("0.0") + "%" : "")} {(p.WorkingSetBytes is { } w ? Format.Bytes(w) : "")}".Trim()))} (observed {Format.Duration(ctx.ProcessAge ?? TimeSpan.Zero)} before the alert; may not be the cause)";
                sp.Children.Add(Note("Processes: " + procs));
            }
            var ack = new Button { Content = "Acknowledge", IsEnabled = i.State == AlertState.Active };
            var id = i.Id;
            var ruleId = i.RuleId;
            ack.Click += (_, _) => { alerts.Acknowledge(id); RefreshAlerts(); };
            var mute = new Button { Content = "Mute rule 1 h" };
            mute.Click += (_, _) => { alerts.Mute(ruleId, TimeSpan.FromHours(1)); RefreshAlerts(); };
            sp.Children.Add(Row(ack, mute));
            box.Child = sp;
            _alertsPanel.Children.Add(box);
        }
        _timeline.Items.Clear();
        foreach (var e in alerts.Timeline().Reverse())
            _timeline.Items.Add($"{e.TimeUtc.ToLocalTime():HH:mm:ss}  {e.Message}");
    }

    // ---------------- History ----------------
    private UIElement BuildHistory()
    {
        var root = new StackPanel();
        foreach (var m in MetricCatalog.Fixed) _metric.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
        _metric.SelectedIndex = 0;
        foreach (var s in new[] { "1m", "15m", "1h" }) _span.Items.Add(s);
        _span.SelectedIndex = 0;
        _metric.SelectionChanged += (_, _) => Refresh();
        _span.SelectionChanged += (_, _) => Refresh();
        System.Windows.Automation.AutomationProperties.SetName(_metric, "Metric");
        System.Windows.Automation.AutomationProperties.SetName(_span, "Chart range");
        root.Children.Add(_banner);
        root.Children.Add(Row(Label("Metric"), _metric, Label("Range"), _span));
        root.Children.Add(_chart);
        root.Children.Add(_stats);
        root.Children.Add(Note("Arrow keys move the chart cursor when the chart is focused. Min/avg/max use valid samples only; gaps are never drawn as zero."));

        root.Children.Add(Heading("Recording"));
        _record.Click += (_, _) => ToggleRecording();
        var bookmark = new Button { Content = "Add bookmark" };
        bookmark.Click += (_, _) => { if (_c.Monitor.Recorder.IsRecording) _c.Bookmark(_label.Text); };
        root.Children.Add(Row(Label("Label"), _label, _record, bookmark));
        root.Children.Add(_recStatus);
        root.Children.Add(Note(_c.Settings.DiskRecordingEnabled
            ? "Recordings are saved locally when you stop (disk recording is on)."
            : "Disk recording is off: recordings stay in memory until you export or close Pulse. Enable it in Settings."));

        var export = new Button { Content = "Export last recording…" };
        export.Click += (_, _) => ExportRecording();
        var open = new Button { Content = "Open file…" };
        open.Click += (_, _) => OpenRecordingFile();
        var live = new Button { Content = "Back to live" };
        live.Click += (_, _) => { _playing = null; Refresh(); };
        var delete = new Button { Content = "Delete all history…" };
        delete.Click += (_, _) => DeleteHistory();
        _playback.SelectionChanged += (_, _) =>
        {
            if (_playback.SelectedIndex is >= 0 and var idx && idx < _saved.Count)
            {
                try { _playing = _c.RecordingStore.Load(_saved[idx].Path); Refresh(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open recording"); }
            }
        };
        root.Children.Add(Row(export, open, live, delete));
        root.Children.Add(Row(Label("Saved"), _playback));
        LoadSaved();
        return root;
    }

    private void LoadSaved()
    {
        try
        {
            _saved = _c.RecordingStore.List().OrderByDescending(r => r.StartedUtc).ToList();
            _playback.Items.Clear();
            foreach (var r in _saved) _playback.Items.Add($"{r.StartedUtc.ToLocalTime():g} · {r.Label} · {Format.Duration(r.Duration)} · {Format.Bytes(r.Bytes)}");
        }
        catch { }
    }

    private void ToggleRecording()
    {
        if (_c.Monitor.Recorder.IsRecording) _c.StopRecording();
        else _c.StartRecording(string.IsNullOrWhiteSpace(_label.Text) ? "Recording" : _label.Text.Trim());
        Refresh();
    }

    private RecordingData? ExportSource() => _playing ?? _c.LastRecording;

    private void ExportRecording()
    {
        var r = ExportSource();
        if (r is null) { MessageBox.Show(this, "Nothing to export yet. Record a session first.", "Export"); return; }
        var dlg = new SaveFileDialog { Filter = "Pulse JSON (*.json)|*.json|CSV (*.csv)|*.csv", FileName = "pulse-recording" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            AtomicFile.WriteAllText(dlg.FileName, dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? RecordingCsv.Write(r) : RecordingJson.Serialize(r));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }

    private void OpenRecordingFile()
    {
        var dlg = new OpenFileDialog { Filter = "Pulse recording (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { _playing = RecordingJson.Deserialize(File.ReadAllText(dlg.FileName)); Refresh(); }
        catch (Exception ex) { MessageBox.Show(this, "Not a readable Pulse recording: " + ex.Message, "Open failed"); }
    }

    private void DeleteHistory()
    {
        if (MessageBox.Show(this, "Delete in-memory history and all saved recordings?", "Delete history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _playing = null;
        _c.DeleteHistory();
        LoadSaved();
        Refresh();
    }

    private void RefreshHistory()
    {
        var id = (_metric.SelectedItem as ComboBoxItem)?.Tag as string ?? MetricIds.Cpu;
        var def = MetricCatalog.Find(id);
        _chart.Unit = def?.Unit ?? "";
        IReadOnlyList<HistoryPoint> pts;
        if (_playing is { } p)
        {
            pts = p.Series(id);
            _chart.From = p.StartedUtc;
            _chart.To = p.EndedUtc > p.StartedUtc ? p.EndedUtc : p.StartedUtc.AddSeconds(1);
            _chart.IsPlayback = true;
            _banner.Text = $"NOT LIVE — playing back \"{p.Label}\" ({p.StartedUtc.ToLocalTime():g}, {Format.Duration(p.Duration)}){(p.Truncated ? " · truncated at sample cap" : "")}";
            _banner.Visibility = Visibility.Visible;
        }
        else
        {
            var span = _span.SelectedIndex switch { 1 => TimeSpan.FromMinutes(15), 2 => TimeSpan.FromHours(1), _ => TimeSpan.FromMinutes(1) };
            var now = DateTime.UtcNow;
            _chart.From = now - span;
            _chart.To = now;
            pts = _c.Monitor.History.Query(id, _chart.From, now);
            _chart.IsPlayback = false;
            _banner.Visibility = _c.Paused ? Visibility.Visible : Visibility.Collapsed;
            _banner.Text = "PAUSED — history is not advancing";
        }
        _chart.Series = pts;
        var st = SeriesMath.Stats(pts);
        _stats.Text = st is null ? "No valid samples in range." : $"min {st.Min:0.##} · avg {st.Average:0.##} · max {st.Max:0.##} {def?.Unit} · {st.Count} samples";

        var rec = _c.Monitor.Recorder;
        _record.Content = rec.IsRecording ? "Stop recording" : "Start recording";
        _recStatus.Text = rec.IsRecording
            ? $"● Recording {rec.SampleCount:N0} samples since {(rec.StartedUtc ?? DateTime.UtcNow).ToLocalTime():HH:mm:ss}"
            : (_c.LastRecording is { } lr ? $"Last recording: \"{lr.Label}\", {lr.Samples.Count:N0} samples{(lr.Truncated ? " (truncated)" : "")}" :             "Not recording.");
    }

    // ---------------- Processes ----------------
    private sealed record ProcLine(string RowId, string Name, string Pid, string Cpu, string Memory, string Note, bool IsGroup)
    {
        public string Display => (IsGroup ? (Expanded ? "▼ " : "▶ ") : "    ") + Name;
        public bool Expanded { get; init; }
    }

    private UIElement BuildProcesses()
    {
        var root = new DockPanel();
        foreach (var s in new[] { "CPU", "Memory", "Name", "PID" }) _sort.Items.Add(s);
        _sort.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search processes");
        System.Windows.Automation.AutomationProperties.SetName(_sort, "Sort processes");
        var tm = new Button { Content = "Open Task Manager" };
        tm.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { } };
        _search.TextChanged += (_, _) => RefreshProcesses();
        _sort.SelectionChanged += (_, _) => RefreshProcesses();
        _grouped.Click += (_, _) => RefreshProcesses();
        var bar = Row(Label("Search"), _search, Label("Sort"), _sort, _grouped, tm);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Process", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ProcLine.Display)) });
        grid.Columns.Add(new GridViewColumn { Header = "PID", Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ProcLine.Pid)) });
        grid.Columns.Add(new GridViewColumn { Header = "CPU", Width = 80, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ProcLine.Cpu)) });
        grid.Columns.Add(new GridViewColumn { Header = "Memory", Width = 100, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ProcLine.Memory)) });
        grid.Columns.Add(new GridViewColumn { Header = "Note", Width = 160, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(ProcLine.Note)) });
        _procs.View = grid;
        _procs.Background = Brush("#121B24");
        _procs.Foreground = Brush("#DCE5EC");
        _procs.Height = 320;
        _procs.SelectionMode = SelectionMode.Single;
        VirtualizingPanel.SetIsVirtualizing(_procs, true);
        VirtualizingPanel.SetVirtualizationMode(_procs, VirtualizationMode.Recycling);
        System.Windows.Automation.AutomationProperties.SetName(_procs, "Process list");
        _procs.SelectionChanged += (_, _) =>
        {
            if (_restoringSelection) return;
            _selHistory.Select((_procs.SelectedItem as ProcLine)?.RowId);
            UpdateSelected();
        };
        _procs.MouseDoubleClick += (_, _) =>
        {
            if (_procs.SelectedItem is ProcLine { IsGroup: true } g)
            {
                if (!_expanded.Remove(g.RowId)) _expanded.Add(g.RowId);
                RefreshProcesses();
            }
        };
        _procs.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && _procs.SelectedItem is ProcLine { IsGroup: true } g)
            {
                if (!_expanded.Remove(g.RowId)) _expanded.Add(g.RowId);
                RefreshProcesses();
                e.Handled = true;
            }
        };

        var lower = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        lower.Children.Add(_procInfo);
        lower.Children.Add(_procChart);
        lower.Children.Add(Note("CPU is normalized to total logical processors, like Task Manager. Process data is a throttled observation, not a continuous trace. Pulse never ends processes."));
        DockPanel.SetDock(lower, Dock.Bottom);
        root.Children.Add(lower);
        root.Children.Add(_procs);
        return root;
    }

    private void RefreshProcesses()
    {
        var obs = _c.Engine.LastProcessObservation;
        if (obs is null) { _procInfo.Text = "No process scan yet."; return; }
        var sort = _sort.SelectedIndex switch { 1 => ProcessSort.Memory, 2 => ProcessSort.Name, 3 => ProcessSort.Pid, _ => ProcessSort.Cpu };
        var rows = ProcessView.Build(obs, _grouped.IsChecked == true, _search.Text, sort, sort is ProcessSort.Cpu or ProcessSort.Memory);
        var lines = new List<ProcLine>();
        foreach (var r in rows)
        {
            var group = r.Children is { Count: > 0 } || r.Instances > 1;
            var open = group && _expanded.Contains(r.RowId);
            lines.Add(ToLine(r, group, open, false));
            if (open) foreach (var ch in r.Children!) lines.Add(ToLine(ch, false, false, true));
        }
        var keep = _selHistory.RowId;
        _restoringSelection = true;
        _procs.ItemsSource = lines;
        if (keep is not null) _procs.SelectedItem = lines.FirstOrDefault(l => l.RowId == keep);
        _restoringSelection = false;
        _selHistory.Observe(obs);
        UpdateSelected();
    }

    private static ProcLine ToLine(ProcessRow r, bool group, bool open, bool child) => new(
        r.RowId,
        group ? $"{r.Name} ×{r.Instances}" : r.Name,
        r.Pid?.ToString(CultureInfo.InvariantCulture) ?? "",
        r.CpuPercent is { } c ? c.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "—",
        r.WorkingSetBytes is { } w ? Format.Bytes(w) : "—",
        r.AccessLimited ? "limited access" : "",
        group) { Expanded = open };

    private void UpdateSelected()
    {
        var age = _c.Engine.LastProcessObservationUtc is { } t ? DateTime.UtcNow - t : (TimeSpan?)null;
        var ageText = age is { } a ? $"scan {Format.Duration(a)} old" : "no scan";
        if (_selHistory.RowId is null) { _procInfo.Text = $"Select a process to chart its CPU while it is observed. ({ageText})"; _procChart.Series = []; return; }
        _procInfo.Text = _selHistory.Exited ? "Selected process has exited — history kept below." : $"Selected process history ({ageText})";
        var samples = _selHistory.Samples;
        if (samples.Count == 0) { _procChart.Series = []; return; }
        _procChart.From = samples[0].TimeUtc;
        _procChart.To = samples[^1].TimeUtc > samples[0].TimeUtc ? samples[^1].TimeUtc : samples[0].TimeUtc.AddSeconds(1);
        _procChart.Series = samples.Select(s => new HistoryPoint(s.TimeUtc, s.CpuPercent, s.CpuPercent is null)).ToList();
    }

    // ---------------- Diagnostics ----------------
    private UIElement BuildDiagnostics()
    {
        var root = new StackPanel();
        root.Children.Add(Note("Local only. The log is bounded to 100 entries and excludes process arguments and user paths."));
        root.Children.Add(Heading("Collectors"));
        root.Children.Add(_collectors);
        root.Children.Add(Heading("Log"));
        root.Children.Add(_log);
        var clear = new Button { Content = "Clear log", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        clear.Click += (_, _) => { _c.Engine.Diagnostics.Clear(); RefreshDiagnostics(); };
        root.Children.Add(clear);
        return root;
    }

    private void RefreshDiagnostics()
    {
        var snap = _c.Monitor.Latest;
        var lines = new List<string> { $"{"collector",-12}{"state",-13}{"last good",-12}{"failures",-9}detail" };
        if (snap?.Statuses is { } statuses)
            foreach (var s in statuses)
                lines.Add($"{s.Name,-12}{s.State,-13}{(s.Age(snap.TimestampUtc) is { } a ? Format.Duration(a) + " ago" : "never"),-12}{s.ConsecutiveFailures,-9}{s.Detail}");
        else lines.Add("No snapshot yet.");
        _collectors.Text = string.Join(Environment.NewLine, lines);
        _collectors.Foreground = Brush("#DCE5EC");
        _log.Text = string.Join(Environment.NewLine, _c.Engine.Diagnostics.Entries().Select(e => $"{e.TimeUtc.ToLocalTime():HH:mm:ss} [{e.Source}] {e.State}: {e.Message}"));
    }

    // ---------------- Settings ----------------
    private UIElement BuildSettings()
    {
        BuildSettingsContent();
        return _settings;
    }

    private void BuildSettingsContent()
    {
        _settings.Children.Clear();
        var s = _c.Settings;
        if (_c.SettingsMessage is { } m) _settings.Children.Add(Note(m));
        _settings.Children.Add(Note("Stored at " + SettingsStore.DefaultPath()));

        CheckBox Check(string text, bool value) { var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) }; _settings.Children.Add(c); return c; }
        var alerts = Check("Evaluate alert rules", s.AlertsEnabled);
        var notify = Check("Windows notifications for alerts (observation-only wording)", s.NotificationsEnabled);
        var tray = Check("Close button hides Pulse to the tray (Exit in the tray menu stops all polling)", s.CloseToTray);
        var launch = Check("Launch Pulse at sign-in (starts in the tray)", s.LaunchAtSignIn);
        var motion = Check("Reduce motion", s.ReduceMotion ?? !SystemParameters.ClientAreaAnimation);
        var disk = Check("Allow saving recordings to disk (off = memory only)", s.DiskRecordingEnabled);

        TextBox Num(string label, int value) { var tb = new TextBox { Width = 70, Text = value.ToString(CultureInfo.InvariantCulture) }; System.Windows.Automation.AutomationProperties.SetName(tb, label); _settings.Children.Add(Row(Label(label), tb)); return tb; }
        var days = Num("Keep recordings (days)", s.RetentionDays);
        var count = Num("Max recordings", s.MaxRecordings);
        var mb = Num("Max recordings size (MB)", s.MaxRecordingsMegabytes);

        var err = new TextBlock { Foreground = Brush("#F18C83"), TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "Save settings", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 4) };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(days.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ||
                !int.TryParse(count.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ||
                !int.TryParse(mb.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            { err.Text = "Retention values must be whole numbers."; return; }
            var u = _c.Settings;
            u.AlertsEnabled = alerts.IsChecked == true;
            u.NotificationsEnabled = notify.IsChecked == true;
            u.CloseToTray = tray.IsChecked == true;
            u.LaunchAtSignIn = launch.IsChecked == true;
            u.ReduceMotion = motion.IsChecked == true;
            u.DiskRecordingEnabled = disk.IsChecked == true;
            u.RetentionDays = d;
            u.MaxRecordings = n;
            u.MaxRecordingsMegabytes = size;
            err.Text = _c.ApplySettings(u) ?? "Saved.";
        };
        _settings.Children.Add(save);
        _settings.Children.Add(err);
    }

    // ---------------- Refresh ----------------
    private void Refresh()
    {
        if (!IsVisible) return;
        switch (_tabs.SelectedIndex)
        {
            case 0: RefreshAlerts(); break;
            case 1: RefreshHistory(); break;
            case 2: RefreshProcesses(); break;
            case 3: RefreshDiagnostics(); break;
        }
    }
}


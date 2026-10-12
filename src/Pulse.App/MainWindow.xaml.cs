using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.App.Controls;
using Pulse.Core;

namespace Pulse.App;

/// <summary>View only: renders snapshots and history; collection, alerts, recording and settings live in <see cref="AppController"/>.</summary>
public partial class MainWindow : Window
{
    private readonly AppController _controller;
    private MetricsSnapshot? _pending;
    private int _renderQueued;
    private TimeSpan _span;
    private bool _loaded;

    public MainWindow(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        _span = controller.Settings.ChartSpan;
        _controller.Monitor.SnapshotProcessed += OnSnapshotProcessed;
        _controller.PausedChanged += UpdatePausedUi;
        _controller.SettingsChanged += OnSettingsChanged;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        SetActiveNav(NavOverview);
        FooterMachineName.Text = Environment.MachineName.ToUpperInvariant();
        FooterOS.Text = $"{Environment.OSVersion.VersionString} · {Environment.ProcessorCount} logical processors";
        HeaderSubtitle.Text = $"{Environment.MachineName} · live hardware and operating-system telemetry";
        SyncPickers();
        UpdatePausedUi();
        UpdateViewing();
        if (_controller.SettingsMessage is { } message)
            MessageBox.Show(this, message, "Pulse settings", MessageBoxButton.OK, MessageBoxImage.Information);
        if (_controller.Monitor.Latest is { } latest) Render(latest);
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateViewing();
    private void Window_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateViewing();

    private void UpdateViewing() =>
        _controller.SetViewing(this, IsVisible && WindowState != WindowState.Minimized);

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_controller.IsExiting) return;
        if (_controller.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _controller.Monitor.SnapshotProcessed -= OnSnapshotProcessed;
        _controller.PausedChanged -= UpdatePausedUi;
        _controller.SettingsChanged -= OnSettingsChanged;
        _controller.Exit();
    }

    private void SyncPickers()
    {
        _loaded = false;
        foreach (ComboBoxItem item in RefreshPicker.Items)
            if (item.Tag as string == _controller.Settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture)) RefreshPicker.SelectedItem = item;
        var minutes = (int)_controller.Settings.ChartSpan.TotalMinutes;
        foreach (ComboBoxItem item in WindowPicker.Items)
            if (item.Tag as string == minutes.ToString(CultureInfo.InvariantCulture)) WindowPicker.SelectedItem = item;
        _loaded = true;
    }

    private void OnSettingsChanged()
    {
        _span = _controller.Settings.ChartSpan;
        SyncPickers();
        if (_controller.Monitor.Latest is { } s) Render(s);
    }

    private void UpdatePausedUi()
    {
        var paused = _controller.Paused;
        PausedBadge.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        PauseText.Text = paused ? "Resume collection" : "Pause collection";
        PauseGlyph.Text = paused ? "\uE768" : "\uE769";
        Title = paused ? "Pulse — PAUSED" : "Pulse — PC health";
        if (paused)
        {
            ConnectionStatus.Text = "Paused — nothing is being collected";
            ConnectionDot.Fill = BrushFrom("#F3BD72");
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _controller.TogglePaused();

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tab }) ToolsWindow.ShowTab(_controller, tab, this);
    }

    private void RefreshPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || RefreshPicker.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return;
        var s = _controller.Settings;
        s.RefreshSeconds = seconds;
        _controller.ApplySettings(s);
    }

    private void WindowPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || WindowPicker.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        var s = _controller.Settings;
        s.ChartWindow = value switch { "15" => "15m", "60" => "1h", _ => "1m" };
        _controller.ApplySettings(s);
    }

    public static readonly DependencyProperty ScrollPositionProperty = DependencyProperty.Register(
        nameof(ScrollPosition), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).MainScroll.ScrollToVerticalOffset((double)e.NewValue)));

    public double ScrollPosition
    {
        get => (double)GetValue(ScrollPositionProperty);
        set => SetValue(ScrollPositionProperty, value);
    }

    private void Overview_Click(object sender, RoutedEventArgs e) => ScrollTo(null, NavOverview);
    private void Hardware_Click(object sender, RoutedEventArgs e) => ScrollTo(HardwareSection, NavHardware);
    private void Processes_Click(object sender, RoutedEventArgs e) => ScrollTo(ProcessesSection, NavProcesses);
    private void Network_Click(object sender, RoutedEventArgs e) => ScrollTo(NetworkSection, NavNetwork);

    private void ScrollTo(FrameworkElement? target, Button nav)
    {
        SetActiveNav(nav);
        var offset = target is null
            ? 0
            : target.TransformToAncestor((Visual)MainScroll.Content).Transform(new Point(0, 0)).Y - 8;
        offset = Math.Clamp(offset, 0, Math.Max(0, MainScroll.ScrollableHeight));
        if (_controller.ReduceMotion)
        {
            BeginAnimation(ScrollPositionProperty, null);
            MainScroll.ScrollToVerticalOffset(offset);
            return;
        }
        var animation = new System.Windows.Media.Animation.DoubleAnimation(MainScroll.VerticalOffset, offset,
            TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        BeginAnimation(ScrollPositionProperty, animation);
    }

    private void SetActiveNav(Button active)
    {
        foreach (var b in new[] { NavOverview, NavHardware, NavProcesses, NavNetwork })
        {
            var on = b == active;
            b.Background = on ? new SolidColorBrush(Color.FromRgb(0x1A, 0x29, 0x35)) : Brushes.Transparent;
            b.Foreground = on ? (Brush)FindResource("Cyan") : (Brush)FindResource("TextMuted");
        }
    }

    private void OnSnapshotProcessed(MetricsSnapshot snapshot)
    {
        _pending = snapshot;
        // Coalesce: at most one render is queued; it draws the newest snapshot.
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0 && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                if (_pending is { } s && IsVisible) Render(s);
            }));
    }

    /// <summary>Explains why a value is missing, using the collector's real state and age.</summary>
    private static string Why(MetricsSnapshot s, string collector, string label)
    {
        var st = s.StatusOf(collector);
        if (st is null) return $"{label}: no status yet";
        var age = st.Age(s.TimestampUtc);
        return st.State switch
        {
            CollectorState.WarmingUp => $"{label}: warming up — needs two readings",
            CollectorState.Stale => $"{label}: stale — last good reading {Format.Duration(age ?? TimeSpan.Zero)} ago",
            CollectorState.Unsupported => $"{label}: not supported on this PC",
            CollectorState.AccessDenied => $"{label}: Windows denied access",
            CollectorState.Failing => $"{label}: failing, retrying ({st.ConsecutiveFailures} failures)",
            _ => $"{label}: no valid reading",
        };
    }

    private static string StatsText(IReadOnlyList<HistoryPoint> points, string unit)
    {
        var st = SeriesMath.Stats(points);
        return st is null
            ? "last minute: no valid samples"
            : $"last minute · min {st.Min:0}{unit} · avg {st.Average:0}{unit} · max {st.Max:0}{unit} · {st.Count} samples";
    }

    private void SetChart(TimeChart chart, string id, string? id2, DateTime now)
    {
        chart.From = now - _span;
        chart.To = now;
        chart.Series = _controller.Monitor.History.Query(id, chart.From, now);
        if (id2 is not null) chart.Series2 = _controller.Monitor.History.Query(id2, chart.From, now);
    }

    private void Render(MetricsSnapshot snapshot)
    {
        var nowUtc = snapshot.TimestampUtc;
        UpdatedAt.Text = _controller.Paused ? $"PAUSED · last sample {snapshot.Timestamp:HH:mm:ss}" : $"UPDATED {snapshot.Timestamp:HH:mm:ss}";

        var health = HealthAssessment.Evaluate(snapshot);
        HealthLabel.Text = health.Level switch
        {
            HealthLevel.Warning => "Needs attention",
            HealthLevel.Notice => "Partial coverage",
            HealthLevel.Unknown => "Insufficient data",
            _ => "Systems calm"
        };
        HealthDot.Fill = health.Level switch
        {
            HealthLevel.Warning => BrushFrom("#F18C83"),
            HealthLevel.Notice => BrushFrom("#F3BD72"),
            HealthLevel.Unknown => BrushFrom("#718193"),
            _ => BrushFrom("#66DBAA")
        };
        HealthLabel.ToolTip = health.Message;
        if (!_controller.Paused)
        {
            ConnectionStatus.Text = health.Level switch
            {
                HealthLevel.Unknown => "Insufficient data",
                HealthLevel.Notice => "Live · partial coverage",
                _ => "Live"
            };
            ConnectionDot.Fill = BrushFrom(health.Level is HealthLevel.Unknown or HealthLevel.Notice ? "#F3BD72" : "#66DBAA");
        }
        ConnectionStatus.ToolTip = health.Message;

        var minute = nowUtc - TimeSpan.FromMinutes(1);
        var history = _controller.Monitor.History;

        if (snapshot.CpuPercent is { } cpu)
        {
            CpuValue.Text = cpu.ToString("0", CultureInfo.CurrentCulture);
            CpuMeta.Text = $"{Environment.ProcessorCount} logical processors";
            CpuTrendBadge.Text = "LIVE";
        }
        else
        {
            CpuValue.Text = "—";
            CpuMeta.Text = Why(snapshot, CollectorNames.Cpu, "CPU");
            CpuTrendBadge.Text = "NO DATA";
        }
        CpuVolatilityValue.Text = StatsText(history.Query(MetricIds.Cpu, minute, nowUtc), "%");
        SetChart(CpuChart, MetricIds.Cpu, null, nowUtc);

        if (snapshot.Memory is { } memory)
        {
            MemoryValue.Text = memory.UsedPercent.ToString("0", CultureInfo.CurrentCulture);
            MemoryMeta.Text = $"{Format.Bytes(memory.UsedBytes)} of {Format.Bytes(memory.TotalBytes)} in use";
            MemoryCommitBadge.Text = $"COMMIT {memory.CommitPercent:0}%";
            if (snapshot.Counts is not null)
            {
                CachedValue.Text = Format.Bytes(memory.CachedBytes);
                PagedPoolValue.Text = Format.Bytes(memory.PagedPoolBytes);
                NonPagedPoolValue.Text = Format.Bytes(memory.NonPagedPoolBytes);
                MemoryInsight.Text = $"{Format.Bytes(memory.CommitUsedBytes)} committed of {Format.Bytes(memory.CommitLimitBytes)} limit. Cached pages are reclaimable when applications need RAM.";
            }
            else
            {
                CachedValue.Text = "Not exposed";
                PagedPoolValue.Text = "—";
                NonPagedPoolValue.Text = "—";
                MemoryInsight.Text = $"{Format.Bytes(memory.CommitUsedBytes)} committed of {Format.Bytes(memory.CommitLimitBytes)} limit. Cache and kernel-pool counters are unavailable.";
            }
        }
        else
        {
            MemoryValue.Text = "—";
            MemoryMeta.Text = Why(snapshot, CollectorNames.Memory, "Memory");
            MemoryCommitBadge.Text = "COMMIT —";
            CachedValue.Text = "—";
            PagedPoolValue.Text = "—";
            NonPagedPoolValue.Text = "—";
            MemoryInsight.Text = "Memory counters have no current valid reading.";
        }
        SetChart(MemoryChart, MetricIds.Memory, null, nowUtc);

        RenderThermal(snapshot);
        RenderGpu(snapshot);
        RenderDisk(snapshot);
        RenderNetwork(snapshot);
        RenderProcesses(snapshot);
        RenderSystemCounts(snapshot);
        RenderBattery(snapshot);
        RenderDrives(snapshot.Drives);
        SetChart(ThermalChart, MetricIds.Thermal, null, nowUtc);
        SetChart(GpuChart, MetricIds.Gpu, null, nowUtc);
        SetChart(DiskChart, MetricIds.DiskRead, MetricIds.DiskWrite, nowUtc);
        SetChart(NetworkChart, MetricIds.NetDown, MetricIds.NetUp, nowUtc);
    }

    private void RenderThermal(MetricsSnapshot s)
    {
        var st = s.StatusOf(CollectorNames.Thermal);
        if (s.Thermal is { Zones.Count: > 0 } thermal)
        {
            var hottest = thermal.Zones.Max(z => z.Celsius);
            ThermalValue.Text = $"{hottest:0}°C";
            var names = string.Join(", ", thermal.Zones.Take(2).Select(z => z.Name));
            ThermalDescription.Text = $"Hottest of {thermal.Zones.Count} ACPI thermal zone{(thermal.Zones.Count == 1 ? "" : "s")} · {names} · not a CPU or GPU die temperature";
            ThermalDescription.ToolTip = null;
            ThermalStatus.Text = "ACPI ZONE";
            return;
        }
        ThermalValue.Text = "—";
        ThermalStatus.Text = st?.State switch
        {
            CollectorState.AccessDenied => "ACCESS DENIED",
            CollectorState.Unsupported => "UNAVAILABLE",
            CollectorState.Failing => "ERROR",
            CollectorState.Stale => "STALE",
            _ => "CHECKING"
        };
        ThermalDescription.Text = Why(s, CollectorNames.Thermal, "ACPI thermal zones");
        ThermalDescription.ToolTip = st?.Detail;
    }

    private void RenderGpu(MetricsSnapshot s)
    {
        if (s.Gpu is { } gpu)
        {
            GpuAvailability.Text = $"{gpu.ActiveEngines} BUSY INSTANCES";
            GpuValue.Text = gpu.UtilizationPercent.ToString("0", CultureInfo.CurrentCulture);
            GpuMeta.Text = $"Peak {gpu.Engine} engine · Windows performance counters";
            GpuMeta.ToolTip = null;
            return;
        }
        var st = s.StatusOf(CollectorNames.Gpu);
        GpuAvailability.Text = st?.State switch
        {
            CollectorState.AccessDenied => "ACCESS DENIED",
            CollectorState.Unsupported => "UNAVAILABLE",
            CollectorState.Failing => "ERROR",
            CollectorState.Stale => "STALE",
            _ => "CHECKING"
        };
        GpuValue.Text = "—";
        GpuMeta.Text = Why(s, CollectorNames.Gpu, "GPU engine counters");
        GpuMeta.ToolTip = st?.Detail;
    }

    private void RenderDisk(MetricsSnapshot s)
    {
        if (s.DiskIo is not { } disk)
        {
            DiskRateValue.Text = "— / —";
            DiskRateMeta.Text = Why(s, CollectorNames.DiskIo, "Storage counters");
            DiskQueueMeta.Text = "Queue depth — · peak drive busy —";
            DiskActiveBadge.Text = "— PEAK";
            return;
        }
        DiskRateValue.Text = $"{Format.Rate(disk.ReadBytesPerSecond)} / {Format.Rate(disk.WriteBytesPerSecond)}";
        DiskRateMeta.Text = "Read / write · bytes per second";
        DiskQueueMeta.Text = $"Queue depth {disk.QueueDepth} · peak drive busy {disk.ActivePercent:0}%";
        DiskActiveBadge.Text = $"{disk.ActivePercent:0}% PEAK";
    }

    private void RenderNetwork(MetricsSnapshot s)
    {
        if (s.Network is not { } network)
        {
            NetworkRateValue.Text = "— / —";
            NetworkRateMeta.Text = Why(s, CollectorNames.Network, "Network counters");
            LinkUtilization.Text = "LINK —";
            SessionTransfer.Text = "This session: — received · — sent";
            AdapterRows.Children.Clear();
            return;
        }
        NetworkRateValue.Text = $"{Format.Rate(network.DownBytesPerSecond)} / {Format.Rate(network.UpBytesPerSecond)}";
        NetworkRateMeta.Text = "↓ received  /  ↑ sent";
        var linkCapacity = network.Adapters.Sum(a => Math.Max(0, a.LinkSpeedBitsPerSecond));
        var utilization = linkCapacity > 0
            ? (network.DownBytesPerSecond + network.UpBytesPerSecond) * 8 * 100 / linkCapacity
            : 0;
        LinkUtilization.Text = linkCapacity > 0 ? $"LINK {utilization:0.0}%" : "LINK —";
        SessionTransfer.Text = $"This session: ↓ {Format.Bytes(network.SessionDownBytes)} · ↑ {Format.Bytes(network.SessionUpBytes)}";
        RenderAdapters(network.Adapters);
    }

    private void RenderProcesses(MetricsSnapshot s)
    {
        if (s.Top is not { } top)
        {
            ProcessScanStatus.Text = s.StatusOf(CollectorNames.Processes)?.State is CollectorState.AccessDenied or CollectorState.Failing ? "UNAVAILABLE" : "SCANNING";
            CpuProcessRows.Children.Clear();
            MemoryProcessRows.Children.Clear();
            ProcessChurnValue.Text = "— started · — exited";
            return;
        }

        var age = _controller.Engine.LastProcessObservationUtc is { } t ? s.TimestampUtc - t : (TimeSpan?)null;
        ProcessScanStatus.Text = age is { } a && a > TimeSpan.FromSeconds(15) ? $"SCAN {Format.Duration(a)} OLD" : "THROTTLED SCAN";
        RenderProcessRows(CpuProcessRows, top.ByCpu, u => $"{u.CpuPercent:0.0}%", "No standout process in this scan (or CPU still warming up)");
        RenderProcessRows(MemoryProcessRows, top.ByMemory, u => Format.Bytes(u.WorkingSetBytes), "No process data");
        ProcessChurnValue.Text = $"{top.StartedLastMinute} started · {top.ExitedLastMinute} exited";
    }

    private static void RenderProcessRows(Panel panel, IReadOnlyList<ProcessUsage> processes, Func<ProcessUsage, string> valueFormatter, string empty)
    {
        panel.Children.Clear();
        if (processes.Count == 0)
        {
            panel.Children.Add(MakeText(empty, "#718193", 10));
            return;
        }

        foreach (var process in processes)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = MakeText($"{process.Name}{(process.Instances > 1 ? $"  ×{process.Instances}" : "")}", "#DCE5EC", 10);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(name);
            var value = MakeText(valueFormatter(process), "#9EB0BF", 10);
            value.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            panel.Children.Add(row);
        }
    }

    private void RenderSystemCounts(MetricsSnapshot snapshot)
    {
        UptimeValue.Text = Format.Duration(snapshot.Uptime);
        if (snapshot.Counts is { } counts)
        {
            ProcessCountValue.Text = counts.Processes.ToString("N0", CultureInfo.CurrentCulture);
            ThreadCountValue.Text = counts.Threads.ToString("N0", CultureInfo.CurrentCulture);
            HandleCountValue.Text = counts.Handles.ToString("N0", CultureInfo.CurrentCulture);
        }
        else
        {
            ProcessCountValue.Text = "—";
            ThreadCountValue.Text = "—";
            HandleCountValue.Text = "—";
        }
    }

    private void RenderBattery(MetricsSnapshot s)
    {
        if (s.Battery is not { } battery)
        {
            var st = s.StatusOf(CollectorNames.Battery);
            var noBattery = st?.State is null or CollectorState.Current or CollectorState.Unsupported;
            PowerAvailability.Text = noBattery ? "NO BATTERY REPORTED" : "UNAVAILABLE";
            BatteryValue.Text = noBattery ? "No battery detected" : "Battery state unknown";
            BatteryMeta.Text = noBattery ? "Normal for an AC-powered desktop." : Why(s, CollectorNames.Battery, "Power status");
            return;
        }

        PowerAvailability.Text = battery.OnAc ? "ON AC POWER" : "ON BATTERY";
        BatteryValue.Text = battery.Percent is { } percent ? $"{percent}% charge" : "Battery detected";
        var details = battery.Charging ? "Charging" : battery.OnAc ? "Connected to AC" : "Discharging";
        if (battery.Remaining is { } remaining && !battery.OnAc) details += $" · {Format.Duration(remaining)} remaining";
        BatteryMeta.Text = details;
    }

    private void RenderDrives(IReadOnlyList<DriveSample> drives)
    {
        DriveRows.Children.Clear();
        if (drives.Count == 0)
        {
            DriveRows.Children.Add(MakeText("No ready fixed volumes reported", "#718193", 10));
            return;
        }

        foreach (var drive in drives)
        {
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 7) };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = string.IsNullOrWhiteSpace(drive.Label) ? drive.Name : $"{drive.Name}  {drive.Label}";
            var name = MakeText(label, "#DCE5EC", 10);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            header.Children.Add(name);
            var space = MakeText($"{Format.Bytes(drive.FreeBytes)} free", "#91A2B0", 9);
            Grid.SetColumn(space, 1);
            header.Children.Add(space);
            row.Children.Add(header);
            row.Children.Add(MakeUsageBar(drive.UsedPercent));
            DriveRows.Children.Add(row);
        }
    }

    private void RenderAdapters(IReadOnlyList<AdapterSample> adapters)
    {
        AdapterRows.Children.Clear();
        if (adapters.Count == 0)
        {
            AdapterRows.Children.Add(MakeText("No active physical Ethernet or Wi-Fi adapter", "#718193", 10));
            return;
        }

        foreach (var adapter in adapters)
        {
            var grid = new Grid { Margin = new Thickness(0, 5, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = MakeText(adapter.Name, "#DCE5EC", 10);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            grid.Children.Add(name);
            var rate = MakeText($"↓ {Format.Rate(adapter.DownBytesPerSecond)}   ↑ {Format.Rate(adapter.UpBytesPerSecond)}   ·   {FormatLinkSpeed(adapter.LinkSpeedBitsPerSecond)}", "#91A2B0", 9);
            rate.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(rate, 1);
            grid.Children.Add(rate);
            AdapterRows.Children.Add(grid);
        }
    }

    private static string FormatLinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "link speed —";
        var value = (double)bitsPerSecond;
        var unit = "bit/s";
        foreach (var next in new[] { ("Kbit/s", 1_000L), ("Mbit/s", 1_000_000L), ("Gbit/s", 1_000_000_000L) })
        {
            if (bitsPerSecond < next.Item2) break;
            value = (double)bitsPerSecond / next.Item2;
            unit = next.Item1;
        }
        return $"{value:0.#} {unit}";
    }

    private static FrameworkElement MakeUsageBar(double usedPercent)
    {
        var percent = Math.Clamp(usedPercent, 0, 100);
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 0), ClipToBounds = true };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(percent, 0.01), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - percent, 0.01), GridUnitType.Star) });
        var fill = new Border { Background = BrushFrom("#68D6E4"), CornerRadius = new CornerRadius(2) };
        var remainder = new Border { Background = BrushFrom("#293743"), CornerRadius = new CornerRadius(2) };
        Grid.SetColumn(fill, 0);
        Grid.SetColumn(remainder, 1);
        bar.Children.Add(fill);
        bar.Children.Add(remainder);
        return bar;
    }

    private static TextBlock MakeText(string text, string color, double size) => new()
    {
        Text = text,
        Foreground = BrushFrom(color),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Brush BrushFrom(string color) =>
        new BrushConverter().ConvertFromString(color) as Brush ?? Brushes.White;
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;

namespace Pulse.App;

public partial class MainWindow : Window
{
    private readonly MetricsEngine _engine = new();
    private readonly BoundedHistory<double> _cpuHistory = new();
    private readonly BoundedHistory<double> _memoryHistory = new();
    private readonly BoundedHistory<double> _gpuHistory = new();
    private readonly BoundedHistory<double> _thermalHistory = new();
    private readonly BoundedHistory<double> _diskReadHistory = new();
    private readonly BoundedHistory<double> _diskWriteHistory = new();
    private readonly BoundedHistory<double> _networkDownHistory = new();
    private readonly BoundedHistory<double> _networkUpHistory = new();
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        CpuChart.Values = _cpuHistory;
        MemoryChart.Values = _memoryHistory;
        GpuChart.Values = _gpuHistory;
        ThermalChart.Values = _thermalHistory;
        DiskChart.Values = _diskReadHistory;
        DiskChart.Values2 = _diskWriteHistory;
        NetworkChart.Values = _networkDownHistory;
        NetworkChart.Values2 = _networkUpHistory;
        _engine.SnapshotReady += OnSnapshotReady;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        SetActiveNav(NavOverview);
        FooterMachineName.Text = Environment.MachineName.ToUpperInvariant();
        FooterOS.Text = $"{Environment.OSVersion.VersionString} · {Environment.ProcessorCount} logical processors";
        HeaderSubtitle.Text = $"{Environment.MachineName} · live hardware and operating-system telemetry";
        _engine.Visible = true;
        _engine.Start();
    }

    private void Window_StateChanged(object? sender, EventArgs e) =>
        _engine.Visible = IsVisible && WindowState != WindowState.Minimized;

    private void Window_Closed(object? sender, EventArgs e)
    {
        _engine.SnapshotReady -= OnSnapshotReady;
        _engine.Dispose();
    }

    private void RefreshPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || RefreshPicker.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            _engine.Interval = TimeSpan.FromSeconds(seconds);
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

    private void OnSnapshotReady(MetricsSnapshot snapshot)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => Render(snapshot)));
    }

    private void Render(MetricsSnapshot snapshot)
    {
        UpdatedAt.Text = $"UPDATED {snapshot.Timestamp:HH:mm:ss}";
        ConnectionStatus.Text = "Sampling live";

        var health = HealthAssessment.Evaluate(snapshot);
        HealthLabel.Text = health.Level switch
        {
            HealthLevel.Warning => "Needs attention",
            HealthLevel.Notice => "Under load",
            _ => "Systems calm"
        };
        HealthDot.Fill = health.Level switch
        {
            HealthLevel.Warning => BrushFrom("#F18C83"),
            HealthLevel.Notice => BrushFrom("#F3BD72"),
            _ => BrushFrom("#66DBAA")
        };
        HealthLabel.ToolTip = health.Message;

        if (snapshot.CpuPercent is { } cpu)
        {
            _cpuHistory.Add(cpu);
            CpuValue.Text = cpu.ToString("0", CultureInfo.CurrentCulture);
            CpuMeta.Text = $"{LogicalProcessorLabel()} · σ {StandardDeviation(_cpuHistory, 30):0.0} pp";
            CpuChart.InvalidateVisual();
            CpuVolatilityValue.Text = $"Load variation σ {StandardDeviation(_cpuHistory, 30):0.0} percentage points · last 30 samples";
        }
        else
        {
            CpuValue.Text = "—";
            CpuMeta.Text = "Waiting for a valid CPU interval";
        }

        if (snapshot.Memory is { } memory)
        {
            _memoryHistory.Add(memory.UsedPercent);
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
            MemoryChart.InvalidateVisual();
        }
        else
        {
            MemoryValue.Text = "—";
            MemoryMeta.Text = "Memory counters unavailable";
        }

        RenderHardware(snapshot.Hardware);
        RenderDisk(snapshot.DiskIo);
        RenderNetwork(snapshot.Network);
        RenderProcesses(snapshot.Top);
        RenderSystemCounts(snapshot);
        RenderBattery(snapshot.Battery);
        RenderDrives(snapshot.Drives);

        CpuChart.InvalidateVisual();
        MemoryChart.InvalidateVisual();
    }

    private void RenderHardware(HardwareSample? hardware)
    {
        if (hardware is null)
        {
            ThermalStatus.Text = "CHECKING";
            ThermalValue.Text = "—";
            ThermalDescription.Text = "Windows sensor query is starting.";
            GpuAvailability.Text = "CHECKING";
            GpuValue.Text = "—";
            GpuMeta.Text = "Checking Windows GPU counters…";
            return;
        }

        if (hardware.ThermalReadings.Count == 0)
        {
            _thermalHistory.Clear();
            ThermalChart.InvalidateVisual();
            ThermalValue.Text = hardware.ThermalError is null ? "Not exposed" : "Read error";
            ThermalDescription.Text = hardware.ThermalError is null
                ? "No ACPI thermal zones were returned by this device's firmware."
                : "Windows could not query the ACPI thermal-zone provider.";
            ThermalStatus.Text = hardware.ThermalError is null ? "UNAVAILABLE" : "WMI ERROR";
            ThermalDescription.ToolTip = hardware.ThermalError;
        }
        else
        {
            var hottest = hardware.ThermalReadings.Max(reading => reading.Celsius);
            _thermalHistory.Add(hottest);
            ThermalValue.Text = $"{hottest:0}°C";
            var zoneNames = string.Join(", ", hardware.ThermalReadings.Take(2).Select(reading => reading.Name));
            ThermalDescription.Text = $"{hardware.ThermalReadings.Count} ACPI zone{(hardware.ThermalReadings.Count == 1 ? "" : "s")} · {zoneNames} · highest exposed";
            ThermalDescription.ToolTip = null;
            ThermalStatus.Text = "FIRMWARE";
            ThermalChart.InvalidateVisual();
        }

        if (hardware.Gpu is null)
        {
            _gpuHistory.Clear();
            GpuChart.InvalidateVisual();
            GpuAvailability.Text = hardware.GpuError is null ? "UNAVAILABLE" : "WMI ERROR";
            GpuValue.Text = "—";
            GpuMeta.Text = hardware.GpuError is null
                ? "Windows did not publish GPU engine counters."
                : "Windows could not query GPU engine counters.";
            GpuMeta.ToolTip = hardware.GpuError;
        }
        else
        {
            _gpuHistory.Add(hardware.Gpu.UtilizationPercent);
            GpuAvailability.Text = $"{hardware.Gpu.ActiveEngines} BUSY INSTANCES";
            GpuValue.Text = hardware.Gpu.UtilizationPercent.ToString("0", CultureInfo.CurrentCulture);
            GpuMeta.Text = $"Peak {hardware.Gpu.Engine} engine · Windows performance counters";
            GpuMeta.ToolTip = null;
            GpuChart.InvalidateVisual();
        }
    }

    private void RenderDisk(DiskIoSample? disk)
    {
        if (disk is null)
        {
            DiskRateValue.Text = "— / —";
            DiskRateMeta.Text = "Waiting for storage counters";
            DiskQueueMeta.Text = "Queue depth — · peak drive busy —";
            DiskActiveBadge.Text = "— PEAK";
            return;
        }

        _diskReadHistory.Add(disk.ReadBytesPerSecond);
        _diskWriteHistory.Add(disk.WriteBytesPerSecond);
        DiskRateValue.Text = $"{Format.Rate(disk.ReadBytesPerSecond)} / {Format.Rate(disk.WriteBytesPerSecond)}";
        DiskRateMeta.Text = "Read / write · bytes per second";
        DiskQueueMeta.Text = $"Queue depth {disk.QueueDepth} · peak drive busy {disk.ActivePercent:0}%";
        DiskActiveBadge.Text = $"{disk.ActivePercent:0}% PEAK";
        DiskChart.InvalidateVisual();
    }

    private void RenderNetwork(NetworkSample? network)
    {
        if (network is null)
        {
            NetworkRateValue.Text = "— / —";
            NetworkRateMeta.Text = "Network counters unavailable";
            LinkUtilization.Text = "LINK —";
            SessionTransfer.Text = "This session: — received · — sent";
            AdapterRows.Children.Clear();
            return;
        }

        _networkDownHistory.Add(network.DownBytesPerSecond);
        _networkUpHistory.Add(network.UpBytesPerSecond);
        NetworkRateValue.Text = $"{Format.Rate(network.DownBytesPerSecond)} / {Format.Rate(network.UpBytesPerSecond)}";
        NetworkRateMeta.Text = "↓ received  /  ↑ sent";
        var linkCapacity = network.Adapters.Sum(adapter => Math.Max(0, adapter.LinkSpeedBitsPerSecond));
        var utilization = linkCapacity > 0
            ? (network.DownBytesPerSecond + network.UpBytesPerSecond) * 8 * 100 / linkCapacity
            : 0;
        LinkUtilization.Text = linkCapacity > 0 ? $"LINK {utilization:0.0}%" : "LINK —";
        SessionTransfer.Text = $"This session: ↓ {Format.Bytes(network.SessionDownBytes)} · ↑ {Format.Bytes(network.SessionUpBytes)}";
        NetworkChart.InvalidateVisual();
        RenderAdapters(network.Adapters);
    }

    private void RenderProcesses(TopProcesses? top)
    {
        if (top is null)
        {
            ProcessScanStatus.Text = "SCANNING";
            CpuProcessRows.Children.Clear();
            MemoryProcessRows.Children.Clear();
            return;
        }

        ProcessScanStatus.Text = "5-SECOND SCAN";
        RenderProcessRows(CpuProcessRows, top.ByCpu, usage => $"{usage.CpuPercent:0.0}%");
        RenderProcessRows(MemoryProcessRows, top.ByMemory, usage => Format.Bytes(usage.WorkingSetBytes));
        ProcessChurnValue.Text = $"{top.StartedLastMinute} started · {top.ExitedLastMinute} exited";
    }

    private void RenderProcessRows(
        Panel panel,
        IReadOnlyList<ProcessUsage> processes,
        Func<ProcessUsage, string> valueFormatter)
    {
        panel.Children.Clear();
        if (processes.Count == 0)
        {
            panel.Children.Add(MakeText("No standout process in this sample", "#718193", 10));
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

    private void RenderBattery(BatterySample? battery)
    {
        if (battery is null)
        {
            PowerAvailability.Text = "DESKTOP / NO SENSOR";
            BatteryValue.Text = "No battery detected";
            BatteryMeta.Text = "This is normal for an AC-powered desktop.";
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

    private static string LogicalProcessorLabel() =>
        $"{Environment.ProcessorCount} logical processors";

    private static double StandardDeviation(BoundedHistory<double> history, int sampleCount)
    {
        var start = Math.Max(0, history.Count - sampleCount);
        var count = history.Count - start;
        if (count < 2) return 0;

        var mean = 0.0;
        for (var i = start; i < history.Count; i++) mean += history[i];
        mean /= count;

        var sumSquares = 0.0;
        for (var i = start; i < history.Count; i++)
        {
            var delta = history[i] - mean;
            sumSquares += delta * delta;
        }
        return Math.Sqrt(sumSquares / count);
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

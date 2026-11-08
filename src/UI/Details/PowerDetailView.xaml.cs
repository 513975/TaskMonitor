using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>Power detail panel bound to the typed power snapshot.</summary>
    public partial class PowerDetailView : UserControl, IDetailView
    {
        private readonly ChartTipHost _tip = new ChartTipHost();
        private readonly ProcessIconCache _icons = new ProcessIconCache();
        private int _intervalMs = 1000;
        private IReadOnlyList<double?> _systemHistory;
        private IReadOnlyList<double?> _cpuPackageHistory;
        private IReadOnlyList<double?> _batteryHistory;
        private PowerValueKind _systemKind;
        private PowerValueKind _cpuPackageKind;

        public ContentControl PinSlot => PinSlotHost;

        public PowerDetailView(bool dark)
        {
            InitializeComponent();
            ApplyTheme(dark);

            _tip.Attach(PowerChart, PowerChart.HitTest, HistoryTip, HistoryTipText, i =>
            {
                double? system = ValueAt(_systemHistory, i);
                double? cpu = ValueAt(_cpuPackageHistory, i);
                double? battery = ValueAt(_batteryHistory, i);
                string left = system.HasValue ? $"整机/组件 {FormatWatts(system, _systemKind)}" : "整机/组件 —";
                string right = cpu.HasValue ? $"CPU 封装 {FormatWatts(cpu, _cpuPackageKind)}" : "CPU 封装 —";
                string third = battery.HasValue ? $"电池 {FormatBatteryFlow(battery.Value)}" : "电池 —";
                return $"{HistoryTimeFormatter.Ago(59 - i, _intervalMs)} · {left} · {right} · {third}";
            });
        }

        void IDetailView.Refresh(SystemSnapshot snapshot)
        {
            _intervalMs = Math.Max(1, snapshot.SampleIntervalMs);
            PowerSnapshot power = snapshot.Power ?? PowerSnapshot.Empty;
            _systemKind = power.SystemKind;
            _cpuPackageKind = power.CpuPackageKind;
            if (power.SystemWatts.HasValue)
            {
                SystemHeaderText.Text = FormatWatts(power.SystemWatts, power.SystemKind);
                SystemHeaderLabelText.Text = power.SystemKind == PowerValueKind.Estimated
                    ? "电池端整机功耗估算"
                    : "整机功率";
            }
            else if (power.BatteryState == BatteryFlowState.Charging && power.BatteryPowerWatts.HasValue)
            {
                SystemHeaderText.Text = $"+{power.BatteryPowerWatts.Value:F1} W";
                SystemHeaderLabelText.Text = "电池充电功率 · 不代表整机耗电";
            }
            else
            {
                SystemHeaderText.Text = "—";
                SystemHeaderLabelText.Text = "整机功率不可用";
            }
            CpuPackageValueRun.Text = FormatWatts(power.CpuPackageWatts, power.CpuPackageKind);
            BatteryStatusRun.Text = FormatBatteryStatus(power);
            bool usesBatteryForSystemEstimate = power.SystemKind == PowerValueKind.Estimated
                && power.BatteryState == BatteryFlowState.Discharging
                && power.BatteryPowerWatts.HasValue;
            bool hasBatteryDischargeEstimate = usesBatteryForSystemEstimate;
            bool needsAvailabilityNotice = power.SystemKind == PowerValueKind.Estimated
                || (!power.SystemWatts.HasValue && !hasBatteryDischargeEstimate)
                || (power.BatteryState == BatteryFlowState.Charging && !power.SystemWatts.HasValue);
            PowerUnavailableNotice.Visibility = needsAvailabilityNotice ? Visibility.Visible : Visibility.Collapsed;
            PowerUnavailableText.Text = power.AvailabilityMessage;

            string source = !string.IsNullOrWhiteSpace(power.SystemSource)
                ? power.SystemSource : power.BatterySource;
            string sourceKind = power.SystemWatts.HasValue
                ? KindText(power.SystemKind)
                : power.BatteryPowerWatts.HasValue ? "Windows 报告" : "不可用";
            SystemSourceRun.Text = JoinSource(source, sourceKind);
            SystemAverageText.Text = FormatWatts(power.SystemAverageWatts, power.SystemKind);
            SystemPeakText.Text = FormatWatts(power.SystemPeakWatts, power.SystemKind);
            EnergyText.Text = power.SystemEnergyWh.HasValue ? $"{power.SystemEnergyWh.Value:F2} Wh" : "—";
            BatteryAverageText.Text = FormatWatts(power.BatteryAverageWatts, PowerValueKind.Measured);
            BatteryPeakText.Text = FormatWatts(power.BatteryPeakWatts, PowerValueKind.Measured);
            BatteryEnergyText.Text = power.BatteryEnergyWh.HasValue ? $"{power.BatteryEnergyWh.Value:F2} Wh" : "—";
            BatteryAverageLabelText.Text = power.BatteryState == BatteryFlowState.Charging
                ? "平均充电功率" : power.BatteryState == BatteryFlowState.Discharging
                    ? "平均放电功率" : "电池平均功率";
            BatteryPeakLabelText.Text = power.BatteryState == BatteryFlowState.Charging
                ? "充电峰值" : power.BatteryState == BatteryFlowState.Discharging
                    ? "放电峰值" : "电池峰值";
            BatteryEnergyLabelText.Text = power.BatteryState == BatteryFlowState.Charging
                ? "充电段累计" : power.BatteryState == BatteryFlowState.Discharging
                    ? "放电段累计" : "电池累计";
            SystemEnergyLabelText.Text = power.SystemKind == PowerValueKind.Estimated
                ? "估算累计能耗" : "累计耗能";

            _systemHistory = PadHistory(AsReadOnly(power.SystemHistory));
            _cpuPackageHistory = PadHistory(AsReadOnly(power.CpuPackageHistory));
            _batteryHistory = PadHistory(AsReadOnly(power.BatteryHistory));
            // Battery discharge is already the system estimate on battery power; draw it
            // once under the system series instead of stacking an identical battery line.
            PowerChart.SystemValues = power.SystemWatts.HasValue
                ? _systemHistory : PadHistory(Array.Empty<double?>());
            PowerChart.CpuPackageValues = _cpuPackageHistory;
            PowerChart.BatteryDischargeValues = usesBatteryForSystemEstimate
                ? PadHistory(Array.Empty<double?>())
                : PadHistory(AsReadOnly(power.BatteryDischargeHistory));
            PowerChart.BatteryChargeValues = PadHistory(AsReadOnly(power.BatteryChargeHistory));
            PowerChart.HistoryDurationSeconds = (int)Math.Ceiling(59 * _intervalMs / 1000.0);
            SystemLegendText.Text = power.SystemKind == PowerValueKind.Measured
                ? "整机实测" : power.SystemKind == PowerValueKind.Estimated
                    ? "整机估算" : "整机功率计";
            SystemLegendDot.Visibility = power.SystemWatts.HasValue
                ? Visibility.Visible : Visibility.Collapsed;
            SystemLegendText.Visibility = SystemLegendDot.Visibility;
            BatteryLegendPanel.Visibility = usesBatteryForSystemEstimate
                ? Visibility.Collapsed : Visibility.Visible;
            _tip.Refresh();

            var components = new List<PowerRow>();
            foreach (PowerComponentReading component in power.Components ?? new List<PowerComponentReading>())
                components.Add(new PowerRow
                {
                    Name = component.Name,
                    WattsText = FormatWatts(component.Watts, component.Kind),
                    StatusText = KindText(component.Kind),
                    Source = component.Source,
                });
            NoComponentsNotice.Visibility = components.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (components.Count == 0)
                NoComponentsNotice.Text = power.AvailabilityMessage;
            ComponentList.ItemsSource = components;
            var processRows = new List<PowerRow>();
            foreach (ProcessPowerReading process in power.TopProcesses ?? new List<ProcessPowerReading>())
                processRows.Add(new PowerRow
                {
                    Name = process.Name,
                    WattsText = FormatWatts(process.Watts, process.Kind),
                    StatusText = KindText(process.Kind),
                    ExePath = process.ExePath,
                    Icon = _icons.Resolve(process.ExePath),
                });
            NoProcessesNotice.Visibility = processRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ProcessList.ItemsSource = processRows;
        }

        public void ApplyTheme(bool dark)
        {
            HistoryTipBorder.Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

            PowerChart.SystemColor = ChartPalette.Accent;
            PowerChart.CpuPackageColor = Color.FromRgb(0xF7, 0x63, 0x0C);
            PowerChart.BatteryColor = Color.FromRgb(0x10, 0xB9, 0x81);
            PowerChart.BatteryChargeColor = Color.FromRgb(0x8B, 0x5C, 0xF6);
            PowerChart.GridColor = ChartPalette.Grid;
            PowerChart.FrameColor = ChartPalette.FaintBase(0x66);
            SystemLegendDot.Fill = new SolidColorBrush(ChartPalette.Accent);
            CpuLegendDot.Fill = new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C));
            BatteryLegendDot.Fill = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            BatteryChargeLegendDot.Fill = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
        }

        private static IReadOnlyList<double?> AsReadOnly(double?[] values)
            => values ?? Array.Empty<double?>();

        private static IReadOnlyList<double?> PadHistory(IReadOnlyList<double?> values)
        {
            const int capacity = 60;
            var padded = new double?[capacity];
            int count = values?.Count ?? 0;
            for (int i = 0; i < capacity; i++)
                padded[i] = i < capacity - count ? (double?)null : values[i - (capacity - count)];
            return padded;
        }

        private static double? ValueAt(IReadOnlyList<double?> values, int index)
            => values != null && index >= 0 && index < values.Count ? values[index] : (double?)null;

        private static bool IsUsable(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

        private static string KindText(PowerValueKind kind)
            => kind == PowerValueKind.Measured ? "实测"
             : kind == PowerValueKind.Estimated ? "估算" : "不可用";

        private static string JoinSource(string source, string kind)
        {
            if (string.IsNullOrWhiteSpace(source)) return string.IsNullOrWhiteSpace(kind) ? "—" : kind;
            if (string.IsNullOrWhiteSpace(kind)) return source;
            return $"{source} · {kind}";
        }

        private static string FormatWatts(double? watts, PowerValueKind kind)
            => watts.HasValue && IsUsable(watts.Value)
                ? $"{(kind == PowerValueKind.Estimated ? "~" : "")}{watts.Value:F1} W" : "—";

        private static string FormatBatteryFlow(double signedWatts)
            => signedWatts < 0
                ? $"充电 {Math.Abs(signedWatts):F1} W"
                : signedWatts > 0
                    ? $"放电 {signedWatts:F1} W"
                    : "未充放电";

        private static string FormatBatteryStatus(PowerSnapshot power)
        {
            if (power == null) return "—";
            if (power.BatteryState == BatteryFlowState.Charging)
                return power.BatteryPowerWatts.HasValue ? $"充电 {power.BatteryPowerWatts.Value:F1} W" : "充电";
            if (power.BatteryState == BatteryFlowState.Discharging)
                return power.BatteryPowerWatts.HasValue ? $"放电 {power.BatteryPowerWatts.Value:F1} W" : "放电";
            if (power.BatteryState == BatteryFlowState.Idle) return "未充放电";
            return "不可用";
        }

        private sealed class PowerRow
        {
            public string Name { get; set; }
            public string WattsText { get; set; }
            public string StatusText { get; set; }
            public string Source { get; set; }
            public string ExePath { get; set; }
            public System.Windows.Media.ImageSource Icon { get; set; }
        }
    }
}

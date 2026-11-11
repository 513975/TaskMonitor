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
        private int _intervalMs = 1000;
        private IReadOnlyList<double?> _cpuPackageHistory;
        private IReadOnlyList<double?> _gpuPowerHistory;
        private IReadOnlyList<double?> _batteryHistory;

        public ContentControl PinSlot => PinSlotHost;

        public PowerDetailView(bool dark)
        {
            InitializeComponent();
            ApplyTheme(dark);

            _tip.Attach(PowerChart, PowerChart.HitTest, HistoryTip, HistoryTipText, i =>
            {
                double? cpu = ValueAt(_cpuPackageHistory, i);
                double? gpu = ValueAt(_gpuPowerHistory, i);
                double? battery = ValueAt(_batteryHistory, i);
                string left = cpu.HasValue ? $"CPU 封装 {FormatWatts(cpu)}" : "CPU 封装 —";
                string gpuText = gpu.HasValue ? $"GPU 合计 {FormatWatts(gpu)}" : "GPU 合计 —";
                string batteryText = battery.HasValue
                    ? $"电池 {FormatBatteryFlow(battery.Value)}" : "电池 —";
                return $"{HistoryTimeFormatter.Ago(59 - i, _intervalMs)} · {left} · {gpuText} · {batteryText}";
            });
        }

        void IDetailView.Refresh(SystemSnapshot snapshot)
        {
            _intervalMs = Math.Max(1, snapshot.SampleIntervalMs);
            PowerSnapshot power = snapshot.Power ?? PowerSnapshot.Empty;
            CoreTotalHeaderText.Text = FormatWatts(power.CoreTotalWatts);
            CoreTotalHeaderLabelText.Text = "核心总功耗";
            CoreTotalHeaderText.ToolTip = "确认仅核显时为 CPU Package；存在独显时为 CPU Package + 完整实测的 NVML 独显功率。不是整机功耗";
            CpuInputWattsText.Text = FormatWatts(power.CpuPackageWatts);
            CpuInputStatusText.Text = KindText(power.CpuPackageKind);
            string packageTooltip = power.CpuPackageSampleTimeUtc == default(DateTime)
                ? power.CpuPackageSource
                : $"{power.CpuPackageSource}\n测量时间：{power.CpuPackageSampleTimeUtc.ToLocalTime():HH:mm:ss}";
            if (power.CpuPackageSource.IndexOf("Intel RAPL Package", StringComparison.OrdinalIgnoreCase) >= 0)
                packageTooltip += "\nPackage 是封装域；PP0/IA 是较窄的核心域，两者范围不同，不应相加。";
            CpuInputWattsText.ToolTip = packageTooltip;
            GpuInputWattsText.Text = FormatWatts(power.GpuPowerWatts);
            GpuInputStatusText.Text = power.GpuPowerWatts.HasValue
                ? $"实测 · {power.GpuPowerReadings.Length} 块"
                : "不可用";
            GpuInputWattsText.ToolTip = power.GpuPowerAvailability;
            BatteryStatusRun.Text = FormatBatteryStatus(power);
            BatteryStatusTextBlock.ToolTip = string.IsNullOrWhiteSpace(power.BatterySource)
                ? null : power.SampleTimeUtc == default(DateTime)
                    ? power.BatterySource
                    : $"{power.BatterySource}\n更新时间：{power.SampleTimeUtc.ToLocalTime():HH:mm:ss}";
            PowerUnavailableNotice.Visibility = power.CpuPackageWatts.HasValue
                ? Visibility.Collapsed : Visibility.Visible;
            PowerUnavailableText.Text = power.AvailabilityMessage;

            CpuSourceRun.Text = JoinSource(power.CpuPackageSource, KindText(power.CpuPackageKind));
            CpuPackagePeakText.Text = FormatWatts(power.CpuPackagePeakWatts);
            string peakWindow = $"最近最多 60 个采样点（当前间隔下约 {Math.Ceiling(59 * _intervalMs / 1000.0):F0} 秒）";
            CpuPackagePeakText.ToolTip = peakWindow + "内的 CPU Package 峰值。";
            GpuPowerPeakText.Text = FormatWatts(power.GpuPowerPeakWatts);
            GpuPowerPeakText.ToolTip = peakWindow + "内 NVML GPU 汇总峰值；缺测样本不按 0 计算。";
            CoreTotalEnergyText.Text = power.CoreTotalEnergyWh.HasValue
                ? $"{power.CoreTotalEnergyWh.Value:F2} Wh" : "—";
            CoreTotalEnergyText.ToolTip = "本次运行中对完整、新鲜的核心总功率积分；功耗来源缺测时暂停累计，不跨缺口估算。";
            BatteryAverageText.Text = FormatWatts(power.BatteryAverageWatts);
            BatteryPeakText.Text = FormatWatts(power.BatteryPeakWatts);
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
            _cpuPackageHistory = PadHistory(AsReadOnly(power.CpuPackageHistory));
            _gpuPowerHistory = PadHistory(AsReadOnly(power.GpuPowerHistory));
            _batteryHistory = PadHistory(AsReadOnly(power.BatteryHistory));
            PowerChart.CpuPackageValues = _cpuPackageHistory;
            PowerChart.GpuPowerValues = _gpuPowerHistory;
            PowerChart.BatteryDischargeValues = PadHistory(AsReadOnly(power.BatteryDischargeHistory));
            PowerChart.BatteryChargeValues = PadHistory(AsReadOnly(power.BatteryChargeHistory));
            _tip.Refresh();

            var components = new List<PowerRow>();
            foreach (PowerComponentReading component in power.Components ?? new List<PowerComponentReading>())
                components.Add(new PowerRow
                {
                    Name = component.Name,
                    WattsText = FormatWatts(component.Watts),
                    StatusText = KindText(component.Kind),
                    Source = component.Source,
                });
            NoComponentsNotice.Visibility = components.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (components.Count == 0)
                NoComponentsNotice.Text = power.CpuPackageWatts.HasValue
                    ? "当前处理器未提供额外的 RAPL 功耗域"
                    : power.AvailabilityMessage;
            ComponentList.ItemsSource = components;

            var gpuRows = new List<GpuPowerRow>();
            foreach (GpuPowerReading reading in power.GpuPowerReadings ?? Array.Empty<GpuPowerReading>())
                gpuRows.Add(new GpuPowerRow
                {
                    Name = reading.Name,
                    WattsText = FormatWatts(reading.Watts),
                    StatusText = reading.Status,
                    DeviceId = reading.DeviceId,
                    Source = reading.Source,
                    Tooltip = $"{reading.DeviceId}\n{reading.Source}\n更新时间：{reading.SampleTimeUtc.ToLocalTime():HH:mm:ss}",
                });
            NoGpuPowerNotice.Visibility = gpuRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoGpuPowerNotice.Text = power.GpuPowerAvailability;
            GpuPowerList.ItemsSource = gpuRows;
        }

        public void ApplyTheme(bool dark)
        {
            HistoryTipBorder.Background = dark
                ? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
                : new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

            PowerChart.CpuPackageColor = Color.FromRgb(0xF7, 0x63, 0x0C);
            PowerChart.GpuPowerColor = Color.FromRgb(0x0E, 0xA5, 0xE9);
            PowerChart.BatteryColor = Color.FromRgb(0x10, 0xB9, 0x81);
            PowerChart.BatteryChargeColor = Color.FromRgb(0x8B, 0x5C, 0xF6);
            PowerChart.GridColor = ChartPalette.Grid;
            PowerChart.FrameColor = ChartPalette.FaintBase(0x66);
            CpuLegendDot.Fill = new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C));
            GpuLegendDot.Fill = new SolidColorBrush(Color.FromRgb(0x0E, 0xA5, 0xE9));
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
             : kind == PowerValueKind.Stale ? "暂时沿用上次读数"
             : "不可用";

        private static string JoinSource(string source, string kind)
        {
            if (string.IsNullOrWhiteSpace(source)) return string.IsNullOrWhiteSpace(kind) ? "—" : kind;
            if (string.IsNullOrWhiteSpace(kind)) return source;
            return $"{source} · {kind}";
        }

        private static string FormatWatts(double? watts)
            => watts.HasValue && IsUsable(watts.Value)
                ? $"{watts.Value:F1} W" : "—";

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
        }

        private sealed class GpuPowerRow
        {
            public string Name { get; set; }
            public string WattsText { get; set; }
            public string StatusText { get; set; }
            public string DeviceId { get; set; }
            public string Source { get; set; }
            public string Tooltip { get; set; }
        }
    }
}

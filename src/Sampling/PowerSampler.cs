using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace task_monitor
{
    internal enum PowerValueKind
    {
        Unavailable,
        Measured,
        Estimated,
    }

    internal enum BatteryFlowState
    {
        Unavailable,
        Idle,
        Charging,
        Discharging,
    }

    internal sealed class PowerComponentReading
    {
        public string Name { get; set; }
        public double? Watts { get; set; }
        public string Source { get; set; }
        public PowerValueKind Kind { get; set; }
    }

    internal sealed class ProcessPowerReading
    {
        public string Name { get; set; }
        public string ExePath { get; set; }
        public double? Watts { get; set; }
        public PowerValueKind Kind { get; set; }
    }

    /// <summary>
    /// Detached hardware readings. The sampler reads Windows' built-in Energy Meter
    /// performance counters (EMI/RAPL) and aggregate battery status API; it never
    /// depends on another monitoring app.
    /// </summary>
    internal sealed class PowerSensorFrame
    {
        public double? CpuPackageWatts;
        public string CpuPackageSource;
        public double? CpuCoreDomainWatts;
        public double? DirectSystemWatts;
        public string DirectSystemSource;
        public bool AcOnLine;
        public double? BatteryPowerWatts;
        public BatteryFlowState BatteryState;
        public string BatterySource;
        public List<PowerComponentReading> Components = new List<PowerComponentReading>();
        public string AvailabilityMessage = "等待 Windows 硬件功率传感器";

        public static readonly PowerSensorFrame Empty = new PowerSensorFrame
        {
            AvailabilityMessage = "等待 Windows Energy Meter / RAPL 功率数据",
        };
    }

    internal sealed class PowerSnapshot
    {
        public double? SystemWatts;
        public PowerValueKind SystemKind;
        public string SystemSource = "";
        public double? CpuPackageWatts;
        public PowerValueKind CpuPackageKind;
        public string AvailabilityMessage = "";
        public double? SystemAverageWatts;
        public double? SystemPeakWatts;
        public double? SystemEnergyWh;
        public double?[] SystemHistory = Array.Empty<double?>();
        public double?[] CpuPackageHistory = Array.Empty<double?>();
        public double? BatteryPowerWatts;
        public BatteryFlowState BatteryState;
        public string BatterySource = "";
        public double? BatteryAverageWatts;
        public double? BatteryPeakWatts;
        public double? BatteryEnergyWh;
        public double?[] BatteryHistory = Array.Empty<double?>();
        public double?[] BatteryChargeHistory = Array.Empty<double?>();
        public double?[] BatteryDischargeHistory = Array.Empty<double?>();
        public double?[] CoreWatts = Array.Empty<double?>();
        public PowerValueKind[] CoreKinds = Array.Empty<PowerValueKind>();
        public List<PowerComponentReading> Components = new List<PowerComponentReading>();
        public List<ProcessPowerReading> TopProcesses = new List<ProcessPowerReading>();

        public static readonly PowerSnapshot Empty = new PowerSnapshot();
    }

    /// <summary>
    /// Reads Windows Energy Meter / RAPL counters and the OS aggregate battery rate.
    /// On Intel systems, Windows publishes RAPL domains in milliwatts. The Power Meter
    /// total is used only when Windows reports a nonzero system-wide meter. If that is
    /// unavailable, battery discharge is exposed as an estimated battery-side whole-system
    /// value. While AC is offline, that battery value takes priority even if a system-wide
    /// meter is also present. CPU package power is never substituted for total system power.
    /// </summary>
    internal sealed class HardwarePowerSensorSampler
    {
        private const int PollIntervalMs = 1000;
        private const int RetryIntervalMs = 10000;
        private const int StopJoinMs = 1500;
        private const double MaxPowerWatts = 2000;

        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private PowerSensorFrame _latest = PowerSensorFrame.Empty;
        private volatile bool _stopping;
        private volatile bool _enabled;
        private string _lastStatus;
        private long _retryAfter;
        private readonly List<RaplCounter> _raplCounters = new List<RaplCounter>();
        private PerformanceCounter _systemPowerCounter;

        public PowerSensorFrame Latest => Volatile.Read(ref _latest);

        public HardwarePowerSensorSampler()
        {
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "HardwarePowerSensorSampler",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
                Publish(PowerSensorFrame.Empty);
            _wake.Set();
        }

        private void PollLoop()
        {
            try
            {
                while (!_stopping)
                {
                    if (!_enabled)
                    {
                        ReleaseCounters();
                        _wake.WaitOne(PollIntervalMs);
                        continue;
                    }

                    long start = Stopwatch.GetTimestamp();
                    PowerSensorFrame frame;
                    try
                    {
                        if (_raplCounters.Count == 0 && _systemPowerCounter == null
                            && Stopwatch.GetTimestamp() >= _retryAfter)
                            OpenCounters();

                        frame = ReadFrame();
                    }
                    catch (Exception ex)
                    {
                        ReleaseCounters();
                        _retryAfter = Stopwatch.GetTimestamp()
                            + (long)(RetryIntervalMs / 1000.0 * Stopwatch.Frequency);
                        frame = FailedFrame("Windows Energy Meter 读取失败");
                        LogTransition(frame.AvailabilityMessage, "读取 Windows 硬件功率传感器失败", ex);
                    }

                    Publish(frame);
                    int elapsed = (int)((Stopwatch.GetTimestamp() - start) * 1000L / Stopwatch.Frequency);
                    _wake.WaitOne(Math.Max(1, PollIntervalMs - elapsed));
                }
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("hardware-power-sampler", "硬件功率采样线程异常退出", ex);
            }
            finally
            {
                ReleaseCounters();
                Volatile.Write(ref _latest, PowerSensorFrame.Empty);
            }
        }

        private void OpenCounters()
        {
            ReleaseCounters();
            try
            {
                if (PerformanceCounterCategory.Exists("Energy Meter"))
                {
                    var energyCategory = new PerformanceCounterCategory("Energy Meter");
                    foreach (string instance in energyCategory.GetInstanceNames())
                    {
                        if (!TryParseRaplInstance(instance, out string domain)) continue;

                        PerformanceCounter counter = null;
                        try
                        {
                            counter = new PerformanceCounter("Energy Meter", "Power", instance, true);
                            // Prime the counter's interval baseline; its first NextValue is expected
                            // to be zero, and the following sample contains the current mW value.
                            counter.NextValue();
                            _raplCounters.Add(new RaplCounter(domain, counter));
                            counter = null;
                        }
                        finally
                        {
                            counter?.Dispose();
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // A machine without the Windows Energy Meter provider can still expose
                // the separate system-level Power Meter category below.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Counter registration is machine-specific; keep the sampler alive.
            }
            try
            {
                if (PerformanceCounterCategory.Exists("Power Meter")
                    && PerformanceCounterCategory.InstanceExists("_Total", "Power Meter"))
                {
                    _systemPowerCounter = new PerformanceCounter("Power Meter", "Power", "_Total", true);
                    _systemPowerCounter.NextValue();
                }
            }
            catch (Exception)
            {
                _systemPowerCounter?.Dispose();
                _systemPowerCounter = null;
            }

            if (_raplCounters.Count == 0 && _systemPowerCounter == null)
                _retryAfter = Stopwatch.GetTimestamp()
                    + (long)(RetryIntervalMs / 1000.0 * Stopwatch.Frequency);
        }

        private PowerSensorFrame ReadFrame()
        {
            var frame = new PowerSensorFrame();
            var domainValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double packageTotal = 0;
            bool hasPackage = false;
            double coreDomainTotal = 0;
            bool hasCoreDomain = false;

            foreach (RaplCounter sensor in _raplCounters)
            {
                double? watts = ReadCounterWatts(sensor.Counter);
                if (!watts.HasValue) continue;

                if (string.Equals(sensor.Domain, "PKG", StringComparison.OrdinalIgnoreCase))
                {
                    packageTotal += watts.Value;
                    hasPackage = true;
                    continue;
                }

                if (string.Equals(sensor.Domain, "PP0", StringComparison.OrdinalIgnoreCase))
                {
                    coreDomainTotal += watts.Value;
                    hasCoreDomain = true;
                }

                string domainKey = sensor.Domain.ToUpperInvariant();
                domainValues[domainKey] = (domainValues.TryGetValue(domainKey, out double previous) ? previous : 0)
                    + watts.Value;
            }

            if (hasPackage)
            {
                frame.CpuPackageWatts = packageTotal;
                frame.CpuPackageSource = "Windows Energy Meter / Intel RAPL Package";
                frame.Components.Add(MeasuredComponent("CPU 封装（RAPL Package）", packageTotal,
                    frame.CpuPackageSource));
            }

            if (hasCoreDomain)
                frame.CpuCoreDomainWatts = coreDomainTotal;

            foreach (KeyValuePair<string, double> domain in domainValues)
            {
                string name = DomainDisplayName(domain.Key);
                frame.Components.Add(MeasuredComponent(name, domain.Value,
                    "Windows Energy Meter / Intel RAPL " + domain.Key));
            }

            double? systemMeter = ReadCounterWatts(_systemPowerCounter);
            if (systemMeter.HasValue)
            {
                frame.DirectSystemWatts = systemMeter.Value;
                frame.DirectSystemSource = "Windows Power Meter / 系统级功率计";
                frame.Components.Insert(0, MeasuredComponent("系统级功率计", systemMeter.Value,
                    frame.DirectSystemSource));
            }

            ReadBattery(frame);
            UpdateAvailability(frame);
            return frame;
        }

        private static void ReadBattery(PowerSensorFrame frame)
        {
            if (!BatteryPowerInterop.TryRead(out BatteryPowerInterop.State battery))
                return;

            frame.AcOnLine = battery.AcOnLine != 0;
            if (battery.BatteryPresent == 0)
            {
                frame.BatterySource = "Windows Battery API · 未检测到电池";
                return;
            }

            frame.BatterySource = "Windows Battery API / CallNtPowerInformation";
            // Native Rate is ULONG, but providers use the high bit as a signed
            // discharge value. 0x80000000 is the documented unknown sentinel.
            if (battery.Rate == 0x80000000U)
            {
                if (battery.Charging != 0 && battery.Discharging == 0)
                    frame.BatteryState = BatteryFlowState.Charging;
                else if (battery.Discharging != 0 && battery.Charging == 0)
                    frame.BatteryState = BatteryFlowState.Discharging;
                return;
            }

            int signedRate = unchecked((int)battery.Rate);
            if (signedRate == 0)
            {
                // Zero is a valid no-flow state only when the aggregate flags agree.
                if (battery.Charging != 0 && battery.Discharging == 0)
                    frame.BatteryState = BatteryFlowState.Charging;
                else if (battery.Discharging != 0 && battery.Charging == 0)
                    frame.BatteryState = BatteryFlowState.Discharging;
                else if (battery.Charging == 0 && battery.Discharging == 0)
                    frame.BatteryState = BatteryFlowState.Idle;
                return;
            }

            bool isCharging = signedRate > 0;
            // Rate's sign carries direction. If the status flags contradict it, wait for
            // a consistent sample rather than publishing a misleading direction.
            if ((isCharging && battery.Discharging != 0 && battery.Charging == 0)
                || (!isCharging && battery.Charging != 0 && battery.Discharging == 0))
                return;

            double watts = Math.Abs((double)signedRate) / 1000.0;
            frame.BatteryState = isCharging
                ? BatteryFlowState.Charging
                : BatteryFlowState.Discharging;
            if (watts <= 0 || watts > MaxPowerWatts)
                return;

            frame.BatteryPowerWatts = watts;
            string flow = isCharging ? "充电" : "放电";
            frame.Components.Add(MeasuredComponent("电池" + flow + "功率", watts,
                frame.BatterySource));
        }

        private static void UpdateAvailability(PowerSensorFrame frame)
        {
            if (!frame.AcOnLine
                && frame.BatteryState == BatteryFlowState.Discharging
                && frame.BatteryPowerWatts.HasValue)
            {
                frame.AvailabilityMessage = frame.DirectSystemWatts.HasValue
                    ? "当前由电池供电；整机读数采用电池放电功率估算，系统级功率计作为组件读数保留。"
                    : "系统级功率计不可用；当前整机读数采用电池放电功率，表示电池端输出估算。";
                return;
            }

            if (frame.DirectSystemWatts.HasValue && frame.CpuPackageWatts.HasValue)
            {
                frame.AvailabilityMessage = "已读取 Windows 系统级功率计与 CPU 封装传感器。";
                return;
            }

            if (frame.BatteryState == BatteryFlowState.Charging && frame.BatteryPowerWatts.HasValue)
            {
                frame.AvailabilityMessage = frame.DirectSystemWatts.HasValue
                    ? "已读取系统级功率计与电池充电功率；充电功率不等于整机耗电。"
                    : "已读取电池充电功率；充电功率不等于整机耗电，当前整机功率计不可用。";
                return;
            }

            if (frame.BatteryState == BatteryFlowState.Idle)
            {
                frame.AvailabilityMessage = "电池当前没有充放电；电池端数据无法提供整机功耗。";
                return;
            }

            if (frame.CpuPackageWatts.HasValue)
            {
                frame.AvailabilityMessage = "已读取 Windows Energy Meter / Intel RAPL CPU 封装功率；系统级与电池功率当前不可用。";
                return;
            }

            if (frame.DirectSystemWatts.HasValue)
            {
                frame.AvailabilityMessage = "已读取 Windows 系统级功率计；CPU 封装和电池功率当前不可用。";
                return;
            }

            frame.AvailabilityMessage = "Windows 未提供有效的系统级、电池或 CPU RAPL 功率读数。";
        }

        private static bool TryParseRaplInstance(string instance, out string domain)
        {
            const string prefix = "RAPL_Package";
            domain = null;
            if (string.IsNullOrWhiteSpace(instance)
                || !instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            int separator = instance.IndexOf('_', prefix.Length);
            if (separator <= prefix.Length)
                return false;

            domain = instance.Substring(separator + 1);
            return domain.Length > 0;
        }

        private static double? ReadCounterWatts(PerformanceCounter counter)
        {
            if (counter == null) return null;

            float milliwatts = counter.NextValue();
            if (float.IsNaN(milliwatts) || float.IsInfinity(milliwatts)
                || milliwatts <= 0 || milliwatts > MaxPowerWatts * 1000)
                return null;

            return milliwatts / 1000.0;
        }

        private static PowerComponentReading MeasuredComponent(string name, double watts, string source)
            => new PowerComponentReading
            {
                Name = name,
                Watts = watts,
                Source = source,
                Kind = PowerValueKind.Measured,
            };

        private static string DomainDisplayName(string domain)
        {
            if (string.Equals(domain, "DRAM", StringComparison.OrdinalIgnoreCase)) return "DRAM 功耗域";
            if (string.Equals(domain, "PP0", StringComparison.OrdinalIgnoreCase)) return "CPU 核心域（PP0）";
            if (string.Equals(domain, "PP1", StringComparison.OrdinalIgnoreCase)) return "CPU 图形域（PP1）";
            return "RAPL " + domain + " 功耗域";
        }

        private static PowerSensorFrame FailedFrame(string message)
            => new PowerSensorFrame { AvailabilityMessage = message };

        private void Publish(PowerSensorFrame frame)
        {
            LogTransition(frame.AvailabilityMessage, null, null);
            Volatile.Write(ref _latest, frame);
        }

        private void LogTransition(string status, string error, Exception exception)
        {
            if (string.Equals(_lastStatus, status, StringComparison.Ordinal))
                return;
            _lastStatus = status;
            if (string.IsNullOrWhiteSpace(error))
                Logger.Info("硬件功率数据状态：" + status);
            else
                Logger.Warn(error + "：" + status, exception);
        }

        private void ReleaseCounters()
        {
            foreach (RaplCounter sensor in _raplCounters)
                sensor.Counter?.Dispose();
            _raplCounters.Clear();
            _systemPowerCounter?.Dispose();
            _systemPowerCounter = null;
        }

        private sealed class RaplCounter
        {
            public readonly string Domain;
            public readonly PerformanceCounter Counter;

            public RaplCounter(string domain, PerformanceCounter counter)
            {
                Domain = domain;
                Counter = counter;
            }
        }

        public bool Shutdown()
        {
            _stopping = true;
            _wake.Set();
            if (Thread.CurrentThread == _thread) return true;
            if (_thread.Join(StopJoinMs))
            {
                _wake.Dispose();
                return true;
            }
            Logger.Warn($"硬件功率传感器采样停止等待超时（{StopJoinMs}ms）");
            return false;
        }
    }

    internal sealed class PowerSampler
    {
        private const int HistoryCapacity = 60;
        private readonly HardwarePowerSensorSampler _sensor = new HardwarePowerSensorSampler();
        private readonly Queue<double?> _systemHistory = new Queue<double?>(HistoryCapacity);
        private readonly Queue<double?> _packageHistory = new Queue<double?>(HistoryCapacity);
        private readonly Queue<double?> _batteryHistory = new Queue<double?>(HistoryCapacity);
        private readonly Queue<double?> _batteryChargeHistory = new Queue<double?>(HistoryCapacity);
        private readonly Queue<double?> _batteryDischargeHistory = new Queue<double?>(HistoryCapacity);
        private PowerValueKind? _lastSystemKind;
        private string _lastSystemSource;
        private double? _energyWh;
        private double _peak;
        private double? _lastSystemWatts;
        private long _lastSampleTimestamp;
        private BatteryFlowState? _lastBatteryState;
        private double? _batteryEnergyWh;
        private double _batteryPeak;
        private double? _lastBatteryWatts;
        private long _lastBatteryTimestamp;

        public void SetEnabled(bool enabled) => _sensor.SetEnabled(enabled);

        /// <summary>Breaks the energy integration interval after sampling was paused.</summary>
        public void ResetEnergyBaseline()
        {
            _lastSystemWatts = null;
            _lastSampleTimestamp = 0;
            _lastBatteryWatts = null;
            _lastBatteryTimestamp = 0;
        }

        public PowerSnapshot Sample(double[] perCoreUsage, List<ProcessInfo> topProcesses)
        {
            long now = Stopwatch.GetTimestamp();
            PowerSensorFrame frame = _sensor.Latest ?? PowerSensorFrame.Empty;
            double? package = frame.CpuPackageWatts;
            double? system = frame.DirectSystemWatts;
            PowerValueKind systemKind = system.HasValue ? PowerValueKind.Measured : PowerValueKind.Unavailable;
            string systemSource = frame.DirectSystemSource ?? "";

            if (!frame.AcOnLine
                && frame.BatteryState == BatteryFlowState.Discharging
                && frame.BatteryPowerWatts.HasValue)
            {
                system = frame.BatteryPowerWatts;
                systemKind = PowerValueKind.Estimated;
                systemSource = "Windows Battery API · 电池端放电功率估算";
            }

            var components = new List<PowerComponentReading>(frame.Components);

            // Do not combine unlike sources in one rolling average or energy interval.
            if (_lastSystemKind.HasValue
                && (_lastSystemKind.Value != systemKind
                    || !string.Equals(_lastSystemSource, systemSource, StringComparison.Ordinal)))
            {
                _systemHistory.Clear();
                _energyWh = null;
                _peak = 0;
                ResetEnergyBaseline();
            }
            _lastSystemKind = systemKind;
            _lastSystemSource = systemSource;

            AddHistory(_systemHistory, system);
            AddHistory(_packageHistory, package);
            if (system.HasValue && _lastSystemWatts.HasValue && _lastSampleTimestamp != 0)
            {
                double elapsedSeconds = (now - _lastSampleTimestamp) / (double)Stopwatch.Frequency;
                if (elapsedSeconds > 0 && elapsedSeconds <= 30)
                    _energyWh = (_energyWh ?? 0) + ((_lastSystemWatts.Value + system.Value) / 2.0) * elapsedSeconds / 3600.0;
            }
            if (system.HasValue) _peak = Math.Max(_peak, system.Value);
            _lastSystemWatts = system;
            _lastSampleTimestamp = now;

            if (!_lastBatteryState.HasValue || _lastBatteryState.Value != frame.BatteryState)
            {
                _batteryEnergyWh = null;
                _batteryPeak = 0;
                _lastBatteryWatts = null;
                _lastBatteryTimestamp = 0;
            }
            _lastBatteryState = frame.BatteryState;
            double? batteryFlowWatts = frame.BatteryState == BatteryFlowState.Charging
                ? -frame.BatteryPowerWatts
                : frame.BatteryState == BatteryFlowState.Discharging
                    ? frame.BatteryPowerWatts
                    : frame.BatteryState == BatteryFlowState.Idle
                        ? 0.0
                        : (double?)null;
            AddHistory(_batteryHistory, batteryFlowWatts);
            AddHistory(_batteryChargeHistory, frame.BatteryState == BatteryFlowState.Charging
                ? frame.BatteryPowerWatts : (double?)null);
            AddHistory(_batteryDischargeHistory, frame.BatteryState == BatteryFlowState.Discharging
                ? frame.BatteryPowerWatts : (double?)null);
            if (batteryFlowWatts.HasValue && frame.BatteryState != BatteryFlowState.Idle)
            {
                _batteryPeak = Math.Max(_batteryPeak, Math.Abs(batteryFlowWatts.Value));
                if (_lastBatteryWatts.HasValue && _lastBatteryTimestamp != 0)
                {
                    double elapsedSeconds = (now - _lastBatteryTimestamp) / (double)Stopwatch.Frequency;
                    if (elapsedSeconds > 0 && elapsedSeconds <= 30)
                        _batteryEnergyWh = (_batteryEnergyWh ?? 0)
                            + ((Math.Abs(_lastBatteryWatts.Value) + Math.Abs(batteryFlowWatts.Value)) / 2.0)
                            * elapsedSeconds / 3600.0;
                }
                _lastBatteryWatts = batteryFlowWatts;
                _lastBatteryTimestamp = now;
            }
            else
            {
                _lastBatteryWatts = null;
                _lastBatteryTimestamp = 0;
            }

            double[] usages = perCoreUsage ?? Array.Empty<double>();
            var coreWatts = new double?[usages.Length];
            var coreKinds = new PowerValueKind[usages.Length];
            double totalUsage = usages.Sum(value => Math.Max(0, value));
            for (int i = 0; i < usages.Length; i++)
            {
                double? coreDomain = frame.CpuCoreDomainWatts ?? package;
                if (coreDomain.HasValue)
                {
                    double weight = totalUsage > 0 ? Math.Max(0, usages[i]) / totalUsage : 1.0 / usages.Length;
                    coreWatts[i] = coreDomain.Value * weight;
                    coreKinds[i] = PowerValueKind.Estimated;
                }
                else
                {
                    coreKinds[i] = PowerValueKind.Unavailable;
                }
            }

            var processRows = new List<ProcessPowerReading>();
            if (package.HasValue && topProcesses != null)
            {
                foreach (ProcessInfo process in topProcesses)
                {
                    double watts = Math.Max(0, package.Value) * Math.Max(0, process.CpuPercent) / 100.0;
                    processRows.Add(new ProcessPowerReading
                    {
                        Name = process.Name,
                        ExePath = process.ExePath,
                        Watts = watts,
                        Kind = PowerValueKind.Estimated,
                    });
                }
                processRows = processRows.OrderByDescending(p => p.Watts ?? 0).Take(8).ToList();
            }

            return new PowerSnapshot
            {
                SystemWatts = system,
                SystemKind = systemKind,
                SystemSource = systemSource,
                CpuPackageWatts = package,
                CpuPackageKind = package.HasValue ? PowerValueKind.Measured : PowerValueKind.Unavailable,
                AvailabilityMessage = frame.AvailabilityMessage,
                SystemAverageWatts = Average(_systemHistory),
                SystemPeakWatts = _peak > 0 ? _peak : (double?)null,
                SystemEnergyWh = _energyWh,
                SystemHistory = _systemHistory.ToArray(),
                CpuPackageHistory = _packageHistory.ToArray(),
                BatteryPowerWatts = frame.BatteryPowerWatts,
                BatteryState = frame.BatteryState,
                BatterySource = frame.BatterySource ?? "",
                BatteryAverageWatts = frame.BatteryState == BatteryFlowState.Charging
                    ? Average(_batteryChargeHistory)
                    : frame.BatteryState == BatteryFlowState.Discharging
                        ? Average(_batteryDischargeHistory)
                        : (double?)null,
                BatteryPeakWatts = _batteryPeak > 0 ? _batteryPeak : (double?)null,
                BatteryEnergyWh = _batteryEnergyWh,
                BatteryHistory = _batteryHistory.ToArray(),
                BatteryChargeHistory = _batteryChargeHistory.ToArray(),
                BatteryDischargeHistory = _batteryDischargeHistory.ToArray(),
                CoreWatts = coreWatts,
                CoreKinds = coreKinds,
                Components = components,
                TopProcesses = processRows,
            };
        }

        public bool Shutdown() => _sensor.Shutdown();

        private static double? Average(IEnumerable<double?> values)
        {
            double sum = 0;
            int count = 0;
            foreach (double? value in values)
                if (value.HasValue) { sum += value.Value; count++; }
            return count == 0 ? (double?)null : sum / count;
        }

        private static void AddHistory(Queue<double?> queue, double? value)
        {
            queue.Enqueue(value);
            while (queue.Count > HistoryCapacity) queue.Dequeue();
        }
    }
}

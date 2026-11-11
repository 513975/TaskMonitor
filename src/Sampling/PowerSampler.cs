using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace task_monitor
{
    internal enum PowerValueKind
    {
        Unavailable,
        Measured,
        Stale,
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

    internal sealed class GpuPowerReading
    {
        public string Name { get; set; }
        public string DeviceId { get; set; }
        public double? Watts { get; set; }
        public DateTime SampleTimeUtc { get; set; }
        public PowerValueKind Kind { get; set; }
        public string Status { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// Detached hardware readings. The sampler reads Windows' built-in Energy Meter
    /// performance counters (EMI/RAPL) and aggregate battery status API; NVIDIA GPU
    /// power is published separately by the optional NVML worker.
    /// </summary>
    internal sealed class PowerSensorFrame
    {
        public double? CpuPackageWatts;
        public string CpuPackageSource;
        public bool CpuPackageStale;
        public DateTime CpuPackageSampleTimeUtc;
        public bool? AcOnLine;
        public bool? BatteryPresent;
        public double? BatteryPowerWatts;
        public BatteryFlowState BatteryState;
        public string BatterySource;
        public DateTime SampleTimeUtc;
        public long SampleTimestamp;
        public List<PowerComponentReading> Components = new List<PowerComponentReading>();
        public string AvailabilityMessage = "等待 Windows 硬件功率传感器";

        public static readonly PowerSensorFrame Empty = new PowerSensorFrame
        {
            AvailabilityMessage = "等待 Windows Energy Meter / RAPL 功率数据",
        };
    }

    internal sealed class PowerSnapshot
    {
        public double? CpuPackageWatts;
        public PowerValueKind CpuPackageKind;
        public string CpuPackageSource = "";
        public DateTime CpuPackageSampleTimeUtc;
        public DateTime SampleTimeUtc;
        public bool? AcOnLine;
        public bool? BatteryPresent;
        public double? CpuPackagePeakWatts; // max of the most recent 60 published power samples
        public string AvailabilityMessage = "";
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
        public List<PowerComponentReading> Components = new List<PowerComponentReading>();
        public GpuPowerReading[] GpuPowerReadings = Array.Empty<GpuPowerReading>();
        public double? GpuPowerWatts;
        public double? GpuPowerPeakWatts; // max of the complete NVML aggregate's 60-sample window
        public double? CoreTotalWatts;
        public double? CoreTotalEnergyWh; // session cumulative Wh, trapezoid-integrated while CoreTotal is valid
        public double?[] GpuPowerHistory = Array.Empty<double?>();
        public string GpuPowerAvailability = "等待 NVIDIA NVML 功耗数据";

        public static readonly PowerSnapshot Empty = new PowerSnapshot();
    }

    /// <summary>
    /// Reads Windows Energy Meter / RAPL counters and the OS battery rate.
    /// Counter inventory and per-domain recovery invariants: docs/gotchas.md §49.
    /// On Intel systems, Windows publishes RAPL domains in milliwatts. Battery rate remains
    /// a battery-side flow and is never substituted for CPU or system power.
    /// </summary>
    internal sealed class HardwarePowerSensorSampler
    {
        private const int PollIntervalMs = 1000;
        private const int RetryIntervalMs = 10000;
        private const int StopJoinMs = 1500;
        private const int ReenumerateEveryTicks = 60;
        private const double MaxPowerWatts = 2000;

        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private PowerSensorFrame _latest = PowerSensorFrame.Empty;
        private volatile bool _stopping;
        private volatile bool _enabled;
        private string _lastStatus;
        private long _retryAfter;
        private int _ticksSinceEnumerate;
        private readonly List<RaplCounter> _raplCounters = new List<RaplCounter>();

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
                        if (_raplCounters.Count == 0)
                        {
                            if (Stopwatch.GetTimestamp() >= _retryAfter)
                                OpenCounters();
                        }
                        else if (++_ticksSinceEnumerate >= ReenumerateEveryTicks)
                        {
                            // Counter instances can change after sleep/resume, firmware
                            // transitions, or a provider restart. Re-scan off the UI/sampler
                            // thread at a low cadence so a surviving stale counter does not
                            // suppress discovery of newly available RAPL domains.
                            OpenCounters();
                        }

                        frame = ReadFrame();
                    }
                    catch (Exception ex)
                    {
                        if (_raplCounters.Count == 0)
                            _retryAfter = Stopwatch.GetTimestamp()
                                + (long)(RetryIntervalMs / 1000.0 * Stopwatch.Frequency);
                        frame = FailedFrame("硬件功率传感器暂时读取失败");
                        Logger.WarnOnce("hardware-power-frame-read",
                            "硬件功率采样帧读取失败；保留现有 RAPL 句柄并继续重试", ex);
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
            _ticksSinceEnumerate = 0;
            try
            {
                var desiredInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (PerformanceCounterCategory.Exists("Energy Meter"))
                {
                    var energyCategory = new PerformanceCounterCategory("Energy Meter");
                    foreach (string instance in energyCategory.GetInstanceNames())
                        if (TryParseRaplInstance(instance, out _))
                            desiredInstances.Add(instance);
                }

                if (desiredInstances.Count == 0 && _raplCounters.Count > 0)
                {
                    // A transient empty inventory must not tear down working handles.
                    // Existing counters will still fail and retry independently if the
                    // provider was genuinely removed.
                    return;
                }

                // Keep live handles when the instance inventory is unchanged. Reopening
                // every counter during a routine scan resets rate-counter baselines and
                // can make CPU Package disappear for a sample.
                for (int i = _raplCounters.Count - 1; i >= 0; i--)
                {
                    RaplCounter current = _raplCounters[i];
                    if (desiredInstances.Contains(current.InstanceName)) continue;
                    current.Counter?.Dispose();
                    _raplCounters.RemoveAt(i);
                }

                foreach (string instance in desiredInstances)
                {
                    bool alreadyOpen = false;
                    foreach (RaplCounter current in _raplCounters)
                    {
                        if (!string.Equals(current.InstanceName, instance, StringComparison.OrdinalIgnoreCase))
                            continue;
                        alreadyOpen = true;
                        break;
                    }
                    if (alreadyOpen || !TryParseRaplInstance(instance, out string domain))
                        continue;

                    var sensor = new RaplCounter(instance, domain);
                    _raplCounters.Add(sensor);
                    TryOpenCounter(sensor);
                }
            }
            catch (Exception ex)
            {
                // Counter-category inventory can be unavailable while Windows refreshes
                // providers. Preserve existing readers and retry on the next low-rate scan.
                LogTransition("Windows Energy Meter 实例枚举暂时失败",
                    "枚举 Windows Energy Meter 实例失败", ex);
            }
            if (_raplCounters.Count == 0)
                _retryAfter = Stopwatch.GetTimestamp()
                    + (long)(RetryIntervalMs / 1000.0 * Stopwatch.Frequency);
        }

        private static bool TryOpenCounter(RaplCounter sensor)
        {
            PerformanceCounter counter = null;
            try
            {
                counter = new PerformanceCounter("Energy Meter", "Power", sensor.InstanceName, true);
                // Prime the interval baseline; the actual value is read on a later tick
                // after a full sampling interval.
                counter.NextValue();
                sensor.Counter?.Dispose();
                sensor.Counter = counter;
                sensor.NeedsPrime = true;
                sensor.InvalidSamples = 0;
                sensor.RetryAfter = 0;
                counter = null;
                return true;
            }
            catch (Exception ex)
            {
                sensor.RetryAfter = Stopwatch.GetTimestamp()
                    + (long)(2000 / 1000.0 * Stopwatch.Frequency);
                Logger.WarnOnce("rapl-open-" + sensor.InstanceName,
                    "打开 RAPL 功率计数器失败，将自动重试：" + sensor.InstanceName, ex);
                return false;
            }
            finally
            {
                counter?.Dispose();
            }
        }

        private PowerSensorFrame ReadFrame()
        {
            var frame = new PowerSensorFrame();
            var domainValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double packageTotal = 0;
            int packageCount = 0;
            int packageReadCount = 0;
            foreach (RaplCounter sensor in _raplCounters)
            {
                bool isPackage = string.Equals(sensor.Domain, "PKG", StringComparison.OrdinalIgnoreCase);
                if (isPackage)
                    packageCount++;
                double? watts = ReadCounterWatts(sensor, isPackage, out bool stale);
                if (!watts.HasValue) continue;

                if (isPackage)
                {
                    packageTotal += watts.Value;
                    packageReadCount++;
                    frame.CpuPackageStale |= stale;
                    if (sensor.LastGoodSampleTimeUtc > frame.CpuPackageSampleTimeUtc)
                        frame.CpuPackageSampleTimeUtc = sensor.LastGoodSampleTimeUtc;
                    continue;
                }

                string domainKey = sensor.Domain.ToUpperInvariant();
                domainValues[domainKey] = (domainValues.TryGetValue(domainKey, out double previous) ? previous : 0)
                    + watts.Value;
            }

            // A multi-socket machine must publish a package total only when every
            // package instance contributed. A partial sum would under-report CPU
            // power and could contaminate peaks or core-energy integration.
            if (packageCount > 0 && packageReadCount == packageCount)
            {
                frame.CpuPackageWatts = packageTotal;
                frame.CpuPackageSource = "Windows Energy Meter / Intel RAPL Package";
            }

            foreach (KeyValuePair<string, double> domain in domainValues)
            {
                string name = DomainDisplayName(domain.Key);
                frame.Components.Add(MeasuredComponent(name, domain.Value,
                    "Windows Energy Meter / Intel RAPL " + domain.Key));
            }

            try
            {
                ReadBattery(frame);
            }
            catch (Exception ex)
            {
                // Battery API failures are independent from RAPL; do not invalidate CPU
                // counters because the platform battery provider failed this sample.
                Logger.WarnOnce("battery-power-read",
                    "读取 Windows 电池功率失败；CPU RAPL 采样继续", ex);
            }
            UpdateAvailability(frame);
            return frame;
        }

        private static void ReadBattery(PowerSensorFrame frame)
        {
            if (!BatteryPowerInterop.TryRead(out BatteryPowerInterop.State battery))
                return;

            // Only successful API output is authoritative. The native struct is
            // zero-initialized before the call, so never infer offline from a default
            // value when TryRead failed.
            if (battery.AcOnLine <= 1)
                frame.AcOnLine = battery.AcOnLine != 0;
            frame.BatteryPresent = battery.BatteryPresent != 0;

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
        }

        private static void UpdateAvailability(PowerSensorFrame frame)
        {
            if (frame.CpuPackageWatts.HasValue && frame.BatteryPowerWatts.HasValue)
                frame.AvailabilityMessage = "已读取 CPU 封装功率与电池充放电功率。";
            else if (frame.CpuPackageWatts.HasValue)
                frame.AvailabilityMessage = "已读取 CPU 封装功率；当前没有可用的电池功率读数。";
            else if (frame.BatteryPowerWatts.HasValue || frame.BatteryState != BatteryFlowState.Unavailable)
                frame.AvailabilityMessage = "当前没有可用的 CPU 封装功率；电池读数仅表示电池充放电功率。";
            else
                frame.AvailabilityMessage = "Windows 未提供有效的 CPU RAPL 封装或电池功率读数。";
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

        private double? ReadCounterWatts(RaplCounter sensor, bool allowRecentValue, out bool stale)
        {
            stale = false;
            if (sensor.Counter == null)
            {
                if (Stopwatch.GetTimestamp() >= sensor.RetryAfter && TryOpenCounter(sensor))
                {
                    // This call happened during the current tick. The next scheduled sample
                    // will arrive after the required baseline interval.
                    sensor.NeedsPrime = false;
                }
                return allowRecentValue ? RecentValue(sensor, out stale) : (double?)null;
            }

            if (sensor.NeedsPrime)
            {
                sensor.NeedsPrime = false;
                return allowRecentValue ? RecentValue(sensor, out stale) : (double?)null;
            }

            float milliwatts;
            try
            {
                milliwatts = sensor.Counter.NextValue();
            }
            catch (Exception ex)
            {
                InvalidateCounter(sensor, ex);
                return allowRecentValue ? RecentValue(sensor, out stale) : (double?)null;
            }

            if (float.IsNaN(milliwatts) || float.IsInfinity(milliwatts)
                || milliwatts <= 0 || milliwatts > MaxPowerWatts * 1000)
            {
                if (++sensor.InvalidSamples >= 3)
                    InvalidateCounter(sensor, null);
                return allowRecentValue ? RecentValue(sensor, out stale) : (double?)null;
            }

            sensor.InvalidSamples = 0;
            sensor.LastGoodWatts = milliwatts / 1000.0;
            sensor.LastGoodTimestamp = Stopwatch.GetTimestamp();
            sensor.LastGoodSampleTimeUtc = DateTime.UtcNow;
            return sensor.LastGoodWatts;
        }

        private static double? RecentValue(RaplCounter sensor, out bool stale)
        {
            stale = sensor.LastGoodWatts.HasValue && sensor.LastGoodTimestamp > 0
                && Stopwatch.GetTimestamp() - sensor.LastGoodTimestamp
                    <= 3L * Stopwatch.Frequency;
            return stale ? sensor.LastGoodWatts : (double?)null;
        }

        private static void InvalidateCounter(RaplCounter sensor, Exception exception)
        {
            sensor.Counter?.Dispose();
            sensor.Counter = null;
            sensor.NeedsPrime = false;
            sensor.InvalidSamples = 0;
            sensor.RetryAfter = Stopwatch.GetTimestamp()
                + (long)(2000 / 1000.0 * Stopwatch.Frequency);
            Logger.WarnOnce("rapl-read-" + sensor.InstanceName,
                "RAPL 功率计数器读取失败，将单独重连：" + sensor.InstanceName, exception);
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
            if (!ReferenceEquals(frame, PowerSensorFrame.Empty))
            {
                frame.SampleTimeUtc = DateTime.UtcNow;
                frame.SampleTimestamp = Stopwatch.GetTimestamp();
            }
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
        }

        private sealed class RaplCounter
        {
            public readonly string InstanceName;
            public readonly string Domain;
            public PerformanceCounter Counter;
            public bool NeedsPrime;
            public int InvalidSamples;
            public long RetryAfter;
            public double? LastGoodWatts;
            public long LastGoodTimestamp;
            public DateTime LastGoodSampleTimeUtc;

            public RaplCounter(string instanceName, string domain)
            {
                InstanceName = instanceName;
                Domain = domain;
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
        // Rolling peak windows and CoreTotal integration/gap semantics: docs/gotchas.md §51.
        private readonly HardwarePowerSensorSampler _sensor = new HardwarePowerSensorSampler();
        private readonly NvmlGpuPowerSampler _gpuSensor = new NvmlGpuPowerSampler();
        private readonly Queue<double?> _packageHistory = new Queue<double?>(HistoryCapacity);
        private readonly Queue<double?> _batteryHistory = new Queue<double?>(HistoryCapacity);
        private readonly Queue<double?> _batteryChargeHistory = new Queue<double?>(HistoryCapacity);
        private readonly Queue<double?> _batteryDischargeHistory = new Queue<double?>(HistoryCapacity);
        private readonly Queue<double?> _gpuPowerHistory = new Queue<double?>(HistoryCapacity);
        private double? _coreTotalEnergyWh;
        private double? _lastCoreTotalWatts;
        private long _lastCoreTotalTimestamp;
        private BatteryFlowState? _lastBatteryState;
        private double? _batteryEnergyWh;
        private double _batteryPeak;
        private double? _lastBatteryWatts;
        private long _lastBatteryTimestamp;

        public void SetEnabled(bool enabled) => _sensor.SetEnabled(enabled);
        public void SetGpuEnabled(bool enabled) => _gpuSensor.SetEnabled(enabled);

        /// <summary>
        /// Breaks integration baselines and clears rolling display windows after power
        /// sampling was paused; accumulated core energy remains session cumulative.
        /// </summary>
        public void ResetEnergyBaseline()
        {
            _lastCoreTotalWatts = null;
            _lastCoreTotalTimestamp = 0;
            _lastBatteryWatts = null;
            _lastBatteryTimestamp = 0;
            _lastBatteryState = null;
            _batteryPeak = 0;
            _packageHistory.Clear();
            _gpuPowerHistory.Clear();
            _batteryHistory.Clear();
            _batteryChargeHistory.Clear();
            _batteryDischargeHistory.Clear();
        }

        public PowerSnapshot Sample(List<GpuInfo> gpuTopology)
        {
            long now = Stopwatch.GetTimestamp();
            PowerSensorFrame frame = _sensor.Latest ?? PowerSensorFrame.Empty;
            NvmlGpuPowerFrame gpuFrame = _gpuSensor.Latest ?? NvmlGpuPowerFrame.Empty;
            bool sensorFresh = IsFresh(frame.SampleTimestamp, now);
            double? package = sensorFresh ? frame.CpuPackageWatts : (double?)null;
            BatteryFlowState batteryState = sensorFresh ? frame.BatteryState : BatteryFlowState.Unavailable;
            double? batteryWatts = sensorFresh ? frame.BatteryPowerWatts : (double?)null;

            var components = sensorFresh
                ? new List<PowerComponentReading>(frame.Components)
                : new List<PowerComponentReading>();
            bool packageMeasured = package.HasValue && sensorFresh && !frame.CpuPackageStale;
            bool gpuFresh = IsGpuFrameFresh(gpuFrame, now);
            GpuPowerReading[] gpuReadings = gpuFresh
                ? GetFreshGpuReadings(gpuFrame, now)
                : Array.Empty<GpuPowerReading>();
            double? gpuWatts = SumMeasuredGpuPower(gpuReadings);
            AddHistory(_gpuPowerHistory, gpuWatts);
            AddHistory(_packageHistory, packageMeasured ? package : (double?)null);

            if (!_lastBatteryState.HasValue || _lastBatteryState.Value != batteryState)
            {
                _batteryEnergyWh = null;
                _batteryPeak = 0;
                _lastBatteryWatts = null;
                _lastBatteryTimestamp = 0;
            }
            _lastBatteryState = batteryState;
            double? batteryFlowWatts = batteryState == BatteryFlowState.Charging
                ? -batteryWatts
                : batteryState == BatteryFlowState.Discharging
                    ? batteryWatts
                    : batteryState == BatteryFlowState.Idle
                        ? 0.0
                        : (double?)null;
            AddHistory(_batteryHistory, batteryFlowWatts);
            AddHistory(_batteryChargeHistory, batteryState == BatteryFlowState.Charging
                ? batteryWatts : (double?)null);
            AddHistory(_batteryDischargeHistory, batteryState == BatteryFlowState.Discharging
                ? batteryWatts : (double?)null);
            if (batteryFlowWatts.HasValue && batteryState != BatteryFlowState.Idle)
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

            var snapshot = new PowerSnapshot
            {
                CpuPackageWatts = package,
                CpuPackageKind = !package.HasValue ? PowerValueKind.Unavailable
                    : frame.CpuPackageStale ? PowerValueKind.Stale : PowerValueKind.Measured,
                CpuPackageSource = sensorFresh ? frame.CpuPackageSource ?? "" : "",
                CpuPackageSampleTimeUtc = sensorFresh ? frame.CpuPackageSampleTimeUtc : default(DateTime),
                SampleTimeUtc = sensorFresh ? frame.SampleTimeUtc : default(DateTime),
                CpuPackagePeakWatts = Maximum(_packageHistory),
                AvailabilityMessage = sensorFresh ? frame.AvailabilityMessage : "硬件功率读数已过期或不可用。",
                CpuPackageHistory = _packageHistory.ToArray(),
                AcOnLine = sensorFresh ? frame.AcOnLine : (bool?)null,
                BatteryPresent = sensorFresh ? frame.BatteryPresent : (bool?)null,
                BatteryPowerWatts = batteryWatts,
                BatteryState = batteryState,
                BatterySource = sensorFresh ? frame.BatterySource ?? "" : "",
                BatteryAverageWatts = batteryState == BatteryFlowState.Charging
                    ? Average(_batteryChargeHistory)
                    : batteryState == BatteryFlowState.Discharging
                        ? Average(_batteryDischargeHistory)
                        : (double?)null,
                BatteryPeakWatts = _batteryPeak > 0 ? _batteryPeak : (double?)null,
                BatteryEnergyWh = _batteryEnergyWh,
                BatteryHistory = _batteryHistory.ToArray(),
                BatteryChargeHistory = _batteryChargeHistory.ToArray(),
                BatteryDischargeHistory = _batteryDischargeHistory.ToArray(),
                Components = components,
                GpuPowerReadings = gpuReadings,
                GpuPowerWatts = gpuWatts,
                GpuPowerPeakWatts = Maximum(_gpuPowerHistory),
                GpuPowerHistory = _gpuPowerHistory.ToArray(),
                GpuPowerAvailability = gpuFresh
                    ? gpuFrame.AvailabilityMessage
                    : "NVIDIA NVML 功耗读数已过期或不可用",
            };
            ApplyGpuTopology(snapshot, gpuTopology);

            double? coreTotal = snapshot.CoreTotalWatts;
            if (coreTotal.HasValue && snapshot.CpuPackageKind == PowerValueKind.Measured)
            {
                if (_lastCoreTotalWatts.HasValue && _lastCoreTotalTimestamp != 0)
                {
                    double elapsedSeconds = (now - _lastCoreTotalTimestamp) / (double)Stopwatch.Frequency;
                    if (elapsedSeconds > 0 && elapsedSeconds <= 30)
                        _coreTotalEnergyWh = (_coreTotalEnergyWh ?? 0)
                            + ((_lastCoreTotalWatts.Value + coreTotal.Value) / 2.0)
                                * elapsedSeconds / 3600.0;
                }
                _lastCoreTotalWatts = coreTotal;
                _lastCoreTotalTimestamp = now;
            }
            else
            {
                // Preserve the accumulated value, but never bridge missing, stale or
                // incomplete sources with an assumed constant core power.
                _lastCoreTotalWatts = null;
                _lastCoreTotalTimestamp = 0;
            }
            snapshot.CoreTotalEnergyWh = _coreTotalEnergyWh;
            return snapshot;
        }

        /// <summary>
        /// Derives CPU+GPU core power only when DXCore topology proves that every
        /// discrete adapter has a corresponding fresh NVML reading. On an iGPU-only
        /// Intel system, RAPL Package already contains the integrated GPU domain.
        /// </summary>
        private static void ApplyGpuTopology(PowerSnapshot power, List<GpuInfo> gpus)
        {
            if (power == null || power.CpuPackageKind != PowerValueKind.Measured
                || !power.CpuPackageWatts.HasValue || gpus == null || gpus.Count == 0)
                return;

            var discreteAdapters = new Dictionary<long, string>();
            foreach (GpuInfo gpu in gpus)
            {
                if (gpu == null || !gpu.IsIntegrated.HasValue)
                    return;
                if (!gpu.IsIntegrated.Value)
                    discreteAdapters[gpu.AdapterKey] = gpu.Name;
            }

            if (discreteAdapters.Count == 0)
            {
                // Do not add PP1/GT again: it is a child domain already represented by
                // Intel RAPL Package. A visible NVML device with no DXCore discrete
                // adapter means the topology is incomplete, so do not call this iGPU-only.
                if ((power.GpuPowerReadings == null || power.GpuPowerReadings.Length == 0)
                    && power.CpuPackageSource.IndexOf(
                        "Intel RAPL Package", StringComparison.OrdinalIgnoreCase) >= 0)
                    power.CoreTotalWatts = power.CpuPackageWatts;
                return;
            }

            if (power.GpuPowerReadings == null
                || power.GpuPowerReadings.Length != discreteAdapters.Count
                || !power.GpuPowerWatts.HasValue)
                return;

            // Counts alone do not prove that NVML covers the DXCore discrete adapters.
            // Require a one-to-one exact driver-reported name match as well; an unknown,
            // non-NVIDIA, or differently named adapter keeps the aggregate unavailable.
            var matchedReadings = new HashSet<int>();
            foreach (string adapterName in discreteAdapters.Values)
            {
                int match = -1;
                for (int i = 0; i < power.GpuPowerReadings.Length; i++)
                {
                    if (matchedReadings.Contains(i)) continue;
                    if (string.Equals(adapterName, power.GpuPowerReadings[i]?.Name,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        match = i;
                        break;
                    }
                }
                if (match < 0) return;
                matchedReadings.Add(match);
            }

            power.CoreTotalWatts = power.CpuPackageWatts.Value + power.GpuPowerWatts.Value;
        }

        private static bool IsFresh(long sampleTimestamp, long now)
            => sampleTimestamp > 0 && now >= sampleTimestamp
                && now - sampleTimestamp <= 3L * Stopwatch.Frequency;

        private static bool IsGpuFrameFresh(NvmlGpuPowerFrame frame, long now)
            => frame != null && IsFresh(frame.SampleTimestamp, now);

        private static GpuPowerReading[] GetFreshGpuReadings(NvmlGpuPowerFrame frame, long now)
        {
            if (frame?.Readings == null || !IsGpuFrameFresh(frame, now))
                return Array.Empty<GpuPowerReading>();
            return frame.Readings;
        }

        private static double? SumMeasuredGpuPower(GpuPowerReading[] readings)
        {
            if (readings == null || readings.Length == 0)
                return null;

            double total = 0;
            foreach (GpuPowerReading reading in readings)
            {
                if (reading == null || reading.Kind != PowerValueKind.Measured
                    || !reading.Watts.HasValue || double.IsNaN(reading.Watts.Value)
                    || double.IsInfinity(reading.Watts.Value) || reading.Watts.Value < 0)
                    return null;
                total += reading.Watts.Value;
            }
            return total;
        }

        public bool Shutdown()
        {
            bool cpuStopped = _sensor.Shutdown();
            bool gpuStopped = _gpuSensor.Shutdown();
            return cpuStopped && gpuStopped;
        }

        private static double? Average(IEnumerable<double?> values)
        {
            double sum = 0;
            int count = 0;
            foreach (double? value in values)
                if (value.HasValue) { sum += value.Value; count++; }
            return count == 0 ? (double?)null : sum / count;
        }

        private static double? Maximum(IEnumerable<double?> values)
        {
            double maximum = 0;
            bool found = false;
            foreach (double? value in values)
            {
                if (!value.HasValue) continue;
                maximum = found ? Math.Max(maximum, value.Value) : value.Value;
                found = true;
            }
            return found ? maximum : (double?)null;
        }

        private static void AddHistory(Queue<double?> queue, double? value)
        {
            queue.Enqueue(value);
            while (queue.Count > HistoryCapacity) queue.Dequeue();
        }
    }
}

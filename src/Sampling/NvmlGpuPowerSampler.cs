using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace task_monitor
{
    internal sealed class NvmlGpuPowerFrame
    {
        public GpuPowerReading[] Readings = Array.Empty<GpuPowerReading>();
        public string AvailabilityMessage = "NVIDIA NVML 功耗接口不可用";
        public long SampleTimestamp;

        public static readonly NvmlGpuPowerFrame Empty = new NvmlGpuPowerFrame();
    }

    /// <summary>
    /// Optional, driver-provided NVIDIA GPU power readings. NVML calls stay on this small
    /// polling thread so a slow vendor driver cannot stall the main sampler or taskbar UI.
    /// </summary>
    internal sealed class NvmlGpuPowerSampler
    {
        private const int PollIntervalMs = 1000;
        private const int RetryIntervalMs = 10000;
        private const int StopJoinMs = 1500;
        private const int ReenumerateEveryTicks = 30;
        private const int NvmlErrorNotSupported = 3;
        private const double MaxPowerWatts = 2000;

        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private NvmlGpuPowerFrame _latest = NvmlGpuPowerFrame.Empty;
        private volatile bool _enabled;
        private volatile bool _stopping;
        private NvmlApi _api;
        private long _retryAfter;
        private int _ticksSinceEnumerate;
        private int _consecutiveTransientFailures;
        private string _lastStatus;

        public NvmlGpuPowerFrame Latest => Volatile.Read(ref _latest);

        public NvmlGpuPowerSampler()
        {
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "NvmlGpuPowerSampler",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
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
                        ReleaseApi();
                        Publish(NvmlGpuPowerFrame.Empty);
                        _wake.WaitOne(PollIntervalMs);
                        continue;
                    }

                    long start = Stopwatch.GetTimestamp();
                    try
                    {
                        EnsureApi();
                        Publish(_api == null ? UnavailableFrame(_lastStatus) : Sample());
                    }
                    catch (Exception ex)
                    {
                        ReleaseApi();
                        _retryAfter = Stopwatch.GetTimestamp()
                            + (long)(RetryIntervalMs / 1000.0 * Stopwatch.Frequency);
                        string status = "NVML 采样失败：" + ex.GetType().Name;
                        LogTransition(status, ex);
                        Publish(UnavailableFrame(status));
                    }

                    int elapsed = (int)((Stopwatch.GetTimestamp() - start) * 1000L / Stopwatch.Frequency);
                    _wake.WaitOne(Math.Max(1, PollIntervalMs - elapsed));
                }
            }
            catch (Exception ex)
            {
                Logger.WarnOnce("nvml-gpu-power-sampler", "NVIDIA GPU 功耗采样线程异常退出", ex);
            }
            finally
            {
                ReleaseApi();
                Volatile.Write(ref _latest, NvmlGpuPowerFrame.Empty);
            }
        }

        private void EnsureApi()
        {
            if (_api != null || Stopwatch.GetTimestamp() < _retryAfter)
                return;

            if (!NvmlApi.TryCreate(out _api, out string status))
            {
                _retryAfter = Stopwatch.GetTimestamp()
                    + (long)(RetryIntervalMs / 1000.0 * Stopwatch.Frequency);
                LogTransition(status, null);
                return;
            }

            _ticksSinceEnumerate = 0;
            _consecutiveTransientFailures = 0;
            LogTransition(status, null);
        }

        private NvmlGpuPowerFrame Sample()
        {
            if (_api == null)
                return UnavailableFrame(_lastStatus);

            if (++_ticksSinceEnumerate >= ReenumerateEveryTicks)
            {
                _ticksSinceEnumerate = 0;
                if (!_api.RefreshDevices())
                {
                    ReleaseApi();
                    _retryAfter = Stopwatch.GetTimestamp()
                        + (long)(RetryIntervalMs / 1000.0 * Stopwatch.Frequency);
                    const string status = "NVML GPU 设备枚举暂时失败";
                    LogTransition(status, null);
                    return UnavailableFrame(status);
                }
            }

            var readings = new List<GpuPowerReading>(_api.Devices.Count);
            bool hasTransientFailure = false;
            foreach (NvmlApi.Device device in _api.Devices)
            {
                uint milliwatts = 0;
                int result = device.HandleUnavailable
                    ? -1 : _api.ReadPowerUsage(device, out milliwatts);
                bool valid = result == 0 && milliwatts <= MaxPowerWatts * 1000;
                if (!device.HandleUnavailable
                    && ((result != 0 && result != NvmlErrorNotSupported) || (result == 0 && !valid)))
                    hasTransientFailure = true;
                string status = device.HandleUnavailable ? device.Error
                    : valid ? "实测" : result == NvmlErrorNotSupported
                        ? "驱动不支持" : result == 0 ? "读数超出有效范围" : "NVML 错误 " + result;
                if (string.IsNullOrWhiteSpace(device.Uuid))
                    status += " · 临时序号";
                readings.Add(new GpuPowerReading
                {
                    Name = device.Name,
                    DeviceId = string.IsNullOrWhiteSpace(device.Uuid)
                        ? "NVML GPU " + device.Index
                        : device.Uuid,
                    Watts = valid ? milliwatts / 1000.0 : (double?)null,
                    SampleTimeUtc = DateTime.UtcNow,
                    Kind = valid ? PowerValueKind.Measured : PowerValueKind.Unavailable,
                    Status = status,
                    Source = "NVIDIA NVML / GPU Power",
                });
            }

            if (hasTransientFailure)
                _consecutiveTransientFailures++;
            else
                _consecutiveTransientFailures = 0;

            string availability = readings.Count == 0
                ? "NVML 已连接，但没有检测到 NVIDIA GPU"
                : "功耗来源：NVIDIA NVML 驱动遥测";
            if (_consecutiveTransientFailures >= 3)
            {
                const string retryStatus = "NVML 连续读取失败，10 秒后重连";
                LogTransition(retryStatus, null);
                _retryAfter = Stopwatch.GetTimestamp()
                    + (long)(RetryIntervalMs / 1000.0 * Stopwatch.Frequency);
                ReleaseApi();
                availability = retryStatus;
            }
            return new NvmlGpuPowerFrame
            {
                Readings = readings.ToArray(),
                AvailabilityMessage = availability,
            };
        }

        private static NvmlGpuPowerFrame UnavailableFrame(string status)
            => new NvmlGpuPowerFrame
            {
                AvailabilityMessage = string.IsNullOrWhiteSpace(status)
                    ? "NVIDIA NVML 功耗接口不可用" : status,
            };

        private void Publish(NvmlGpuPowerFrame frame)
        {
            frame = frame ?? NvmlGpuPowerFrame.Empty;
            if (!ReferenceEquals(frame, NvmlGpuPowerFrame.Empty))
                frame.SampleTimestamp = Stopwatch.GetTimestamp();
            Volatile.Write(ref _latest, frame);
        }

        private void LogTransition(string status, Exception exception)
        {
            if (string.Equals(_lastStatus, status, StringComparison.Ordinal))
                return;
            _lastStatus = status;
            if (exception == null) Logger.Info("NVIDIA GPU 功耗数据状态：" + status);
            else Logger.Warn("读取 NVIDIA GPU 功耗失败：" + status, exception);
        }

        private void ReleaseApi()
        {
            _api?.Dispose();
            _api = null;
            _ticksSinceEnumerate = 0;
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
            Logger.Warn($"NVIDIA GPU 功耗传感器采样停止等待超时（{StopJoinMs}ms）");
            return false;
        }
    }
}

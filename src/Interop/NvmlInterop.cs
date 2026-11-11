using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace task_monitor
{
    /// <summary>
    /// Optional NVIDIA NVML boundary. The driver-owned nvml.dll is resolved by absolute
    /// path and loaded dynamically; it is never bundled with the application.
    /// </summary>
    internal sealed class NvmlApi : IDisposable
    {
        private const int Success = 0;
        private const uint MaxDevices = 64;
        private const uint BufferLength = 96;
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ShutdownDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DeviceGetCountDelegate(out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DeviceGetHandleByIndexDelegate(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate int DeviceGetStringDelegate(IntPtr device, StringBuilder value, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DeviceGetPowerUsageDelegate(IntPtr device, out uint milliwatts);

        internal sealed class Device
        {
            public IntPtr Handle;
            public uint Index;
            public string Name;
            public string Uuid;
            public bool HandleUnavailable;
            public string Error;
        }

        private IntPtr _module;
        private InitDelegate _init;
        private ShutdownDelegate _shutdown;
        private DeviceGetCountDelegate _getCount;
        private DeviceGetHandleByIndexDelegate _getHandleByIndex;
        private DeviceGetStringDelegate _getName;
        private DeviceGetStringDelegate _getUuid;
        private DeviceGetPowerUsageDelegate _getPowerUsage;
        private bool _initialized;

        private NvmlApi(IntPtr module)
        {
            _module = module;
        }

        public List<Device> Devices { get; } = new List<Device>();

        public static bool TryCreate(out NvmlApi api, out string status)
        {
            api = null;
            status = "NVML 驱动动态库未在受支持目录中找到";
            IntPtr module = IntPtr.Zero;
            bool foundCandidate = false;
            int lastLoadError = 0;
            foreach (string path in CandidatePaths())
            {
                if (!File.Exists(path)) continue;
                foundCandidate = true;
                module = LoadLibraryExW(path, IntPtr.Zero,
                    LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
                if (module != IntPtr.Zero) break;
                lastLoadError = Marshal.GetLastWin32Error();
            }

            if (module == IntPtr.Zero)
            {
                if (foundCandidate)
                    status = "NVML 动态库加载失败 (Win32 " + lastLoadError + ")，请检查位数与驱动安装";
                return false;
            }

            var candidate = new NvmlApi(module);
            try
            {
                if (!candidate.BindExports(out string missingExport))
                {
                    status = "NVML 缺少必需入口：" + missingExport;
                    candidate.Dispose();
                    return false;
                }

                int result = candidate._init();
                if (result != Success)
                {
                    status = "NVML 初始化失败 (" + result + ")";
                    candidate.Dispose();
                    return false;
                }

                candidate._initialized = true;
                if (!candidate.RefreshDevices())
                {
                    status = "NVML 无法枚举 NVIDIA GPU";
                    candidate.Dispose();
                    return false;
                }

                api = candidate;
                status = candidate.Devices.Count == 0
                    ? "NVML 已连接，未检测到 NVIDIA GPU"
                    : "NVML 已连接，发现 " + candidate.Devices.Count + " 块 NVIDIA GPU";
                return true;
            }
            catch (Exception ex)
            {
                status = "NVML 初始化异常：" + ex.GetType().Name;
                candidate.Dispose();
                return false;
            }
        }

        public bool RefreshDevices()
        {
            if (!_initialized) return false;
            if (_getCount(out uint count) != Success || count > MaxDevices)
            {
                Devices.Clear();
                return false;
            }

            var devices = new List<Device>((int)count);
            for (uint i = 0; i < count; i++)
            {
                int handleStatus = _getHandleByIndex(i, out IntPtr handle);
                if (handleStatus != Success || handle == IntPtr.Zero)
                {
                    devices.Add(new Device
                    {
                        Index = i,
                        Name = "NVIDIA GPU " + i,
                        Uuid = "NVML GPU " + i,
                        HandleUnavailable = true,
                        Error = "设备句柄获取失败 (" + handleStatus + ")",
                    });
                    continue;
                }

                string name = ReadString(_getName, handle);
                if (string.IsNullOrWhiteSpace(name)) name = "NVIDIA GPU";
                string uuid = _getUuid == null ? null : ReadString(_getUuid, handle);
                devices.Add(new Device
                {
                    Handle = handle,
                    Index = i,
                    Name = name,
                    Uuid = uuid,
                });
            }

            Devices.Clear();
            Devices.AddRange(devices);
            return true;
        }

        public int ReadPowerUsage(Device device, out uint milliwatts)
        {
            milliwatts = 0;
            if (!_initialized || device == null || device.Handle == IntPtr.Zero)
                return -1;
            return _getPowerUsage(device.Handle, out milliwatts);
        }

        private bool BindExports(out string missingExport)
        {
            missingExport = null;
            _init = Bind<InitDelegate>("nvmlInit_v2") ?? Bind<InitDelegate>("nvmlInit");
            if (_init == null) { missingExport = "nvmlInit_v2/nvmlInit"; return false; }
            _shutdown = Bind<ShutdownDelegate>("nvmlShutdown");
            if (_shutdown == null) { missingExport = "nvmlShutdown"; return false; }
            _getCount = Bind<DeviceGetCountDelegate>("nvmlDeviceGetCount_v2")
                ?? Bind<DeviceGetCountDelegate>("nvmlDeviceGetCount");
            if (_getCount == null) { missingExport = "nvmlDeviceGetCount_v2/nvmlDeviceGetCount"; return false; }
            _getHandleByIndex = Bind<DeviceGetHandleByIndexDelegate>("nvmlDeviceGetHandleByIndex_v2")
                ?? Bind<DeviceGetHandleByIndexDelegate>("nvmlDeviceGetHandleByIndex");
            if (_getHandleByIndex == null) { missingExport = "nvmlDeviceGetHandleByIndex_v2/nvmlDeviceGetHandleByIndex"; return false; }
            _getName = Bind<DeviceGetStringDelegate>("nvmlDeviceGetName");
            if (_getName == null) { missingExport = "nvmlDeviceGetName"; return false; }
            // Power sampling still works if an older driver lacks UUID. In that case the UI
            // uses the explicit NVML enumeration ordinal as an ephemeral fallback identity.
            _getUuid = Bind<DeviceGetStringDelegate>("nvmlDeviceGetUUID");
            _getPowerUsage = Bind<DeviceGetPowerUsageDelegate>("nvmlDeviceGetPowerUsage");
            if (_getPowerUsage == null) { missingExport = "nvmlDeviceGetPowerUsage"; return false; }
            return true;
        }

        private T Bind<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(_module, name);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        private static string ReadString(DeviceGetStringDelegate getter, IntPtr device)
        {
            var buffer = new StringBuilder((int)BufferLength);
            return getter(device, buffer, BufferLength) == Success ? buffer.ToString() : null;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            string programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (string.IsNullOrWhiteSpace(programW6432))
                programW6432 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programW6432))
                yield return Path.Combine(programW6432, "NVIDIA Corporation", "NVSMI", "nvml.dll");

            string systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
                yield return Path.Combine(systemRoot, "System32", "nvml.dll");
        }

        public void Dispose()
        {
            if (_initialized)
            {
                try { _shutdown?.Invoke(); }
                catch { /* Driver teardown is best effort; never block sampler shutdown. */ }
                _initialized = false;
            }

            if (_module != IntPtr.Zero)
            {
                FreeLibrary(_module);
                _module = IntPtr.Zero;
            }
            Devices.Clear();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
    }
}

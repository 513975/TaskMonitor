using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// Reads Windows' aggregate battery rate through powrprof. Rate is a signed mW
    /// value: positive while charging, negative while discharging. This is battery-side
    /// power, not AC adapter input power.
    /// </summary>
    internal static class BatteryPowerInterop
    {
        private const int SystemBatteryState = 5;
        private const int StatusSuccess = 0;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct State
        {
            public byte AcOnLine;
            public byte BatteryPresent;
            public byte Charging;
            public byte Discharging;
            // Three reserved bytes plus Tag keep the following DWORDs 4-byte aligned.
            public byte Spare1;
            public byte Spare2;
            public byte Spare3;
            public byte Tag;
            public uint MaxCapacity;
            public uint RemainingCapacity;
            // Native type is ULONG. The high bit is interpreted as signed LONG by
            // Windows battery providers when it marks discharge direction.
            public uint Rate;
            public uint EstimatedTime;
            public uint DefaultAlert1;
            public uint DefaultAlert2;
        }

        [DllImport("powrprof.dll", ExactSpelling = true)]
        private static extern int CallNtPowerInformation(
            int informationLevel,
            IntPtr inputBuffer,
            uint inputBufferLength,
            out State outputBuffer,
            uint outputBufferLength);

        public static bool TryRead(out State state)
        {
            state = default;
            try
            {
                int status = CallNtPowerInformation(
                    SystemBatteryState,
                    IntPtr.Zero,
                    0,
                    out state,
                    (uint)Marshal.SizeOf(typeof(State)));
                return status == StatusSuccess;
            }
            catch (DllNotFoundException)
            {
                state = default;
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                state = default;
                return false;
            }
            catch (SEHException)
            {
                state = default;
                return false;
            }
        }
    }
}

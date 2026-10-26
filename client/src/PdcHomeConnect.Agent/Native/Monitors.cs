using System.Management;
using System.Runtime.InteropServices;

namespace PdcHomeConnect.Agent.Native;

/// <summary>A display whose brightness can be read and set.</summary>
public interface IBrightnessMonitor : IDisposable
{
    string Name { get; }

    string Kind { get; }

    int? GetBrightness();

    bool SetBrightness(int percent);
}

/// <summary>Enumerates displays with brightness control: external monitors via DDC/CI, internal panels via WMI.</summary>
public static class Monitors
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(nint monitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(nint monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(nint monitor, uint brightness);

    /// <summary>All displays with working brightness control (slow: DDC/CI round trips).</summary>
    public static List<IBrightnessMonitor> Enumerate()
    {
        var result = new List<IBrightnessMonitor>();
        result.AddRange(WmiPanel.Find());

        var handles = new List<nint>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref Rect _, nint _) =>
        {
            handles.Add(monitor);
            return true;
        }, 0);

        foreach (var handle in handles)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(handle, out var count) || count == 0)
            {
                continue;
            }

            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(handle, count, physical))
            {
                continue;
            }

            foreach (var monitor in physical)
            {
                var ddc = new DdcMonitor(monitor.Handle, string.IsNullOrWhiteSpace(monitor.Description) ? "Monitor" : monitor.Description.Trim());
                if (ddc.GetBrightness() is not null)
                {
                    result.Add(ddc);
                }
                else
                {
                    // No DDC/CI (or it is disabled in the monitor menu).
                    ddc.Dispose();
                }
            }
        }

        return result;
    }

    private sealed class DdcMonitor(nint handle, string name) : IBrightnessMonitor
    {
        private uint _min;
        private uint _max = 100;

        public string Name { get; } = name;

        public string Kind => "ddc";

        public int? GetBrightness()
        {
            // DDC/CI is flaky: retry once.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (GetMonitorBrightness(handle, out var min, out var current, out var max) && max > min)
                {
                    _min = min;
                    _max = max;
                    return (int)Math.Round((current - min) * 100.0 / (max - min));
                }

                Thread.Sleep(40);
            }

            return null;
        }

        public bool SetBrightness(int percent)
        {
            var value = _min + (uint)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * (_max - _min));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (SetMonitorBrightness(handle, value))
                {
                    return true;
                }

                Thread.Sleep(40);
            }

            return false;
        }

        public void Dispose() => DestroyPhysicalMonitor(handle);
    }

    /// <summary>Laptop/tablet panel brightness via WMI (root\wmi WmiMonitorBrightness).</summary>
    private sealed class WmiPanel(string instance) : IBrightnessMonitor
    {
        public string Name => "Built-in display";

        public string Kind => "wmi";

        public static List<IBrightnessMonitor> Find()
        {
            var panels = new List<IBrightnessMonitor>();
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName FROM WmiMonitorBrightness WHERE Active = TRUE");
                foreach (var item in searcher.Get().Cast<ManagementObject>())
                {
                    using (item)
                    {
                        panels.Add(new WmiPanel((string)item["InstanceName"]));
                    }
                }
            }
            catch (ManagementException)
            {
                // No WMI brightness support (desktop PCs).
            }
            catch (COMException)
            {
                // WMI not available.
            }

            return panels;
        }

        public int? GetBrightness()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", $"SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE InstanceName = '{instance.Replace("\\", "\\\\").Replace("'", "\\'")}'");
                foreach (var item in searcher.Get().Cast<ManagementObject>())
                {
                    using (item)
                    {
                        return Convert.ToInt32(item["CurrentBrightness"]);
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or COMException)
            {
                // Ignore.
            }

            return null;
        }

        public bool SetBrightness(int percent)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", $"SELECT * FROM WmiMonitorBrightnessMethods WHERE InstanceName = '{instance.Replace("\\", "\\\\").Replace("'", "\\'")}'");
                foreach (var item in searcher.Get().Cast<ManagementObject>())
                {
                    using (item)
                    {
                        item.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)Math.Clamp(percent, 0, 100)]);
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or COMException)
            {
                // Ignore.
            }

            return false;
        }

        public void Dispose()
        {
        }
    }
}

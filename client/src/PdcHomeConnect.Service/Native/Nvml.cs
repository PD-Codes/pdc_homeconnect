using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PdcHomeConnect.Service.Native;

/// <summary>
/// NVIDIA Management Library (nvml.dll, ships with the NVIDIA driver). Works from a service and
/// needs no extra installation.
/// </summary>
internal static class Nvml
{
    private const string Library = "nvml.dll";
    private static bool _resolverSet;

    internal enum Result
    {
        Success = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(Library, EntryPoint = "nvmlInit_v2")]
    internal static extern Result Init();

    [DllImport(Library, EntryPoint = "nvmlShutdown")]
    internal static extern Result Shutdown();

    [DllImport(Library, EntryPoint = "nvmlDeviceGetCount_v2")]
    internal static extern Result DeviceGetCount(out uint count);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    internal static extern Result DeviceGetHandleByIndex(uint index, out nint device);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetName")]
    private static extern Result DeviceGetName(nint device, byte[] name, uint length);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    internal static extern Result DeviceGetUtilizationRates(nint device, out Utilization utilization);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    internal static extern Result DeviceGetMemoryInfo(nint device, out Memory memory);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetTemperature")]
    internal static extern Result DeviceGetTemperature(nint device, int sensorType, out uint temperature);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetPowerUsage")]
    internal static extern Result DeviceGetPowerUsage(nint device, out uint milliwatts);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetFanSpeed")]
    internal static extern Result DeviceGetFanSpeed(nint device, out uint percent);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetClockInfo")]
    internal static extern Result DeviceGetClockInfo(nint device, int clockType, out uint mhz);

    /// <summary>Loads nvml.dll from System32 or the legacy NVSMI folder. Returns false if no NVIDIA driver.</summary>
    internal static bool TryLoad()
    {
        if (!_resolverSet)
        {
            _resolverSet = true;
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, assembly, path) =>
            {
                if (name != Library)
                {
                    return 0;
                }

                var candidates = new[]
                {
                    Path.Combine(Environment.SystemDirectory, Library),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", Library),
                };
                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        return handle;
                    }
                }
                return 0;
            });
        }

        try
        {
            return Init() == Result.Success;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static string GetName(nint device)
    {
        var buffer = new byte[96];
        if (DeviceGetName(device, buffer, (uint)buffer.Length) != Result.Success)
        {
            return "NVIDIA GPU";
        }

        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }
}

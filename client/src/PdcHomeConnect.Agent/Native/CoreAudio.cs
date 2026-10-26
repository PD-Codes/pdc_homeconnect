using System.Runtime.InteropServices;

namespace PdcHomeConnect.Agent.Native;

// Windows Core Audio COM interop (MMDevice API, endpoint volume, default device switching).
// Methods without [PreserveSig] throw on failure HRESULTs.

public enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

public enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;

    public PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct PropVariant
{
    public ushort VarType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Value2;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
public class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    IMMDeviceCollection EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask);

    IMMDevice GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role);

    IMMDevice GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id);

    void RegisterEndpointNotificationCallback(IMMNotificationClient client);

    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    uint GetCount();

    IMMDevice Item(uint index);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object Activate(ref Guid iid, uint clsCtx, nint activationParams);

    IPropertyStore OpenPropertyStore(uint stgmAccess);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetId();

    uint GetState();
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    uint GetCount();

    PropertyKey GetAt(uint index);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    void SetValue(ref PropertyKey key, ref PropVariant value);

    void Commit();
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

    uint GetChannelCount();

    void SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    float GetMasterVolumeLevel();

    float GetMasterVolumeLevelScalar();

    void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    float GetChannelVolumeLevel(uint channel);

    float GetChannelVolumeLevelScalar(uint channel);

    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetMute();

    void GetVolumeStepInfo(out uint step, out uint stepCount);

    void VolumeStepUp(ref Guid eventContext);

    void VolumeStepDown(ref Guid eventContext);

    uint QueryHardwareSupport();

    void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[ComImport]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(nint notifyData);
}

[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

/// <summary>Undocumented but stable interface used by all audio switchers to change the default device.</summary>
[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat(nint deviceName, nint format);

    [PreserveSig]
    int GetDeviceFormat(nint deviceName, int defaultFormat, nint format);

    [PreserveSig]
    int ResetDeviceFormat(nint deviceName);

    [PreserveSig]
    int SetDeviceFormat(nint deviceName, nint endpointFormat, nint mixFormat);

    [PreserveSig]
    int GetProcessingPeriod(nint deviceName, int defaultPeriod, nint defaultPeriodOut, nint minimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod(nint deviceName, nint period);

    [PreserveSig]
    int GetShareMode(nint deviceName, nint mode);

    [PreserveSig]
    int SetShareMode(nint deviceName, nint mode);

    [PreserveSig]
    int GetPropertyValue(nint deviceName, nint key, nint value);

    [PreserveSig]
    int SetPropertyValue(nint deviceName, nint key, nint value);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility(nint deviceName, int visible);
}

[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
public class PolicyConfigClient
{
}

public static class CoreAudioNative
{
    public static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static Guid AudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public const uint DeviceStateActive = 0x1;
    public const uint ClsCtxAll = 0x17;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant variant);

    public static string GetFriendlyName(IMMDevice device)
    {
        try
        {
            var store = device.OpenPropertyStore(0);
            var key = FriendlyName;
            if (store.GetValue(ref key, out var value) == 0)
            {
                try
                {
                    // VT_LPWSTR = 31
                    if (value.VarType == 31 && value.Value != 0)
                    {
                        return Marshal.PtrToStringUni(value.Value) ?? device.GetId();
                    }
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
        }
        catch
        {
            // Fall back to the id.
        }

        return device.GetId();
    }
}

using System.Runtime.InteropServices;

namespace PdcHomeConnect.Agent.Native;

/// <summary>Audio endpoint (device) information.</summary>
public sealed record AudioEndpoint(string Id, string Name);

/// <summary>
/// Wraps the MMDevice enumerator: lists endpoints, switches the default device and raises
/// <see cref="Changed"/> when devices or defaults change. COM callbacks must not call back into
/// the enumerator, so notifications are forwarded asynchronously.
/// </summary>
public sealed class AudioEndpoints : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient _client;

    public AudioEndpoints()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _client = new NotificationClient(() => Task.Run(() => Changed?.Invoke()));
        _enumerator.RegisterEndpointNotificationCallback(_client);
    }

    /// <summary>Raised (on a worker thread) when a device is added/removed or a default changes.</summary>
    public event Action? Changed;

    public IReadOnlyList<AudioEndpoint> List(EDataFlow flow)
    {
        var result = new List<AudioEndpoint>();
        var collection = _enumerator.EnumAudioEndpoints(flow, CoreAudioNative.DeviceStateActive);
        var count = collection.GetCount();
        for (uint i = 0; i < count; i++)
        {
            var device = collection.Item(i);
            result.Add(new AudioEndpoint(device.GetId(), CoreAudioNative.GetFriendlyName(device)));
        }

        return result;
    }

    public AudioEndpoint? Default(EDataFlow flow, ERole role = ERole.Multimedia)
    {
        try
        {
            var device = _enumerator.GetDefaultAudioEndpoint(flow, role);
            return new AudioEndpoint(device.GetId(), CoreAudioNative.GetFriendlyName(device));
        }
        catch (COMException)
        {
            // No device of this kind.
            return null;
        }
    }

    public IMMDevice? DefaultDevice(EDataFlow flow, ERole role = ERole.Multimedia)
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(flow, role);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Makes a device the default for all roles (console, multimedia, communications).</summary>
    public static void SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
        }
    }

    public void Dispose()
    {
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(_client);
        }
        catch
        {
            // Ignore during shutdown.
        }
    }

    [ComVisible(true)]
    public sealed class NotificationClient(Action onChanged) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            onChanged();
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            onChanged();
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            onChanged();
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            onChanged();
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}

/// <summary>Master volume of one endpoint with change notifications.</summary>
public sealed class EndpointVolume : IDisposable
{
    private readonly IAudioEndpointVolume _volume;
    private readonly VolumeCallback _callback;
    private Guid _context = Guid.NewGuid();

    public EndpointVolume(IMMDevice device, Action onChanged)
    {
        var iid = CoreAudioNative.AudioEndpointVolumeId;
        _volume = (IAudioEndpointVolume)device.Activate(ref iid, CoreAudioNative.ClsCtxAll, 0);
        _callback = new VolumeCallback(onChanged);
        _volume.RegisterControlChangeNotify(_callback);
        DeviceId = device.GetId();
    }

    public string DeviceId { get; }

    public float Level
    {
        get => _volume.GetMasterVolumeLevelScalar();
        set => _volume.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref _context);
    }

    public bool Muted
    {
        get => _volume.GetMute();
        set => _volume.SetMute(value, ref _context);
    }

    public void StepUp() => _volume.VolumeStepUp(ref _context);

    public void StepDown() => _volume.VolumeStepDown(ref _context);

    public void Dispose()
    {
        try
        {
            _volume.UnregisterControlChangeNotify(_callback);
        }
        catch
        {
            // Device gone.
        }
    }

    [ComVisible(true)]
    public sealed class VolumeCallback(Action onChanged) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(nint notifyData)
        {
            Task.Run(onChanged);
            return 0;
        }
    }
}

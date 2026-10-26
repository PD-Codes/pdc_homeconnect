using System.Runtime.InteropServices;

namespace PdcHomeConnect.Agent.Native;

/// <summary>
/// Hidden window on the UI thread that receives hotkeys, display power and session notifications.
/// </summary>
public sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int WmHotKey = 0x0312;
    private const int WmPowerBroadcast = 0x0218;
    private const int WmWtsSessionChange = 0x02B1;
    private const int PbtPowerSettingChange = 0x8013;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;

    public static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly List<nint> _powerRegistrations = [];

    public MessageWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "PdcHomeConnectAgentWindow",
            ExStyle = 0x00000080, // WS_EX_TOOLWINDOW
            Style = 0,
        });

        var displayState = ConsoleDisplayState;
        var registration = Native.RegisterPowerSettingNotification(Handle, ref displayState, 0);
        if (registration != 0)
        {
            _powerRegistrations.Add(registration);
        }

        Native.WTSRegisterSessionNotification(Handle, 0);
    }

    public event Action<int>? HotKeyPressed;

    /// <summary>Display state: 0 off, 1 on, 2 dimmed.</summary>
    public event Action<int>? DisplayStateChanged;

    /// <summary>Last known display state (Windows sends the current value right after registration).</summary>
    public int? DisplayState { get; private set; }

    public event Action<bool>? SessionLockChanged;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmHotKey:
                HotKeyPressed?.Invoke((int)m.WParam);
                break;
            case WmPowerBroadcast when (int)m.WParam == PbtPowerSettingChange && m.LParam != 0:
                var setting = Marshal.PtrToStructure<Guid>(m.LParam);
                if (setting == ConsoleDisplayState)
                {
                    // POWERBROADCAST_SETTING { GUID; DWORD DataLength; UCHAR Data[] }
                    var value = Marshal.ReadInt32(m.LParam, 20);
                    DisplayState = value;
                    DisplayStateChanged?.Invoke(value);
                }
                break;
            case WmWtsSessionChange:
                var reason = (int)m.WParam;
                if (reason == WtsSessionLock)
                {
                    SessionLockChanged?.Invoke(true);
                }
                else if (reason == WtsSessionUnlock)
                {
                    SessionLockChanged?.Invoke(false);
                }
                break;
        }

        base.WndProc(ref m);
    }

    public void TurnOffDisplays() =>
        Native.SendMessage(Handle, Native.WmSysCommand, Native.ScMonitorPower, 2);

    public void Dispose()
    {
        foreach (var registration in _powerRegistrations)
        {
            Native.UnregisterPowerSettingNotification(registration);
        }

        Native.WTSUnRegisterSessionNotification(Handle);
        DestroyHandle();
    }
}

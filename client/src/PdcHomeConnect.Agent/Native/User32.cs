using System.Runtime.InteropServices;
using System.Text;

namespace PdcHomeConnect.Agent.Native;

/// <summary>Win32 APIs used by the tray agent.</summary>
internal static class Native
{
    // ------------------------------------------------------------------ input / idle

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Time since the last keyboard/mouse input of this session.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        var now = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(now - info.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public MouseInput mi;
        // Padding so the struct has the size of the largest union member (KEYBDINPUT/HARDWAREINPUT are smaller).
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <summary>Simulates a tiny mouse movement (wakes the displays).</summary>
    public static void NudgeMouse()
    {
        const uint InputMouse = 0;
        const uint MouseEventMove = 0x0001;
        var inputs = new[]
        {
            new Input { type = InputMouse, mi = new MouseInput { dx = 1, dy = 0, dwFlags = MouseEventMove } },
            new Input { type = InputMouse, mi = new MouseInput { dx = -1, dy = 0, dwFlags = MouseEventMove } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    public const uint EsDisplayRequired = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    // ------------------------------------------------------------------ windows

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    public delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hook);

    public const uint EventSystemForeground = 0x0003;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;
    public const uint GwOwner = 4;
    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExAppWindow = 0x00040000L;
    public const int DwmwaCloaked = 14;

    public static string GetWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClass(nint hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    // ------------------------------------------------------------------ processes

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder exeName, ref int size);

    public static string? GetProcessPath(uint processId)
    {
        const uint QueryLimitedInformation = 0x1000;
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ------------------------------------------------------------------ shell

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>QUNS state: 1 not present, 2 busy, 3 D3D fullscreen, 4 presentation, 5 accepts notifications, 6 quiet time, 7 app.</summary>
    public static int QueryNotificationState() => SHQueryUserNotificationState(out var state) == 0 ? state : 5;

    // ------------------------------------------------------------------ messages

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);

    public const uint WmSysCommand = 0x0112;
    public const uint WmSettingChange = 0x001A;
    public const nint ScMonitorPower = 0xF170;
    public static readonly nint HwndBroadcast = 0xFFFF;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterPowerSettingNotification(nint handle);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSRegisterSessionNotification(nint hwnd, int flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSUnRegisterSessionNotification(nint hwnd);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    /// <summary>Lock state of a session from WTSINFOEX (null if unknown).</summary>
    public static bool? IsSessionLocked(int sessionId)
    {
        const int WtsSessionInfoEx = 25;
        if (!WTSQuerySessionInformation(0, sessionId, WtsSessionInfoEx, out var buffer, out var bytes))
        {
            return null;
        }

        try
        {
            if (bytes < 24 || Marshal.ReadInt32(buffer) != 1)
            {
                return null;
            }

            return Marshal.ReadInt32(buffer, 16) switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}

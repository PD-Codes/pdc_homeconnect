using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PdcHomeConnect.Service.Native;

/// <summary>Win32 APIs used by the service.</summary>
public static class Win32
{
    // ---------------------------------------------------------------- kernel32

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    internal const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Returns the full image path of a process or null.</summary>
    internal static string? GetProcessPath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
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

    // ---------------------------------------------------------------- advapi32

    internal const uint ShutdownForceOthers = 0x1;
    internal const uint ShutdownForceSelf = 0x2;
    internal const uint ShutdownRestart = 0x4;
    internal const uint ShutdownPowerOff = 0x8;
    internal const uint ShutdownGraceOverride = 0x20;
    internal const uint ShutdownHybrid = 0x200;
    internal const uint ReasonFlagPlanned = 0x80000000;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint InitiateShutdown(string? machineName, string? message, uint gracePeriod, uint flags, uint reason);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AbortSystemShutdown(string? machineName);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(nint tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    /// <summary>Enables a privilege (e.g. SeShutdownPrivilege) for the current process.</summary>
    internal static void EnablePrivilege(string name)
    {
        const uint TokenAdjustPrivileges = 0x20;
        const uint TokenQuery = 0x8;
        const uint SePrivilegeEnabled = 0x2;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---------------------------------------------------------------- powrprof

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities([Out] byte[] capabilities);

    /// <summary>Sleep (S3 or modern standby) and hibernate availability.</summary>
    internal static (bool Sleep, bool Hibernate) GetPowerCapabilities()
    {
        var buffer = new byte[256];
        if (!GetPwrCapabilities(buffer))
        {
            return (true, false);
        }

        // SYSTEM_POWER_CAPABILITIES: [5]=SystemS3, [6]=SystemS4, [8]=HiberFilePresent, [20]=AoAc (modern standby)
        var sleep = buffer[5] != 0 || buffer[20] != 0;
        var hibernate = buffer[6] != 0 && buffer[8] != 0;
        return (sleep, hibernate);
    }

    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerEnumerate(nint rootPowerKey, nint schemeGuid, nint subGroupOfPowerSettingsGuid, uint accessFlags, uint index, [Out] byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadFriendlyName(nint rootPowerKey, ref Guid schemeGuid, nint subGroupOfPowerSettingsGuid, nint powerSettingGuid, [Out] byte[]? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    internal static extern nint LocalFree(nint memory);

    // ---------------------------------------------------------------- wtsapi32

    public enum ConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init,
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        WinStationName = 6,
        DomainName = 7,
        ConnectState = 8,
        ClientName = 10,
        SessionInfoEx = 25,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string WinStationName;
        public ConnectState State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(nint server, int reserved, int version, out nint sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(nint server, int sessionId, WtsInfoClass infoClass, out nint buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQueryUserToken(uint sessionId, out nint token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSLogoffSession(nint server, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    public sealed record SessionInfo(int Id, string Station, ConnectState State, string? User, string? Domain, string? Client, bool? Locked, DateTimeOffset? LogonTime);

    /// <summary>Enumerates all sessions with user details.</summary>
    internal static List<SessionInfo> GetSessions()
    {
        var result = new List<SessionInfo>();
        if (!WTSEnumerateSessions(0, 0, 1, out var buffer, out var count))
        {
            return result;
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (i * size));
                var user = QueryString(info.SessionId, WtsInfoClass.UserName);
                var (locked, logon) = QueryLockAndLogon(info.SessionId);
                result.Add(new SessionInfo(
                    info.SessionId,
                    info.WinStationName ?? string.Empty,
                    info.State,
                    string.IsNullOrEmpty(user) ? null : user,
                    QueryString(info.SessionId, WtsInfoClass.DomainName),
                    QueryString(info.SessionId, WtsInfoClass.ClientName),
                    locked,
                    logon));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    private static string? QueryString(int sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(0, sessionId, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Reads lock state and logon time from WTSINFOEX (level 1) using fixed offsets.</summary>
    private static (bool? Locked, DateTimeOffset? Logon) QueryLockAndLogon(int sessionId)
    {
        if (!WTSQuerySessionInformation(0, sessionId, WtsInfoClass.SessionInfoEx, out var buffer, out var bytes))
        {
            return (null, null);
        }

        try
        {
            // WTSINFOEXW { DWORD Level; union (8-byte aligned) Data } -> Data at offset 8.
            // WTSINFOEX_LEVEL1_W: SessionFlags at +8, LogonTime (LARGE_INTEGER) at +160.
            if (bytes < 8 + 168 || Marshal.ReadInt32(buffer) != 1)
            {
                return (null, null);
            }

            var flags = Marshal.ReadInt32(buffer, 8 + 8);
            bool? locked = flags switch
            {
                0 => true,  // WTS_SESSIONSTATE_LOCK
                1 => false, // WTS_SESSIONSTATE_UNLOCK
                _ => null,
            };

            var logonFileTime = Marshal.ReadInt64(buffer, 8 + 160);
            DateTimeOffset? logon = logonFileTime > 0 ? DateTimeOffset.FromFileTime(logonFileTime) : null;
            return (locked, logon);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // ---------------------------------------------------------------- process creation in user sessions

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(nint existingToken, uint desiredAccess, nint tokenAttributes, int impersonationLevel, int tokenType, out nint newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        nint token,
        string? applicationName,
        StringBuilder? commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    /// <summary>Starts a program in an interactive user session (requires LocalSystem).</summary>
    internal static int StartInSession(uint sessionId, string exePath, string arguments)
    {
        const uint MaximumAllowed = 0x02000000;
        const int SecurityImpersonation = 2;
        const int TokenPrimary = 1;
        const uint CreateUnicodeEnvironment = 0x00000400;
        const uint CreateNoWindow = 0x08000000;

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        nint primaryToken = 0;
        nint environment = 0;
        try
        {
            if (!DuplicateTokenEx(userToken, MaximumAllowed, 0, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                environment = 0;
            }

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = @"winsta0\default",
            };
            var commandLine = new StringBuilder($"\"{exePath}\" {arguments}");
            if (!CreateProcessAsUser(
                    primaryToken,
                    exePath,
                    commandLine,
                    0,
                    0,
                    false,
                    CreateUnicodeEnvironment | CreateNoWindow,
                    environment,
                    Path.GetDirectoryName(exePath),
                    ref startup,
                    out var process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            return process.dwProcessId;
        }
        finally
        {
            if (environment != 0)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != 0)
            {
                CloseHandle(primaryToken);
            }

            CloseHandle(userToken);
        }
    }
}

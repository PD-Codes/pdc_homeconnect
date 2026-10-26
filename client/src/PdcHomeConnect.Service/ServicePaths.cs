using System.Security.AccessControl;
using System.Security.Principal;

namespace PdcHomeConnect.Service;

/// <summary>Locations of service data (C:\ProgramData\PDC\HomeConnect).</summary>
public sealed class ServicePaths
{
    public ServicePaths()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PDC", "HomeConnect");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Updates);
        Secure(Root);
    }

    public string Root { get; }

    public string Config => Path.Combine(Root, "service.json");

    public string Clients => Path.Combine(Root, "clients.json");

    public string Certificate => Path.Combine(Root, "certificate.pfx");

    public string AgentCache => Path.Combine(Root, "agent-entities.json");

    public string PairingCode => Path.Combine(Root, "pairing-code.txt");

    public string Logs => Path.Combine(Root, "logs");

    public string Updates => Path.Combine(Root, "updates");

    public static string InstallDirectory => AppContext.BaseDirectory;

    public static string AgentExecutable => Path.Combine(InstallDirectory, "PdcHomeConnect.Agent.exe");

    public static string ServiceExecutable => Path.Combine(InstallDirectory, "PdcHomeConnect.Service.exe");

    /// <summary>Restricts the data folder to SYSTEM and Administrators (tokens and keys live here).</summary>
    private static void Secure(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch
        {
            // Not running as SYSTEM/admin (development): keep default permissions.
        }
    }
}

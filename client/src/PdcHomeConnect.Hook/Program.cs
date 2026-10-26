using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

// Claude Code hook helper. Claude Code runs it for every configured hook event with the event JSON on stdin.
// It forwards the JSON to the PDC HomeConnect tray app of the same Windows session and always exits 0,
// so a missing tray app never disturbs Claude Code.
try
{
    using var stdin = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    var copy = stdin.CopyToAsync(buffer);
    if (!copy.Wait(TimeSpan.FromSeconds(5)) || buffer.Length == 0)
    {
        return 0;
    }

    var sessionId = Process.GetCurrentProcess().SessionId;
    using var pipe = new NamedPipeClientStream(".", $"PdcHomeConnect.Agent.{sessionId}", PipeDirection.Out);
    pipe.Connect(1000);

    // Only talk to a pipe created by this Windows user. PipeOptions.CurrentUserOnly is not used because it
    // compares with the token owner, which is "Administrators" in an elevated terminal.
    var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
    using (var identity = WindowsIdentity.GetCurrent())
    {
        if (owner is null || (!owner.Equals(identity.User) && !owner.Equals(identity.Owner)))
        {
            return 0;
        }
    }

    buffer.Position = 0;
    buffer.CopyTo(pipe);
    pipe.Flush();
}
catch
{
    // Tray app not running or busy: ignore.
}

return 0;

namespace PdcHomeConnect.Core.Util;

/// <summary>Builds process start parameters for executables and scripts (.ps1, .bat, .cmd).</summary>
public static class CommandLine
{
    public static (string File, string Arguments) Resolve(string command, string? args)
    {
        var file = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        var arguments = Environment.ExpandEnvironmentVariables(args ?? string.Empty);
        var extension = Path.GetExtension(file).ToLowerInvariant();
        return extension switch
        {
            ".ps1" => ("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{file}\" {arguments}".TrimEnd()),
            ".bat" or ".cmd" => (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"\"{file}\" {arguments}\"".TrimEnd()),
            _ => (file, arguments),
        };
    }
}

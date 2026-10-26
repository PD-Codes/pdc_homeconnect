namespace PdcHomeConnect.Core.Protocol;

/// <summary>Constants shared between the Windows client and the Home Assistant integration.</summary>
public static class ProtocolConstants
{
    /// <summary>Wire protocol version. Bump on breaking changes.</summary>
    public const int Version = 1;

    public const int DefaultPort = 48733;

    public const string ServiceType = "_pdc-homeconnect._tcp";

    public const string WebSocketPath = "/ws";

    public const string GitHubOwner = "PD-Codes";

    public const string GitHubRepo = "pdc_homeconnect";
}

/// <summary>Message type identifiers.</summary>
public static class MessageTypes
{
    // Both directions
    public const string Hello = "hello";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Result = "result";

    // Home Assistant -> PC
    public const string Auth = "auth";
    public const string PairRequest = "pair_request";
    public const string Pair = "pair";
    public const string Unpair = "unpair";
    public const string Command = "command";
    public const string Call = "call";
    public const string GetBlob = "get_blob";

    // PC -> Home Assistant
    public const string AuthOk = "auth_ok";
    public const string AuthFailed = "auth_failed";
    public const string PairPending = "pair_pending";
    public const string PairOk = "pair_ok";
    public const string PairFailed = "pair_failed";
    public const string Device = "device";
    public const string Entities = "entities";
    public const string States = "states";
    public const string Event = "event";
    public const string BusEvent = "bus_event";
    public const string Bye = "bye";
    public const string Error = "error";
}

/// <summary>Home Assistant entity platforms supported by descriptors.</summary>
public static class Platforms
{
    public const string Sensor = "sensor";
    public const string BinarySensor = "binary_sensor";
    public const string Button = "button";
    public const string Switch = "switch";
    public const string Number = "number";
    public const string Select = "select";
    public const string MediaPlayer = "media_player";
    public const string Event = "event";
}

/// <summary>Command actions sent by Home Assistant for entity commands.</summary>
public static class Actions
{
    public const string Press = "press";
    public const string TurnOn = "turn_on";
    public const string TurnOff = "turn_off";
    public const string SetValue = "set_value";
    public const string SelectOption = "select_option";
    public const string MediaPlay = "media_play";
    public const string MediaPause = "media_pause";
    public const string MediaPlayPause = "media_play_pause";
    public const string MediaStop = "media_stop";
    public const string MediaNext = "media_next_track";
    public const string MediaPrevious = "media_previous_track";
    public const string MediaSeek = "media_seek";
    public const string VolumeSet = "volume_set";
    public const string VolumeMute = "volume_mute";
    public const string VolumeUp = "volume_up";
    public const string VolumeDown = "volume_down";
    public const string PlayMedia = "play_media";
}

/// <summary>Error codes returned in failed results.</summary>
public static class ErrorCodes
{
    public const string NotFound = "not_found";
    public const string NotSupported = "not_supported";
    public const string InvalidParams = "invalid_params";
    public const string Unavailable = "unavailable";
    public const string Failed = "failed";
    public const string Timeout = "timeout";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
}

/// <summary>Common Home Assistant state class / category strings.</summary>
public static class Ha
{
    public const string Measurement = "measurement";
    public const string TotalIncreasing = "total_increasing";
    public const string Diagnostic = "diagnostic";
    public const string Config = "config";
}

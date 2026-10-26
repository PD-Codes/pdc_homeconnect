# PDC HomeConnect

Connect your Windows PCs and servers with Home Assistant – fast, event driven and without extra tools.

PDC HomeConnect consists of two parts:

- **Home Assistant integration** (this repository, installable with HACS)
- **Windows client**: a Windows service plus a small tray app, installed with one setup file

State changes (games, lock screen, calls, Claude Code, media, ...) are pushed instantly over an encrypted local
WebSocket connection. No MQTT broker, no cloud, no polling from Home Assistant.

## Features

**System (Windows service, works without a signed-in user)**

- CPU load, temperature, power and clock · memory · disks · network throughput · uptime/last boot · battery
- NVIDIA GPUs directly from the driver (load, temperature, hotspot, VRAM, power, fan, clock) – no extra software
- AMD/Intel GPUs, fans and drive temperatures/health via LibreHardwareMonitor (built in, optional PawnIO driver)
- Shut down, restart, sleep, hibernate, sign out, restart to UEFI, power plan, Wake-on-LAN
- Session state, signed-in users, pending reboot, Windows updates, Defender status
- **Windows Server**: Windows services (switches), Hyper-V VMs (switch, state, CPU, memory), event log errors,
  failed scheduled tasks, Windows Server Backup, storage pools/virtual disks, IIS sites, Docker containers, SMB sessions
- **Plex Media Server**: online, streams, transcodes, bandwidth, library sizes, scan button, playback events
- Whitelisted commands as buttons or via `pdc_homeconnect.run_command` (with response)
- Self-update of the client from Home Assistant (update entity)

**User session (tray app)**

- Activity: `locked`, `gaming`, `call`, `afk`, `working`, `media`, `active`
- AFK and idle time, displays on/off, lock and display buttons
- Foreground app, open apps, watched apps (running sensors)
- Gaming: Steam (instant, with cover art, session time and total playtime), Epic, GOG, custom and fullscreen games;
  game started/stopped events; Steam status and downloads
- **Discord voice**: in a call (works out of the box), channel, members, mute/deafen switches, leave button
  (with your own Discord application, see [docs/DISCORD.md](docs/DISCORD.md))
- Webcam and microphone in use (with the apps using them)
- **Claude Code**: idle / working / waiting for input / error, plus events (finished, waiting for input, ...)
- Media player: now playing from any app (Spotify, browsers, ...), cover art, controls, master volume,
  TTS announcements and audio playback on the PC
- Audio output/input device selection, monitor brightness (DDC/CI), dark mode switch
- Notifications with buttons, reply field, images (also from cameras), alarm/reminder style; button presses
  and replies come back as events
- Global hotkeys and tray menu entries as event entities (triggers for automations)
- Privacy mode (hides app names, titles, media and Discord details)

The full list is in [docs/ENTITIES.md](docs/ENTITIES.md).

## Requirements

- Home Assistant 2026.3 or newer
- Windows 10 (1809) / Windows 11 / Windows Server 2019 or newer, 64-bit (x64)

## Installation

### 1. Home Assistant integration (HACS)

1. HACS → three dots → **Custom repositories** → add `https://github.com/PD-Codes/pdc_homeconnect` as type **Integration**.
2. Install **PDC HomeConnect** and restart Home Assistant.

### 2. Windows client

1. Download `PDC-HomeConnect-Setup-x.y.z.exe` from the [latest release](https://github.com/PD-Codes/pdc_homeconnect/releases/latest).
2. Run it (administrator rights are needed for the service). Keep **Install the PawnIO driver** checked if you
   want CPU temperatures and fan speeds.

The service starts with Windows, the tray app starts at every sign-in. Nothing else needs to be configured.

### 3. Pairing

Home Assistant discovers the PC automatically (**Settings → Devices & services → Discovered**). You can also add
the integration manually and enter the IP address or host name of the PC.

Home Assistant asks for a 6-digit code: it is shown on the PC as a notification (and in the tray menu).
On a server without a signed-in user, the code is in `C:\ProgramData\PDC\HomeConnect\pairing-code.txt`
and in the Windows event log (Application, source "PDC HomeConnect").

## Actions

| Action | Description |
| --- | --- |
| `pdc_homeconnect.notify` | Notification with title, image, buttons, reply field, sound, scenario (reminder/alarm/urgent) |
| `pdc_homeconnect.clear_notifications` | Remove notifications (all or by tag) |
| `pdc_homeconnect.speak` | Speak a text with a Windows voice |
| `pdc_homeconnect.open_url` | Open a URL or app link (allowed schemes are configurable) |
| `pdc_homeconnect.set_clipboard` | Set the clipboard text |
| `pdc_homeconnect.power` | Shutdown/restart/sleep/hibernate/sign out with delay, force or abort |
| `pdc_homeconnect.run_command` | Run a whitelisted command, returns exit code and output |
| `pdc_homeconnect.vm_control` | Start/stop/save/pause/reset a Hyper-V VM |
| `pdc_homeconnect.service_control` | Start/stop/restart a Windows service |

Each PC also has a `notify` entity, so `notify.send_message` works as well.

Example – notification with buttons:

```yaml
action: pdc_homeconnect.notify
data:
  device_id: 0123456789abcdef0123456789abcdef
  title: Doorbell
  message: Someone is at the door
  image: camera.front_door
  actions:
    - action: open_door
      title: Open
    - action: ignore
      title: Ignore
```

Button presses fire the **Notification action** event entity and the bus event `pdc_homeconnect_notification_action`:

```yaml
triggers:
  - trigger: event
    event_type: pdc_homeconnect_notification_action
    event_data:
      action: open_door
```

Other bus events: `pdc_homeconnect_hotkey`, `pdc_homeconnect_tray_action`. All of them include `device_id` and `pc`.

## Configuration

Everything works with the defaults. Optional settings (JSON files with comments, applied automatically when saved):

- **Tray app** (per user): tray menu → Settings → *Edit tray app settings* – watched apps, work and call apps,
  games, hotkeys, tray actions, user commands, features
- **Service** (per PC, admin): tray menu → Settings → *Edit service settings* – device name, port, modules,
  Windows services, Plex, SYSTEM commands

See [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Security

- Local network only: TLS-encrypted WebSocket on port 48733, firewall rule limited to the local subnet
- Pairing with a one-time code shown on the PC; Home Assistant pins the PC's certificate
- Only paired Home Assistant instances can connect; pairings can be removed in the tray app (Status window)
- Commands can only run if they are whitelisted in a config file on the PC – Home Assistant can never run arbitrary
  command lines
- The tray app runs with normal user rights; the service only accepts the tray app from the installation folder

## Troubleshooting

- Logs: `C:\ProgramData\PDC\HomeConnect\logs` (service) and `%LOCALAPPDATA%\PDC\HomeConnect\logs` (tray app)
- More details in the logs: create an empty file named `debug` in `%LOCALAPPDATA%\PDC\HomeConnect` and restart the tray app
- The tray icon shows the connection: green = connected to Home Assistant, gray = not connected, orange = pairing
- CPU temperature missing: install PawnIO (rerun the setup with the PawnIO option or get it from https://pawnio.eu)

## Development

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and [docs/PROTOCOL.md](docs/PROTOCOL.md).

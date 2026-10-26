# Configuration

PDC HomeConnect works without any configuration. Two optional JSON files (comments allowed) change the defaults.
Both are created with all options and explanations on first start. Changes are applied automatically when the
file is saved; an invalid file is ignored (the tray app shows a notification) until it is fixed.

| File | Scope | Open with |
| --- | --- | --- |
| `%APPDATA%\PDC\HomeConnect\agent.json` | Tray app, per Windows user | Tray menu → Settings → Edit tray app settings |
| `C:\ProgramData\PDC\HomeConnect\service.json` | Service, per PC (admin) | Tray menu → Settings → Edit service settings |

## Tray app (`agent.json`)

| Option | Default | Description |
| --- | --- | --- |
| `afk_minutes` | `5` | Minutes without keyboard/mouse input until AFK turns on (never while gaming) |
| `report_window_titles` | `false` | Send the window title of the foreground app |
| `watched_apps` | Discord, Steam, Spotify, ... | Binary sensors "app running": `{ "id", "name", "processes": ["app.exe"] }` |
| `work_apps` | IDEs, Office, terminals | Foreground apps that count as activity `working` (Claude Code working counts too) |
| `call_apps` | Discord, Teams, Zoom, ... | Apps using the microphone/webcam that count as activity `call` |
| `games.steam` / `epic` / `gog` | `true` | Game detection sources |
| `games.fullscreen` | `true` | Treat exclusive fullscreen DirectX apps as games |
| `games.steam_images` | `true` | Steam cover art as entity picture |
| `games.custom` | `[]` | Own games: `{ "name": "Minecraft", "processes": ["javaw.exe"] }` |
| `games.ignore` | launchers, browsers | Processes (or Steam app ids) never treated as games |
| `discord.client_id` | `""` | Discord application id (normally set in the Discord setup dialog, see [DISCORD.md](DISCORD.md)) |
| `discord.redirect_uri` | `http://127.0.0.1` | Redirect URL registered in your Discord application |
| `notifications.fullscreen_fallback` | `sound` | When Windows hides notifications (game, presentation, focus assist): `none`, `sound` or `speak` |
| `hotkeys` | `[]` | `{ "id": "toggle_light", "keys": "Ctrl+Alt+L" }` → event entity "Hotkey" |
| `tray_actions` | `[]` | `{ "id": "movie_mode", "name": "Movie mode" }` → entries in the tray menu, event entity "Tray action" |
| `commands` | `[]` | Commands that run as the user (see below) |
| `allowed_url_schemes` | http, https, steam, ... | Schemes allowed for `pdc_homeconnect.open_url` |
| `features.media` | `true` | Media player entity |
| `features.audio_devices` | `true` | Audio output/input selects |
| `features.brightness` | `true` | Monitor brightness (DDC/CI, laptop panels) |
| `features.dark_mode` | `true` | Dark mode switch |
| `features.claude_code` | `true` | Claude Code status (installs hooks in `~/.claude/settings.json`) |

### User commands

```jsonc
"commands": [
  { "id": "open_downloads", "name": "Open Downloads", "command": "explorer.exe", "args": "%USERPROFILE%\\Downloads", "window": "normal" },
  { "id": "backup_notes", "name": "Backup notes", "command": "C:\\Scripts\\backup.ps1", "wait": true, "timeout_seconds": 300 }
]
```

| Field | Description |
| --- | --- |
| `id` | Used by `pdc_homeconnect.run_command` (`command: open_downloads`) |
| `name` | Button name in Home Assistant |
| `command` | Executable, `.ps1`, `.bat` or `.cmd` (environment variables are expanded) |
| `args` | Arguments |
| `working_directory` | Defaults to the user profile |
| `window` | `hidden` (default), `normal`, `minimized`, `maximized` |
| `wait` | Wait for the exit code and output (only with `hidden`) |
| `timeout_seconds` | Timeout when waiting (default 60) |
| `button` | Create a button entity (default `true`) |

## Service (`service.json`)

| Option | Default | Description |
| --- | --- | --- |
| `device_name` | computer name | Name shown in Home Assistant |
| `port` | `48733` | TCP port of the TLS WebSocket |
| `discovery` | `true` | mDNS announcement for automatic discovery |
| `manage_firewall` | `true` | Create the Windows Firewall rules |
| `firewall_scope` | `local_subnet` | `local_subnet` or `any` (e.g. Home Assistant in another VLAN) |
| `stats_interval` | `10` | Seconds between CPU/memory/disk/network updates |
| `hardware_interval` | `10` | Seconds between temperature/fan/GPU updates |
| `hardware_sensors` | `true` | LibreHardwareMonitor sensors (CPU temperature, fans, drives) |
| `agent_watchdog` | `true` | Restart the tray app in user sessions if it is not running |
| `allow_firmware_restart` | `true` | Offer the "Restart to UEFI" button |
| `windows_update_check_hours` | `6` | Interval of the Windows Update check |
| `modules.*` | see file | `auto` (on when the feature exists), `on` or `off` for: `services`, `hyper_v`, `sessions`, `event_log`, `scheduled_tasks`, `backup`, `storage_health`, `iis`, `docker`, `smb`, `plex`, `windows_update`, `defender`, `power_plan` |
| `watched_services` | `[]` | Windows services as switches, by service name, e.g. `["Spooler", "MSSQLSERVER"]` |
| `event_log.events_for_errors` | `false` | Also fire the "System event" entity for errors (not only critical events) |
| `event_log.logs` | System, Application | Logs that are watched |
| `plex.url` | `http://127.0.0.1:32400` | Plex Media Server URL |
| `plex.token` | auto | Plex token (read automatically from the Plex registry settings when empty) |
| `commands` | `[]` | Commands that run as SYSTEM, same fields as the user commands (without `window`) |

Commands are looked up in `service.json` first, then in the `agent.json` of the active user.

## Files

| Path | Content |
| --- | --- |
| `C:\ProgramData\PDC\HomeConnect\` | Service settings, paired clients, certificate, logs, pairing code (admins only) |
| `%APPDATA%\PDC\HomeConnect\` | Tray app settings and state (Discord tokens are encrypted with Windows DPAPI) |
| `%LOCALAPPDATA%\PDC\HomeConnect\` | Tray app logs, notification images |

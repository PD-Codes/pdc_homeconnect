"""Generate Home Assistant translation files, icons and entity docs from one source.

Run from the repository root:  python scripts/generate_ha_strings.py

Outputs:
  custom_components/pdc_homeconnect/strings.json
  custom_components/pdc_homeconnect/translations/en.json
  custom_components/pdc_homeconnect/translations/de.json
  custom_components/pdc_homeconnect/icons.json
  custom_components/pdc_homeconnect/translation_keys.py
  docs/ENTITIES.md
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
COMPONENT = ROOT / "custom_components" / "pdc_homeconnect"

# Health states shared by several sensors.
HEALTH = {
    "healthy": ("Healthy", "Gesund"),
    "warning": ("Warning", "Warnung"),
    "unhealthy": ("Unhealthy", "Fehlerhaft"),
    "unknown": ("Unknown", "Unbekannt"),
}

VM_STATES = {
    "running": ("Running", "Läuft"),
    "off": ("Off", "Aus"),
    "saved": ("Saved", "Gespeichert"),
    "paused": ("Paused", "Pausiert"),
    "starting": ("Starting", "Startet"),
    "stopping": ("Stopping", "Fährt herunter"),
    "saving": ("Saving", "Speichert"),
    "pausing": ("Pausing", "Wird pausiert"),
    "resuming": ("Resuming", "Wird fortgesetzt"),
    "other": ("Other", "Sonstiges"),
}

# Entity definitions: platform -> key -> (english, german, icon, states/event types, source)
# source: "service" (system level), "agent" (user session), "integration" (Home Assistant side)
ENTITIES: dict[str, dict[str, dict]] = {
    "sensor": {
        "cpu_load": {"en": "CPU load", "de": "CPU-Auslastung", "icon": "mdi:cpu-64-bit", "src": "service"},
        "cpu_temperature": {"en": "CPU temperature", "de": "CPU-Temperatur", "icon": "mdi:thermometer", "src": "service"},
        "cpu_power": {"en": "CPU power", "de": "CPU-Leistung", "icon": "mdi:flash", "src": "service"},
        "cpu_clock": {"en": "CPU clock", "de": "CPU-Takt", "icon": "mdi:speedometer", "src": "service"},
        "memory_usage": {"en": "Memory usage", "de": "Arbeitsspeicher-Auslastung", "icon": "mdi:memory", "src": "service"},
        "memory_used": {"en": "Memory used", "de": "Arbeitsspeicher belegt", "icon": "mdi:memory", "src": "service"},
        "disk_usage": {"en": "Disk {disk} usage", "de": "Laufwerk {disk} belegt", "icon": "mdi:harddisk", "src": "service"},
        "network_download": {"en": "Network download", "de": "Netzwerk-Download", "icon": "mdi:download-network", "src": "service"},
        "network_upload": {"en": "Network upload", "de": "Netzwerk-Upload", "icon": "mdi:upload-network", "src": "service"},
        "last_boot": {"en": "Last boot", "de": "Letzter Start", "icon": "mdi:clock-start", "src": "service"},
        "battery": {"en": "Battery", "de": "Akku", "icon": None, "src": "service"},
        "gpu_load": {"en": "GPU load", "de": "GPU-Auslastung", "icon": "mdi:expansion-card", "src": "service"},
        "gpu_temperature": {"en": "GPU temperature", "de": "GPU-Temperatur", "icon": "mdi:thermometer", "src": "service"},
        "gpu_hotspot_temperature": {"en": "GPU hotspot temperature", "de": "GPU-Hotspot-Temperatur", "icon": "mdi:thermometer-high", "src": "service"},
        "gpu_memory_usage": {"en": "GPU memory usage", "de": "GPU-Speicher-Auslastung", "icon": "mdi:memory", "src": "service"},
        "gpu_memory_used": {"en": "GPU memory used", "de": "GPU-Speicher belegt", "icon": "mdi:memory", "src": "service"},
        "gpu_power": {"en": "GPU power", "de": "GPU-Leistung", "icon": "mdi:flash", "src": "service"},
        "gpu_fan": {"en": "GPU fan", "de": "GPU-Lüfter", "icon": "mdi:fan", "src": "service"},
        "gpu_clock": {"en": "GPU clock", "de": "GPU-Takt", "icon": "mdi:speedometer", "src": "service"},
        "gpu_n_load": {"en": "GPU {index} load", "de": "GPU {index} Auslastung", "icon": "mdi:expansion-card", "src": "service"},
        "gpu_n_temperature": {"en": "GPU {index} temperature", "de": "GPU {index} Temperatur", "icon": "mdi:thermometer", "src": "service"},
        "gpu_n_hotspot_temperature": {"en": "GPU {index} hotspot temperature", "de": "GPU {index} Hotspot-Temperatur", "icon": "mdi:thermometer-high", "src": "service"},
        "gpu_n_memory_usage": {"en": "GPU {index} memory usage", "de": "GPU {index} Speicher-Auslastung", "icon": "mdi:memory", "src": "service"},
        "gpu_n_memory_used": {"en": "GPU {index} memory used", "de": "GPU {index} Speicher belegt", "icon": "mdi:memory", "src": "service"},
        "gpu_n_power": {"en": "GPU {index} power", "de": "GPU {index} Leistung", "icon": "mdi:flash", "src": "service"},
        "gpu_n_fan": {"en": "GPU {index} fan", "de": "GPU {index} Lüfter", "icon": "mdi:fan", "src": "service"},
        "gpu_n_clock": {"en": "GPU {index} clock", "de": "GPU {index} Takt", "icon": "mdi:speedometer", "src": "service"},
        "fan": {"en": "Fan {name}", "de": "Lüfter {name}", "icon": "mdi:fan", "src": "service"},
        "drive_temperature": {"en": "{drive} temperature", "de": "{drive} Temperatur", "icon": "mdi:thermometer", "src": "service"},
        "drive_health": {"en": "{drive} health", "de": "{drive} Zustand", "icon": "mdi:harddisk", "states": HEALTH, "src": "service"},
        "drive_life_remaining": {"en": "{drive} life remaining", "de": "{drive} Restlebensdauer", "icon": "mdi:heart-pulse", "src": "service"},
        "session_state": {
            "en": "Session", "de": "Sitzung", "icon": "mdi:account-lock", "src": "service",
            "states": {
                "active": ("Active", "Aktiv"),
                "locked": ("Locked", "Gesperrt"),
                "disconnected": ("Disconnected", "Getrennt"),
                "logged_off": ("Logged off", "Abgemeldet"),
            },
            "state_icons": {"active": "mdi:account-check", "locked": "mdi:lock", "logged_off": "mdi:account-off"},
        },
        "windows_updates": {"en": "Windows updates", "de": "Windows-Updates", "icon": "mdi:microsoft-windows", "src": "service"},
        "defender_status": {
            "en": "Defender", "de": "Defender", "icon": "mdi:shield-check", "src": "service",
            "states": {
                "ok": ("OK", "OK"),
                "warning": ("Warning", "Warnung"),
                "outdated": ("Signatures outdated", "Signaturen veraltet"),
                "disabled": ("Disabled", "Deaktiviert"),
                "action_required": ("Action required", "Aktion erforderlich"),
                "passive": ("Passive (third-party antivirus)", "Passiv (anderer Virenschutz)"),
                "unknown": ("Unknown", "Unbekannt"),
            },
            "state_icons": {"warning": "mdi:shield-alert", "outdated": "mdi:shield-alert", "disabled": "mdi:shield-off", "action_required": "mdi:shield-alert"},
        },
        "user_sessions": {"en": "User sessions", "de": "Benutzersitzungen", "icon": "mdi:account-multiple", "src": "service"},
        "event_log_errors": {"en": "Event log errors (24 h)", "de": "Ereignisprotokoll-Fehler (24 h)", "icon": "mdi:alert-circle", "src": "service"},
        "failed_tasks": {"en": "Failed scheduled tasks", "de": "Fehlgeschlagene Aufgaben", "icon": "mdi:calendar-alert", "src": "service"},
        "last_backup": {"en": "Last backup", "de": "Letzte Sicherung", "icon": "mdi:backup-restore", "src": "service"},
        "storage_pool_health": {"en": "Storage pool {pool}", "de": "Speicherpool {pool}", "icon": "mdi:database", "states": HEALTH, "src": "service"},
        "virtual_disk_health": {"en": "Virtual disk {disk}", "de": "Virtueller Datenträger {disk}", "icon": "mdi:harddisk", "states": HEALTH, "src": "service"},
        "vm_state": {"en": "VM {vm} state", "de": "VM {vm} Status", "icon": "mdi:server", "states": VM_STATES, "src": "service"},
        "vm_cpu": {"en": "VM {vm} CPU", "de": "VM {vm} CPU", "icon": "mdi:cpu-64-bit", "src": "service"},
        "vm_memory": {"en": "VM {vm} memory", "de": "VM {vm} Arbeitsspeicher", "icon": "mdi:memory", "src": "service"},
        "smb_sessions": {"en": "SMB sessions", "de": "SMB-Sitzungen", "icon": "mdi:folder-network", "src": "service"},
        "smb_open_files": {"en": "SMB open files", "de": "Geöffnete SMB-Dateien", "icon": "mdi:file-lock", "src": "service"},
        "plex_streams": {"en": "Plex streams", "de": "Plex-Streams", "icon": "mdi:plex", "src": "service"},
        "plex_transcodes": {"en": "Plex transcodes", "de": "Plex-Transkodierungen", "icon": "mdi:plex", "src": "service"},
        "plex_bandwidth": {"en": "Plex bandwidth", "de": "Plex-Bandbreite", "icon": "mdi:speedometer", "src": "service"},
        "plex_library": {"en": "Plex {library}", "de": "Plex {library}", "icon": "mdi:filmstrip-box-multiple", "src": "service"},
        "foreground_app": {"en": "Foreground app", "de": "Aktive App", "icon": "mdi:application", "src": "agent"},
        "open_apps": {"en": "Open apps", "de": "Offene Apps", "icon": "mdi:apps", "src": "agent"},
        "idle_time": {"en": "Idle time", "de": "Inaktivität", "icon": "mdi:timer-sand", "src": "agent"},
        "fullscreen_state": {
            "en": "Fullscreen", "de": "Vollbild", "icon": "mdi:fullscreen", "src": "agent",
            "states": {
                "none": ("None", "Keins"),
                "fullscreen": ("Fullscreen app", "Vollbild-App"),
                "game": ("Game (exclusive)", "Spiel (exklusiv)"),
                "presentation": ("Presentation", "Präsentation"),
                "quiet_time": ("Quiet time", "Ruhezeit"),
                "app": ("Store app", "Store-App"),
                "away": ("Away", "Abwesend"),
            },
        },
        "activity": {
            "en": "Activity", "de": "Aktivität", "icon": "mdi:account-clock", "src": "agent",
            "states": {
                "locked": ("Locked", "Gesperrt"),
                "gaming": ("Gaming", "Zockt"),
                "call": ("In a call", "Im Call"),
                "afk": ("Away (AFK)", "Abwesend (AFK)"),
                "working": ("Working", "Arbeitet"),
                "media": ("Media", "Medien"),
                "active": ("Active", "Aktiv"),
                "logged_off": ("Logged off", "Abgemeldet"),
                "unknown": ("Unknown", "Unbekannt"),
            },
            "state_icons": {
                "locked": "mdi:lock", "gaming": "mdi:controller", "call": "mdi:phone-in-talk", "afk": "mdi:account-off",
                "working": "mdi:laptop", "media": "mdi:play-circle", "active": "mdi:account-check", "logged_off": "mdi:logout",
            },
        },
        "current_game": {"en": "Current game", "de": "Aktuelles Spiel", "icon": "mdi:gamepad-variant", "src": "agent"},
        "steam_status": {
            "en": "Steam", "de": "Steam", "icon": "mdi:steam", "src": "agent",
            "states": {"offline": ("Offline", "Offline"), "online": ("Online", "Online"), "in_game": ("In game", "Im Spiel")},
        },
        "discord_voice_channel": {"en": "Discord voice channel", "de": "Discord-Sprachkanal", "icon": "mdi:headset", "src": "agent"},
        "claude_code": {
            "en": "Claude Code", "de": "Claude Code", "icon": "mdi:robot", "src": "agent",
            "states": {
                "idle": ("Idle", "Bereit"),
                "working": ("Working", "Arbeitet"),
                "waiting": ("Waiting for input", "Wartet auf Eingabe"),
                "error": ("Error", "Fehler"),
            },
            "state_icons": {"working": "mdi:robot-excited", "waiting": "mdi:robot-confused", "error": "mdi:robot-dead", "idle": "mdi:robot"},
        },
    },
    "binary_sensor": {
        "online": {"en": "Online", "de": "Online", "icon": None, "src": "integration"},
        "pending_reboot": {"en": "Restart pending", "de": "Neustart ausstehend", "icon": "mdi:restart-alert", "src": "service"},
        "battery_charging": {"en": "Charging", "de": "Lädt", "icon": None, "src": "service"},
        "power_plugged": {"en": "Power plugged in", "de": "Netzteil angeschlossen", "icon": None, "src": "service"},
        "afk": {"en": "AFK", "de": "AFK", "icon": "mdi:account-check", "on_icon": "mdi:account-off", "src": "agent"},
        "monitor_power": {"en": "Displays", "de": "Bildschirme", "icon": "mdi:monitor-off", "on_icon": "mdi:monitor", "src": "agent"},
        "webcam_in_use": {"en": "Webcam in use", "de": "Webcam aktiv", "icon": "mdi:webcam-off", "on_icon": "mdi:webcam", "src": "agent"},
        "microphone_in_use": {"en": "Microphone in use", "de": "Mikrofon aktiv", "icon": "mdi:microphone-off", "on_icon": "mdi:microphone", "src": "agent"},
        "gaming": {"en": "Gaming", "de": "Zockt", "icon": "mdi:controller-off", "on_icon": "mdi:controller", "src": "agent"},
        "steam_updating": {"en": "Steam downloading", "de": "Steam lädt herunter", "icon": "mdi:download", "src": "agent"},
        "discord_voice": {"en": "Discord voice", "de": "Discord-Sprachchat", "icon": "mdi:headset-off", "on_icon": "mdi:headset", "src": "agent"},
        "discord_speaking": {"en": "Speaking in Discord", "de": "Spricht in Discord", "icon": "mdi:account-voice", "src": "agent"},
        "app_running": {"en": "{app}", "de": "{app}", "icon": "mdi:application", "src": "agent"},
        "plex_online": {"en": "Plex Media Server", "de": "Plex Media Server", "icon": "mdi:plex", "src": "service"},
        "plex_scanning": {"en": "Plex library scan", "de": "Plex-Bibliothekscan", "icon": "mdi:magnify-scan", "src": "service"},
    },
    "switch": {
        "windows_service": {"en": "Service {service}", "de": "Dienst {service}", "icon": "mdi:cog", "src": "service"},
        "vm": {"en": "VM {vm}", "de": "VM {vm}", "icon": "mdi:server", "src": "service"},
        "iis_site": {"en": "IIS {site}", "de": "IIS {site}", "icon": "mdi:web", "src": "service"},
        "docker_container": {"en": "Container {container}", "de": "Container {container}", "icon": "mdi:docker", "src": "service"},
        "discord_mute": {"en": "Discord mute", "de": "Discord stumm", "icon": "mdi:microphone", "on_icon": "mdi:microphone-off", "src": "agent"},
        "discord_deafen": {"en": "Discord deafen", "de": "Discord taub", "icon": "mdi:headphones", "on_icon": "mdi:headphones-off", "src": "agent"},
        "dark_mode": {"en": "Dark mode", "de": "Dunkelmodus", "icon": "mdi:theme-light-dark", "src": "agent"},
        "privacy_mode": {"en": "Privacy mode", "de": "Privatsphäre-Modus", "icon": "mdi:incognito-off", "on_icon": "mdi:incognito", "src": "agent"},
    },
    "button": {
        "shutdown": {"en": "Shut down", "de": "Herunterfahren", "icon": "mdi:power", "src": "service"},
        "restart": {"en": "Restart", "de": "Neu starten", "icon": "mdi:restart", "src": "service"},
        "sleep": {"en": "Sleep", "de": "Energie sparen", "icon": "mdi:sleep", "src": "service"},
        "hibernate": {"en": "Hibernate", "de": "Ruhezustand", "icon": "mdi:power-sleep", "src": "service"},
        "logoff": {"en": "Sign out", "de": "Abmelden", "icon": "mdi:logout", "src": "service"},
        "restart_firmware": {"en": "Restart to UEFI", "de": "Neustart ins UEFI", "icon": "mdi:chip", "src": "service"},
        "lock": {"en": "Lock", "de": "Sperren", "icon": "mdi:lock", "src": "agent"},
        "monitor_off": {"en": "Turn off displays", "de": "Bildschirme aus", "icon": "mdi:monitor-off", "src": "agent"},
        "monitor_wake": {"en": "Wake displays", "de": "Bildschirme an", "icon": "mdi:monitor", "src": "agent"},
        "discord_leave": {"en": "Leave Discord voice", "de": "Discord-Sprachkanal verlassen", "icon": "mdi:phone-hangup", "src": "agent"},
        "wake_on_lan": {"en": "Wake on LAN", "de": "Wake-on-LAN", "icon": "mdi:lan-connect", "src": "integration"},
        "command": {"en": "{command}", "de": "{command}", "icon": "mdi:console", "src": "service/agent"},
        "plex_scan": {"en": "Scan Plex libraries", "de": "Plex-Bibliotheken scannen", "icon": "mdi:refresh", "src": "service"},
    },
    "number": {
        "monitor_brightness": {"en": "Display {monitor} brightness", "de": "Bildschirm {monitor} Helligkeit", "icon": "mdi:brightness-6", "src": "agent"},
    },
    "select": {
        "audio_output": {"en": "Audio output", "de": "Audioausgabe", "icon": "mdi:speaker", "src": "agent"},
        "audio_input": {"en": "Audio input", "de": "Audioeingabe", "icon": "mdi:microphone", "src": "agent"},
        "power_plan": {"en": "Power plan", "de": "Energiesparplan", "icon": "mdi:lightning-bolt", "src": "service"},
    },
    "media_player": {
        "media": {"en": "Media", "de": "Medien", "icon": None, "src": "agent"},
    },
    "event": {
        "notification_action": {
            "en": "Notification action", "de": "Benachrichtigungsaktion", "icon": "mdi:message-reply-text", "src": "agent",
            "events": {"action": ("Button pressed", "Schaltfläche gedrückt"), "reply": ("Reply", "Antwort"), "dismissed": ("Dismissed", "Verworfen")},
        },
        "hotkey": {"en": "Hotkey", "de": "Tastenkürzel", "icon": "mdi:keyboard", "src": "agent"},
        "tray_action": {"en": "Tray action", "de": "Tray-Aktion", "icon": "mdi:gesture-tap", "src": "agent"},
        "game": {
            "en": "Game", "de": "Spiel", "icon": "mdi:gamepad-variant", "src": "agent",
            "events": {"started": ("Started", "Gestartet"), "stopped": ("Stopped", "Beendet")},
        },
        "claude_code_event": {
            "en": "Claude Code event", "de": "Claude-Code-Ereignis", "icon": "mdi:robot", "src": "agent",
            "events": {
                "prompt_submitted": ("Prompt submitted", "Prompt gesendet"),
                "waiting_for_input": ("Waiting for input", "Wartet auf Eingabe"),
                "finished": ("Finished", "Fertig"),
                "failed": ("Failed", "Fehlgeschlagen"),
                "session_started": ("Session started", "Sitzung gestartet"),
                "session_ended": ("Session ended", "Sitzung beendet"),
            },
        },
        "system_event": {
            "en": "System event", "de": "Systemereignis", "icon": "mdi:alert", "src": "service",
            "events": {"critical": ("Critical", "Kritisch"), "error": ("Error", "Fehler")},
        },
        "plex_playback": {
            "en": "Plex playback", "de": "Plex-Wiedergabe", "icon": "mdi:plex", "src": "service",
            "events": {"started": ("Started", "Gestartet"), "stopped": ("Stopped", "Beendet"), "paused": ("Paused", "Pausiert"), "resumed": ("Resumed", "Fortgesetzt")},
        },
    },
    "update": {
        "client": {"en": "Client", "de": "Client", "icon": None, "src": "integration"},
    },
    "notify": {
        "notify": {"en": "Notification", "de": "Benachrichtigung", "icon": "mdi:message-badge", "src": "integration"},
    },
}

CONFIG = {
    "en": {
        "flow_title": "{name}",
        "step": {
            "user": {
                "title": "Connect a PC",
                "description": "Enter the address of the PC that runs the PDC HomeConnect client.",
                "data": {"host": "Host", "port": "Port"},
                "data_description": {
                    "host": "IP address or host name of the PC.",
                    "port": "Port of the PDC HomeConnect client (default 48733).",
                },
            },
            "zeroconf_confirm": {
                "title": "PDC HomeConnect",
                "description": "Do you want to connect **{name}**?",
            },
            "pair": {
                "title": "Pair with {name}",
                "description": "A 6-digit pairing code is now shown on **{name}** (Windows notification and tray icon). Enter it below. The code is valid for 5 minutes.",
                "data": {"code": "Pairing code"},
            },
            "reauth_confirm": {
                "title": "Re-pair {name}",
                "description": "**{name}** no longer accepts the stored credentials (for example after a reinstall or when the pairing was removed). Continue to request a new pairing code.",
            },
            "reconfigure": {
                "title": "Change connection",
                "description": "Change the address of **{name}**.",
                "data": {"host": "Host", "port": "Port"},
            },
        },
        "error": {
            "cannot_connect": "Could not connect. Check the address, that the client is running and that the firewall allows the connection.",
            "invalid_code": "The code is not correct.",
            "no_active_code": "The pairing code expired. A new code is now shown on the PC.",
            "too_many_attempts": "Too many wrong codes. A new code is now shown on the PC.",
            "wrong_device": "This address belongs to a different PC.",
            "unknown": "Unexpected error.",
        },
        "abort": {
            "already_configured": "This PC is already configured.",
            "reauth_successful": "Re-pairing was successful.",
            "reconfigure_successful": "The connection was updated.",
            "cannot_connect": "Could not connect to the PC.",
            "unsupported_protocol": "The client version is not compatible with this integration. Update both to the latest version.",
        },
    },
    "de": {
        "flow_title": "{name}",
        "step": {
            "user": {
                "title": "PC verbinden",
                "description": "Gib die Adresse des PCs ein, auf dem der PDC-HomeConnect-Client läuft.",
                "data": {"host": "Host", "port": "Port"},
                "data_description": {
                    "host": "IP-Adresse oder Hostname des PCs.",
                    "port": "Port des PDC-HomeConnect-Clients (Standard 48733).",
                },
            },
            "zeroconf_confirm": {
                "title": "PDC HomeConnect",
                "description": "Möchtest du **{name}** verbinden?",
            },
            "pair": {
                "title": "Mit {name} koppeln",
                "description": "Auf **{name}** wird jetzt ein 6-stelliger Kopplungscode angezeigt (Windows-Benachrichtigung und Tray-Symbol). Gib ihn hier ein. Der Code ist 5 Minuten gültig.",
                "data": {"code": "Kopplungscode"},
            },
            "reauth_confirm": {
                "title": "{name} neu koppeln",
                "description": "**{name}** akzeptiert die gespeicherten Zugangsdaten nicht mehr (z. B. nach einer Neuinstallation oder wenn die Kopplung entfernt wurde). Fahre fort, um einen neuen Kopplungscode anzufordern.",
            },
            "reconfigure": {
                "title": "Verbindung ändern",
                "description": "Ändere die Adresse von **{name}**.",
                "data": {"host": "Host", "port": "Port"},
            },
        },
        "error": {
            "cannot_connect": "Verbindung fehlgeschlagen. Prüfe die Adresse, ob der Client läuft und ob die Firewall die Verbindung erlaubt.",
            "invalid_code": "Der Code ist falsch.",
            "no_active_code": "Der Kopplungscode ist abgelaufen. Auf dem PC wird jetzt ein neuer Code angezeigt.",
            "too_many_attempts": "Zu viele falsche Codes. Auf dem PC wird jetzt ein neuer Code angezeigt.",
            "wrong_device": "Diese Adresse gehört zu einem anderen PC.",
            "unknown": "Unerwarteter Fehler.",
        },
        "abort": {
            "already_configured": "Dieser PC ist bereits eingerichtet.",
            "reauth_successful": "Die Neukopplung war erfolgreich.",
            "reconfigure_successful": "Die Verbindung wurde aktualisiert.",
            "cannot_connect": "Keine Verbindung zum PC möglich.",
            "unsupported_protocol": "Die Client-Version passt nicht zu dieser Integration. Aktualisiere beide auf die neueste Version.",
        },
    },
}

DEVICE_FIELD = {
    "en": ("PC", "The PC(s) to send this action to."),
    "de": ("PC", "Der/die PC(s), an den/die diese Aktion gesendet wird."),
}

# Actions: key -> {lang: (name, description, {field: (name, description)})}
SERVICES = {
    "notify": {
        "icon": "mdi:message-badge",
        "en": ("Send notification", "Shows a Windows notification on the PC.", {
            "message": ("Message", "Text of the notification."),
            "title": ("Title", "Title of the notification."),
            "image": ("Image", "Camera entity (camera.xyz), image URL or /local/... path."),
            "actions": ("Buttons", "Up to 5 buttons: a list of {action, title}. A click fires the notification action event."),
            "reply": ("Reply field", "Placeholder text for a reply text box. The reply fires the notification action event."),
            "scenario": ("Scenario", "Reminder and alarm stay on screen until dismissed."),
            "sound": ("Sound", "Notification sound."),
            "duration": ("Duration", "How long the notification stays on screen."),
            "tag": ("Tag", "Identifier to replace or clear this notification later."),
            "fullscreen_fallback": ("Fullscreen fallback", "What to do when Windows hides notifications because a fullscreen app or game is running."),
        }),
        "de": ("Benachrichtigung senden", "Zeigt eine Windows-Benachrichtigung auf dem PC an.", {
            "message": ("Nachricht", "Text der Benachrichtigung."),
            "title": ("Titel", "Titel der Benachrichtigung."),
            "image": ("Bild", "Kamera-Entität (camera.xyz), Bild-URL oder /local/...-Pfad."),
            "actions": ("Schaltflächen", "Bis zu 5 Schaltflächen: Liste aus {action, title}. Ein Klick löst das Benachrichtigungsaktions-Ereignis aus."),
            "reply": ("Antwortfeld", "Platzhaltertext für ein Antwort-Textfeld. Die Antwort löst das Benachrichtigungsaktions-Ereignis aus."),
            "scenario": ("Szenario", "Erinnerung und Alarm bleiben sichtbar, bis sie geschlossen werden."),
            "sound": ("Ton", "Benachrichtigungston."),
            "duration": ("Dauer", "Wie lange die Benachrichtigung sichtbar bleibt."),
            "tag": ("Tag", "Kennung, um diese Benachrichtigung später zu ersetzen oder zu löschen."),
            "fullscreen_fallback": ("Vollbild-Ersatz", "Was passieren soll, wenn Windows Benachrichtigungen wegen einer Vollbild-App oder eines Spiels unterdrückt."),
        }),
    },
    "clear_notifications": {
        "icon": "mdi:message-off",
        "en": ("Clear notifications", "Removes notifications from the Windows notification center.", {
            "tag": ("Tag", "Only remove the notification with this tag. Leave empty to remove all."),
        }),
        "de": ("Benachrichtigungen löschen", "Entfernt Benachrichtigungen aus dem Windows-Benachrichtigungscenter.", {
            "tag": ("Tag", "Nur die Benachrichtigung mit diesem Tag entfernen. Leer lassen, um alle zu entfernen."),
        }),
    },
    "open_url": {
        "icon": "mdi:open-in-new",
        "en": ("Open URL", "Opens a URL with its default app (browser, Steam, Discord, ...).", {
            "url": ("URL", "The URL to open. Allowed schemes are configured on the PC."),
        }),
        "de": ("URL öffnen", "Öffnet eine URL mit der Standard-App (Browser, Steam, Discord, ...).", {
            "url": ("URL", "Die zu öffnende URL. Erlaubte Schemata werden am PC konfiguriert."),
        }),
    },
    "speak": {
        "icon": "mdi:account-voice",
        "en": ("Speak", "Reads a text aloud on the PC using a Windows voice.", {
            "message": ("Message", "Text to speak."),
            "language": ("Language", "Voice language, e.g. de-DE or en-US."),
        }),
        "de": ("Sprechen", "Liest einen Text mit einer Windows-Stimme auf dem PC vor.", {
            "message": ("Nachricht", "Zu sprechender Text."),
            "language": ("Sprache", "Sprache der Stimme, z. B. de-DE oder en-US."),
        }),
    },
    "set_clipboard": {
        "icon": "mdi:clipboard-text",
        "en": ("Set clipboard", "Copies text to the clipboard of the PC.", {
            "text": ("Text", "Text to copy."),
        }),
        "de": ("Zwischenablage setzen", "Kopiert Text in die Zwischenablage des PCs.", {
            "text": ("Text", "Zu kopierender Text."),
        }),
    },
    "power": {
        "icon": "mdi:power",
        "en": ("Power action", "Shuts down, restarts or suspends the PC, optionally with a delay.", {
            "action": ("Action", "What to do."),
            "delay": ("Delay", "Seconds to wait (the user sees a warning)."),
            "force": ("Force", "Close apps without asking (unsaved work is lost)."),
        }),
        "de": ("Energieaktion", "Fährt den PC herunter, startet ihn neu oder versetzt ihn in den Energiesparmodus, optional verzögert.", {
            "action": ("Aktion", "Was passieren soll."),
            "delay": ("Verzögerung", "Wartezeit in Sekunden (der Benutzer sieht eine Warnung)."),
            "force": ("Erzwingen", "Apps ohne Nachfrage schließen (nicht gespeicherte Daten gehen verloren)."),
        }),
    },
    "run_command": {
        "icon": "mdi:console",
        "en": ("Run command", "Runs a command that is defined in the client configuration on the PC.", {
            "command": ("Command", "Id of the command from the PC configuration."),
        }),
        "de": ("Befehl ausführen", "Führt einen Befehl aus, der in der Client-Konfiguration auf dem PC definiert ist.", {
            "command": ("Befehl", "Id des Befehls aus der PC-Konfiguration."),
        }),
    },
    "vm_control": {
        "icon": "mdi:server",
        "en": ("Control virtual machine", "Starts, stops, saves or pauses a Hyper-V virtual machine.", {
            "vm": ("Virtual machine", "Name of the virtual machine."),
            "action": ("Action", "What to do."),
        }),
        "de": ("Virtuelle Maschine steuern", "Startet, stoppt, speichert oder pausiert eine Hyper-V-VM.", {
            "vm": ("Virtuelle Maschine", "Name der virtuellen Maschine."),
            "action": ("Aktion", "Was passieren soll."),
        }),
    },
    "service_control": {
        "icon": "mdi:cog",
        "en": ("Control Windows service", "Starts, stops or restarts a watched Windows service.", {
            "service": ("Service", "Service name (must be in the watched services list on the PC)."),
            "action": ("Action", "What to do."),
        }),
        "de": ("Windows-Dienst steuern", "Startet, stoppt oder startet einen überwachten Windows-Dienst neu.", {
            "service": ("Dienst", "Dienstname (muss in der Liste der überwachten Dienste am PC stehen)."),
            "action": ("Aktion", "Was passieren soll."),
        }),
    },
}

SELECTOR_OPTIONS = {
    "scenario": {
        "default": ("Default", "Standard"),
        "reminder": ("Reminder", "Erinnerung"),
        "alarm": ("Alarm", "Alarm"),
        "urgent": ("Urgent", "Dringend"),
    },
    "sound": {
        "default": ("Default", "Standard"),
        "silent": ("Silent", "Stumm"),
        "im": ("Message", "Nachricht"),
        "mail": ("Mail", "Mail"),
        "reminder": ("Reminder", "Erinnerung"),
        "sms": ("SMS", "SMS"),
        "alarm": ("Alarm", "Alarm"),
        "call": ("Call", "Anruf"),
    },
    "duration": {"short": ("Short", "Kurz"), "long": ("Long", "Lang")},
    "fullscreen_fallback": {
        "auto": ("Notification only", "Nur Benachrichtigung"),
        "sound": ("Play a sound", "Ton abspielen"),
        "speak": ("Read aloud", "Vorlesen"),
    },
    "power_action": {
        "shutdown": ("Shut down", "Herunterfahren"),
        "restart": ("Restart", "Neu starten"),
        "sleep": ("Sleep", "Energie sparen"),
        "hibernate": ("Hibernate", "Ruhezustand"),
        "logoff": ("Sign out", "Abmelden"),
        "abort": ("Abort pending shutdown", "Geplantes Herunterfahren abbrechen"),
    },
    "vm_action": {
        "start": ("Start", "Starten"),
        "shutdown": ("Shut down (guest)", "Herunterfahren (Gast)"),
        "turn_off": ("Turn off (hard)", "Ausschalten (hart)"),
        "save": ("Save", "Speichern"),
        "pause": ("Pause", "Pausieren"),
        "resume": ("Resume", "Fortsetzen"),
        "reset": ("Reset", "Zurücksetzen"),
    },
    "service_action": {
        "start": ("Start", "Starten"),
        "stop": ("Stop", "Stoppen"),
        "restart": ("Restart", "Neu starten"),
    },
}

EXCEPTIONS = {
    "not_connected": ("{name} is not connected.", "{name} ist nicht verbunden."),
    "command_failed": ("The PC reported an error: {error}", "Der PC meldet einen Fehler: {error}"),
    "no_devices": ("No PDC HomeConnect device selected.", "Kein PDC-HomeConnect-Gerät ausgewählt."),
    "image_failed": ("Could not load the image: {error}", "Das Bild konnte nicht geladen werden: {error}"),
}

ISSUES = {
    "client_outdated": (
        ("Client on {name} is outdated", "The PDC HomeConnect client on {name} uses protocol {client_protocol}, the integration expects {protocol}. Update the client on the PC."),
        ("Client auf {name} ist veraltet", "Der PDC-HomeConnect-Client auf {name} verwendet Protokoll {client_protocol}, die Integration erwartet {protocol}. Aktualisiere den Client auf dem PC."),
    ),
    "integration_outdated": (
        ("Integration is older than the client on {name}", "The client on {name} uses protocol {client_protocol}, this integration only supports {protocol}. Update the integration via HACS."),
        ("Integration ist älter als der Client auf {name}", "Der Client auf {name} verwendet Protokoll {client_protocol}, diese Integration unterstützt nur {protocol}. Aktualisiere die Integration über HACS."),
    ),
}


def lang_index(lang: str) -> int:
    return 0 if lang == "en" else 1


def build_strings(lang: str) -> dict:
    idx = lang_index(lang)
    entity: dict[str, dict] = {}
    for platform, items in ENTITIES.items():
        platform_out: dict[str, dict] = {}
        for key, item in items.items():
            entry: dict = {"name": item[lang]}
            if "states" in item:
                entry["state"] = {k: v[idx] for k, v in item["states"].items()}
            if "events" in item:
                entry["state_attributes"] = {
                    "event_type": {"state": {k: v[idx] for k, v in item["events"].items()}}
                }
            platform_out[key] = entry
        entity[platform] = platform_out

    services: dict[str, dict] = {}
    for key, svc in SERVICES.items():
        name, description, fields = svc[lang]
        out_fields = {
            "device_id": {"name": DEVICE_FIELD[lang][0], "description": DEVICE_FIELD[lang][1]}
        }
        for field, (fname, fdesc) in fields.items():
            out_fields[field] = {"name": fname, "description": fdesc}
        services[key] = {"name": name, "description": description, "fields": out_fields}

    selector = {
        key: {"options": {opt: names[idx] for opt, names in options.items()}}
        for key, options in SELECTOR_OPTIONS.items()
    }

    return {
        "config": CONFIG[lang],
        "entity": entity,
        "services": services,
        "selector": selector,
        "exceptions": {k: {"message": v[idx]} for k, v in EXCEPTIONS.items()},
        "issues": {
            k: {"title": v[idx][0], "description": v[idx][1]} for k, v in ISSUES.items()
        },
    }


def build_icons() -> dict:
    entity: dict[str, dict] = {}
    for platform, items in ENTITIES.items():
        out: dict[str, dict] = {}
        for key, item in items.items():
            icon = item.get("icon")
            if not icon:
                continue
            entry: dict = {"default": icon}
            if item.get("on_icon"):
                entry["state"] = {"on": item["on_icon"]}
            if item.get("state_icons"):
                entry["state"] = dict(item["state_icons"])
            out[key] = entry
        if out:
            entity[platform] = out
    return {
        "entity": entity,
        "services": {key: {"service": svc["icon"]} for key, svc in SERVICES.items()},
    }


def build_translation_keys() -> str:
    lines = [
        '"""Translation keys known to this integration. Generated by scripts/generate_ha_strings.py."""',
        "",
        "from __future__ import annotations",
        "",
        "# platform -> translation key -> required name placeholders",
        "TRANSLATION_KEYS: dict[str, dict[str, frozenset[str]]] = {",
    ]
    for platform, items in ENTITIES.items():
        lines.append(f'    "{platform}": {{')
        for key, item in items.items():
            placeholders = sorted(set(re.findall(r"\{(\w+)\}", item["en"])))
            inner = ", ".join(f'"{p}"' for p in placeholders)
            lines.append(f'        "{key}": frozenset({{{inner}}}),' if placeholders else f'        "{key}": frozenset(),')
        lines.append("    },")
    lines.append("}")
    lines.append("")
    return "\n".join(lines)


def build_docs() -> str:
    out = [
        "# Entities",
        "",
        "Generated by `scripts/generate_ha_strings.py`. Which entities appear depends on the hardware and",
        "on the features that are enabled on the PC (see [CONFIGURATION.md](CONFIGURATION.md)).",
        "",
        "Source: **service** = Windows service (works without a signed-in user), **agent** = tray app in the",
        "user session, **integration** = created by Home Assistant itself.",
        "",
    ]
    for platform, items in ENTITIES.items():
        out.append(f"## {platform}")
        out.append("")
        out.append("| Key | Name (en) | Name (de) | Source | States / events |")
        out.append("| --- | --- | --- | --- | --- |")
        for key, item in items.items():
            extra = ""
            if "states" in item:
                extra = ", ".join(f"`{s}`" for s in item["states"])
            if "events" in item:
                extra = ", ".join(f"`{s}`" for s in item["events"])
            out.append(f"| `{key}` | {item['en']} | {item['de']} | {item['src']} | {extra} |")
        out.append("")
    return "\n".join(out)


def json_text(data: dict) -> str:
    return json.dumps(data, indent=2, ensure_ascii=False) + "\n"


def main() -> None:
    en = build_strings("en")
    outputs: dict[Path, str] = {
        COMPONENT / "strings.json": json_text(en),
        COMPONENT / "translations" / "en.json": json_text(en),
        COMPONENT / "translations" / "de.json": json_text(build_strings("de")),
        COMPONENT / "icons.json": json_text(build_icons()),
        COMPONENT / "translation_keys.py": build_translation_keys(),
        ROOT / "docs" / "ENTITIES.md": build_docs() + "\n",
    }

    if "--check" in sys.argv:
        # CI: fail if the generated files are out of date.
        stale = [
            str(path.relative_to(ROOT))
            for path, text in outputs.items()
            if not path.exists() or path.read_text(encoding="utf-8") != text
        ]
        if stale:
            print("Generated files are out of date, run scripts/generate_ha_strings.py:")
            print("\n".join(f"  {name}" for name in stale))
            sys.exit(1)
        print("Generated files are up to date.")
        return

    for path, text in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    print("Generated translations, icons and docs.")


if __name__ == "__main__":
    main()

# Development

## Repository layout

| Path | Content |
| --- | --- |
| `custom_components/pdc_homeconnect/` | Home Assistant integration |
| `tests/` | Integration tests (pytest-homeassistant-custom-component) |
| `scripts/generate_ha_strings.py` | Single source of entity names, translations (en/de), icons and `docs/ENTITIES.md` |
| `scripts/generate_assets.py` | App/tray icons and brand images |
| `client/src/PdcHomeConnect.Core` | Protocol, entity hub, WebSocket server, pairing, mDNS, certificates |
| `client/src/PdcHomeConnect.Service` | Windows service (LocalSystem): system/hardware/server providers, agent bridge |
| `client/src/PdcHomeConnect.Agent` | Tray app (user session): apps, games, Discord, media, notifications, Claude Code |
| `client/src/PdcHomeConnect.Hook` | `pdc-hc-hook.exe`, forwards Claude Code hook events to the tray app |
| `client/src/PdcHomeConnect.DevHost` | Cross-platform host with simulated entities for integration development |
| `client/tests` | .NET unit tests |
| `client/installer` | Inno Setup script and icons |

## Architecture

```
Home Assistant ──TLS WebSocket (48733)──► Service (LocalSystem) ◄──named pipe──► Tray app (each user session)
                                              │                                     ▲
                                              └─ mDNS _pdc-homeconnect._tcp         └── pdc-hc-hook.exe (Claude Code hooks)
```

- The service owns the connection to Home Assistant. Providers publish entity descriptors and states into the
  `EntityHub`; changes are pushed to Home Assistant in batches (50 ms).
- Tray apps connect to the service pipe `PdcHomeConnect.Service`. The app of the console session is the
  "primary" agent: its entities are forwarded. The service caches the agent entities so they stay in Home
  Assistant (with an offline state) while nobody is signed in.
- Entity names come from translation keys (`scripts/generate_ha_strings.py`), with a name fallback.

## Home Assistant integration

```bash
python -m venv .venv && . .venv/bin/activate
pip install -r requirements_test.txt
python scripts/generate_ha_strings.py   # after changing entity names or translations
pytest
```

End-to-end test against the real C# hub (DevHost, runs on Linux/macOS too):

```bash
cd client
PDC_PORT=48800 PDC_DATA=/tmp/pdc dotnet run --project src/PdcHomeConnect.DevHost
# second terminal, repo root:
PDC_DEVHOST_PORT=48800 PDC_DEVHOST_DATA=/tmp/pdc pytest tests/test_e2e_devhost.py
```

DevHost environment variables: `PDC_PORT`, `PDC_DATA` (pairing code in `pairing-code.txt`), `PDC_MDNS` (`1` = announce),
`PDC_NAME`.

## Windows client

Requirements: .NET 10 SDK, Inno Setup 6 (only for the installer).

```powershell
cd client
dotnet build PdcHomeConnect.slnx
dotnet test PdcHomeConnect.slnx
.\build.ps1            # publish + installer + SHA256SUMS.txt in client\artifacts
```

For debugging the service as a console app, run `PdcHomeConnect.Service.exe` from an elevated terminal
(it detects that it is not running as a Windows service). Set `"allow_any_agent": true` in `service.json`
to connect a tray app started from the build output folder.

## Releases

1. Set the same version in `custom_components/pdc_homeconnect/manifest.json` and `client/Directory.Build.props`.
2. Create a GitHub release with the tag `vX.Y.Z`.
3. The release workflow builds the installer and attaches `PDC-HomeConnect-Setup-X.Y.Z.exe` and `SHA256SUMS.txt`.
   HACS offers the integration update; the client can then be updated from Home Assistant (update entity).

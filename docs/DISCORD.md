# Discord voice status

Without any setup, PDC HomeConnect detects that you are in a Discord voice channel because Discord uses the
microphone (`Discord voice` binary sensor).

For more details and control it can talk to the Discord desktop app over Discord's local RPC interface:

- Voice channel name and server, members in the channel
- Mute and deafen switches
- Leave the voice channel (button)
- Speaking (binary sensor, disabled by default)

Discord only allows this for applications you own, so you need your own (free) Discord application.
This takes about two minutes and is done once.

## Setup

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and sign in.
2. Click **New Application**, enter a name (e.g. `HomeConnect`) and create it.
3. Open **OAuth2** in the menu on the left:
   - Copy the **Client ID**.
   - Click **Reset Secret** and copy the **Client Secret**.
   - Under **Redirects**, add `http://127.0.0.1` and click **Save Changes**.
4. On the PC: tray icon → **Discord…** → paste the Client ID and the Client Secret → **Connect**.
5. Discord shows an authorization dialog – click **Authorize**.

The status changes to "connected" and the new entities appear in Home Assistant. The tokens are stored encrypted
for your Windows user (DPAPI) and are refreshed automatically.

## Notes

- The Discord desktop app must be running. Discord in the browser is not supported.
- Every Windows user who wants the rich status needs to connect once (the same Discord application can be reused).
- If you use a different redirect URL in your application, set it in `agent.json` (`discord.redirect_uri`).
- "Remove" in the dialog deletes the stored credentials; the microphone based detection is used again.
- Problems are logged in `%LOCALAPPDATA%\PDC\HomeConnect\logs`.

# Protocol (version 1)

JSON messages (snake_case) over a TLS WebSocket: `wss://<pc>:48733/ws`. The certificate is self-signed; Home
Assistant pins its SHA-256 fingerprint at pairing time.

## Connection

```
PC → HA   {"type":"hello","protocol":1,"device":{...,"cert_sha256":"..."}}
HA → PC   {"type":"hello","client":"Home Assistant","version":"2026.10.0","protocol":1}
HA → PC   {"type":"auth","token":"..."}
PC → HA   {"type":"auth_ok"} | {"type":"auth_failed","reason":"invalid_token"}
PC → HA   {"type":"device",...} {"type":"entities","revision":3,"entities":[...]} {"type":"states","states":{...}}
```

Pairing (instead of `auth`):

```
HA → PC   {"type":"pair_request","client_name":"Home Assistant"}
PC → HA   {"type":"pair_pending","expires_in":300}          (code is shown on the PC)
HA → PC   {"type":"pair","code":"123456","client_name":"Home Assistant"}
PC → HA   {"type":"pair_ok","token":"..."} | {"type":"pair_failed","reason":"invalid_code|no_active_code|too_many_attempts"}
```

## Messages from the PC

| Type | Content |
| --- | --- |
| `device` | Device info (id, name, os, version, macs, agent state) |
| `entities` | Complete list of entity descriptors (`key`, `platform`, `translation_key`, `placeholders`, `name`, `device_class`, `unit`, `options`, `event_types`, `features`, ...) |
| `states` | `{"states":{"cpu_load":{"state":12.5,"attributes":{},"available":true}}}` (only changed entities) |
| `event` | `{"key":"game","event_type":"started","data":{...}}` |
| `bus_event` | `{"event":"notification_action","data":{...}}` → HA bus event `pdc_homeconnect_<event>` |
| `result` | `{"id":1,"success":true,"data":{...}}` or `{"id":1,"success":false,"error":{"code":"...","message":"..."}}` |
| `bye` | Shutdown/sleep/restart notice (`reason`) |

## Messages from Home Assistant

| Type | Content |
| --- | --- |
| `command` | `{"id":1,"key":"discord_mute","action":"turn_on","params":{}}` |
| `call` | `{"id":2,"method":"notify","params":{"message":"Hi"}}` |
| `get_blob` | `{"id":3,"key":"media_cover"}` → result with `content_type`, `hash`, `data` (base64) |
| `unpair` | Removes the pairing of this client |
| `ping` | Answered with `pong` |

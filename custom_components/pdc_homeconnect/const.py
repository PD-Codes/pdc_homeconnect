"""Constants for the PDC HomeConnect integration."""

from __future__ import annotations

from typing import Final

from homeassistant.const import Platform

DOMAIN: Final = "pdc_homeconnect"
INTEGRATION_VERSION: Final = "0.1.0"

# Wire protocol version, must match the Windows client.
PROTOCOL_VERSION: Final = 1

DEFAULT_PORT: Final = 48733
WS_PATH: Final = "/ws"
ZEROCONF_TYPE: Final = "_pdc-homeconnect._tcp.local."

GITHUB_REPO: Final = "PD-Codes/pdc_homeconnect"

CONF_TOKEN: Final = "token"
CONF_CERT_SHA256: Final = "cert_sha256"
CONF_DEVICE_ID: Final = "device_id"
CONF_MACS: Final = "macs"

STORAGE_VERSION: Final = 1

# Seconds
CONNECT_TIMEOUT: Final = 10
REQUEST_TIMEOUT: Final = 45
MAX_RECONNECT_DELAY: Final = 15
HEARTBEAT: Final = 20

# Platforms whose entities are created from client descriptors.
DESCRIPTOR_PLATFORMS: Final = (
    "sensor",
    "binary_sensor",
    "button",
    "switch",
    "number",
    "select",
    "media_player",
    "event",
)

PLATFORMS: Final = [
    Platform.BINARY_SENSOR,
    Platform.BUTTON,
    Platform.EVENT,
    Platform.MEDIA_PLAYER,
    Platform.NOTIFY,
    Platform.NUMBER,
    Platform.SELECT,
    Platform.SENSOR,
    Platform.SWITCH,
    Platform.UPDATE,
]

# Prefix for Home Assistant bus events fired by the PC.
EVENT_PREFIX: Final = DOMAIN

BLOB_MEDIA_COVER: Final = "media_cover"

"""Diagnostics for PDC HomeConnect."""

from __future__ import annotations

from typing import Any

from homeassistant.components.diagnostics import async_redact_data
from homeassistant.core import HomeAssistant

from .const import CONF_TOKEN
from .models import HomeConnectConfigEntry

TO_REDACT = {CONF_TOKEN, "macs", "agent_user"}


async def async_get_config_entry_diagnostics(
    hass: HomeAssistant, entry: HomeConnectConfigEntry
) -> dict[str, Any]:
    """Return diagnostics for a config entry."""
    runtime = entry.runtime_data
    manager = runtime.manager
    return {
        "entry": async_redact_data(dict(entry.data), TO_REDACT),
        "connected": runtime.client.connected,
        "last_error": runtime.client.last_error,
        "device": async_redact_data(dict(manager.device), TO_REDACT),
        "entities": [descriptor.raw for descriptor in manager.descriptors()],
        "states": {
            descriptor.key: manager.state_for(descriptor.key)
            for descriptor in manager.descriptors()
            if descriptor.platform != "media_player"
        },
    }

"""Switches for PDC HomeConnect."""

from __future__ import annotations

from typing import Any

from homeassistant.components.switch import SwitchEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .entity import HomeConnectEntity
from .models import HomeConnectConfigEntry

PARALLEL_UPDATES = 1


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up switches."""
    entry.runtime_data.manager.async_register_platform(
        "switch", async_add_entities, HomeConnectSwitch
    )


class HomeConnectSwitch(HomeConnectEntity, SwitchEntity):
    """Switch created from a PC descriptor."""

    @property
    def is_on(self) -> bool | None:
        """Return the state."""
        value = self._value
        if value is None:
            return None
        if isinstance(value, str):
            return value.lower() in ("on", "true", "1", "running")
        return bool(value)

    async def async_turn_on(self, **kwargs: Any) -> None:
        """Turn on."""
        await self._async_command("turn_on")

    async def async_turn_off(self, **kwargs: Any) -> None:
        """Turn off."""
        await self._async_command("turn_off")

"""Selects for PDC HomeConnect."""

from __future__ import annotations

from homeassistant.components.select import SelectEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .entity import HomeConnectEntity
from .models import Descriptor, HomeConnectConfigEntry

PARALLEL_UPDATES = 1


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up selects."""
    entry.runtime_data.manager.async_register_platform(
        "select", async_add_entities, HomeConnectSelect
    )


class HomeConnectSelect(HomeConnectEntity, SelectEntity):
    """Select created from a PC descriptor."""

    def _apply_descriptor(self, descriptor: Descriptor) -> None:
        self._attr_options = list(descriptor.options or [])

    @property
    def current_option(self) -> str | None:
        """Return the selected option."""
        value = self._value
        if value is None:
            return None
        text = str(value)
        return text if text in self._attr_options else None

    async def async_select_option(self, option: str) -> None:
        """Select an option."""
        await self._async_command("select_option", option=option)

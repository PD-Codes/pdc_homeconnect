"""Event entities for PDC HomeConnect (hotkeys, notification buttons, games, ...)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.event import EventEntity
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .entity import HomeConnectEntity
from .models import Descriptor, HomeConnectConfigEntry

PARALLEL_UPDATES = 0


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up event entities."""
    entry.runtime_data.manager.async_register_platform(
        "event", async_add_entities, HomeConnectEvent
    )


class HomeConnectEvent(HomeConnectEntity, EventEntity):
    """Event entity created from a PC descriptor."""

    def _apply_descriptor(self, descriptor: Descriptor) -> None:
        self._attr_event_types = list(descriptor.event_types or [])

    @property
    def available(self) -> bool:
        """Events stay available while connected."""
        return self._manager.connected

    @property
    def extra_state_attributes(self) -> dict[str, Any] | None:
        """Event data is part of the event attributes."""
        return None

    @callback
    def async_handle_event(self, event_type: str, data: dict[str, Any]) -> None:
        """Trigger the event."""
        if self.hass is None or self.platform is None:
            return
        if event_type not in self._attr_event_types:
            self._attr_event_types = [*self._attr_event_types, event_type]
        self._trigger_event(event_type, data or None)
        self.async_write_ha_state()

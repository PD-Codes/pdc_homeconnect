"""Binary sensors for PDC HomeConnect."""

from __future__ import annotations

from homeassistant.components.binary_sensor import (
    BinarySensorDeviceClass,
    BinarySensorEntity,
)
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .entity import HomeConnectBaseEntity, HomeConnectEntity
from .manager import EntityManager
from .models import Descriptor, HomeConnectConfigEntry

PARALLEL_UPDATES = 0


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up binary sensors."""
    manager = entry.runtime_data.manager
    async_add_entities([OnlineBinarySensor(manager)])
    manager.async_register_platform(
        "binary_sensor", async_add_entities, HomeConnectBinarySensor
    )


class HomeConnectBinarySensor(HomeConnectEntity, BinarySensorEntity):
    """Binary sensor created from a PC descriptor."""

    def _apply_descriptor(self, descriptor: Descriptor) -> None:
        try:
            self._attr_device_class = (
                BinarySensorDeviceClass(descriptor.device_class)
                if descriptor.device_class
                else None
            )
        except ValueError:
            self._attr_device_class = None

    @property
    def is_on(self) -> bool | None:
        """Return the state."""
        value = self._value
        if value is None:
            return None
        if isinstance(value, str):
            return value.lower() in ("on", "true", "1", "yes")
        return bool(value)


class OnlineBinarySensor(HomeConnectBaseEntity, BinarySensorEntity):
    """Connectivity of the PC (always available)."""

    _attr_device_class = BinarySensorDeviceClass.CONNECTIVITY
    _attr_entity_category = EntityCategory.DIAGNOSTIC
    _attr_translation_key = "online"

    def __init__(self, manager: EntityManager) -> None:
        """Initialize."""
        super().__init__(manager)
        self._attr_unique_id = f"{manager.device_id}_online"

    async def async_added_to_hass(self) -> None:
        """Track the connection state."""
        await super().async_added_to_hass()
        self.async_on_remove(
            self._manager.async_add_connection_listener(self._handle_connection)
        )

    @callback
    def _handle_connection(self) -> None:
        self.async_write_ha_state()

    @property
    def is_on(self) -> bool:
        """Return True if connected."""
        return self._manager.connected

    @property
    def extra_state_attributes(self) -> dict[str, str | None]:
        """Connection details."""
        return {
            "host": self._manager.client.host,
            "last_error": self._manager.client.last_error,
        }

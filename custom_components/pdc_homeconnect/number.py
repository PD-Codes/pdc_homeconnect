"""Numbers for PDC HomeConnect."""

from __future__ import annotations

from homeassistant.components.number import NumberEntity, NumberMode
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
    """Set up numbers."""
    entry.runtime_data.manager.async_register_platform(
        "number", async_add_entities, HomeConnectNumber
    )


class HomeConnectNumber(HomeConnectEntity, NumberEntity):
    """Number created from a PC descriptor."""

    def _apply_descriptor(self, descriptor: Descriptor) -> None:
        self._attr_native_min_value = descriptor.min if descriptor.min is not None else 0
        self._attr_native_max_value = descriptor.max if descriptor.max is not None else 100
        self._attr_native_step = descriptor.step if descriptor.step is not None else 1
        self._attr_native_unit_of_measurement = descriptor.unit
        try:
            self._attr_mode = NumberMode(descriptor.mode) if descriptor.mode else NumberMode.AUTO
        except ValueError:
            self._attr_mode = NumberMode.AUTO

    @property
    def native_value(self) -> float | None:
        """Return the value."""
        value = self._value
        try:
            return None if value is None else float(value)
        except (TypeError, ValueError):
            return None

    async def async_set_native_value(self, value: float) -> None:
        """Set a new value."""
        await self._async_command("set_value", value=value)

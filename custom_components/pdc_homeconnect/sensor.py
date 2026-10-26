"""Sensors for PDC HomeConnect."""

from __future__ import annotations

from datetime import datetime
from typing import Any

from homeassistant.components.sensor import (
    SensorDeviceClass,
    SensorEntity,
    SensorStateClass,
)
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.util import dt as dt_util

from .entity import HomeConnectEntity
from .manager import EntityManager
from .models import Descriptor, HomeConnectConfigEntry

PARALLEL_UPDATES = 0


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up sensors."""
    entry.runtime_data.manager.async_register_platform(
        "sensor", async_add_entities, HomeConnectSensor
    )


class HomeConnectSensor(HomeConnectEntity, SensorEntity):
    """Sensor created from a PC descriptor."""

    def __init__(self, manager: EntityManager, descriptor: Descriptor) -> None:
        """Initialize."""
        super().__init__(manager, descriptor)

    def _apply_descriptor(self, descriptor: Descriptor) -> None:
        try:
            self._attr_device_class = (
                SensorDeviceClass(descriptor.device_class) if descriptor.device_class else None
            )
        except ValueError:
            self._attr_device_class = None
        try:
            self._attr_state_class = (
                SensorStateClass(descriptor.state_class) if descriptor.state_class else None
            )
        except ValueError:
            self._attr_state_class = None
        self._attr_native_unit_of_measurement = descriptor.unit
        self._attr_suggested_display_precision = descriptor.precision
        if descriptor.suggested_unit:
            self._attr_suggested_unit_of_measurement = descriptor.suggested_unit
        self._attr_options = (
            list(descriptor.options)
            if self._attr_device_class is SensorDeviceClass.ENUM and descriptor.options
            else None
        )

    @property
    def native_value(self) -> Any:
        """Return the state reported by the PC."""
        value = self._value
        if value is None:
            return None
        device_class = self._attr_device_class
        if device_class is SensorDeviceClass.TIMESTAMP:
            if isinstance(value, datetime):
                return value
            parsed = dt_util.parse_datetime(str(value))
            if parsed is None:
                return None
            return parsed if parsed.tzinfo else parsed.replace(tzinfo=dt_util.UTC)
        if device_class is SensorDeviceClass.ENUM:
            text = str(value)
            return text if self._attr_options and text in self._attr_options else None
        if isinstance(value, (dict, list)):
            return None
        if isinstance(value, str) and len(value) > 255:
            return value[:255]
        return value

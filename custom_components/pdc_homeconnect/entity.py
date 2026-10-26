"""Base entity for PDC HomeConnect."""

from __future__ import annotations

from typing import Any

from homeassistant.const import EntityCategory
from homeassistant.core import callback
from homeassistant.helpers.entity import Entity

from .manager import EntityManager
from .models import Descriptor
from .translation_keys import TRANSLATION_KEYS

# Attributes that are mapped to entity properties and not exposed as extra attributes.
RESERVED_ATTRIBUTES = frozenset({"entity_picture"})


class HomeConnectBaseEntity(Entity):
    """Common base for all entities of a PC."""

    _attr_has_entity_name = True
    _attr_should_poll = False

    def __init__(self, manager: EntityManager) -> None:
        """Initialize."""
        self._manager = manager
        self._attr_device_info = manager.device_info
        self._write_scheduled = False

    @callback
    def async_schedule_write(self) -> None:
        """Write the state if the entity is attached to Home Assistant."""
        if self.hass is not None and self.platform is not None and self.enabled:
            self.async_write_ha_state()


class HomeConnectEntity(HomeConnectBaseEntity):
    """Entity created from a descriptor sent by the PC."""

    def __init__(self, manager: EntityManager, descriptor: Descriptor) -> None:
        """Initialize."""
        super().__init__(manager)
        self.descriptor = descriptor
        self._attr_unique_id = f"{manager.device_id}_{descriptor.key}"
        self._apply_common(descriptor)
        self._apply_descriptor(descriptor)

    @property
    def key(self) -> str:
        """Return the entity key on the PC."""
        return self.descriptor.key

    def _apply_common(self, descriptor: Descriptor) -> None:
        platform_keys = TRANSLATION_KEYS.get(descriptor.platform, {})
        required = platform_keys.get(descriptor.translation_key or "")
        if (
            descriptor.translation_key
            and required is not None
            and required.issubset(descriptor.placeholders)
        ):
            self._attr_translation_key = descriptor.translation_key
            self._attr_translation_placeholders = dict(descriptor.placeholders)
        else:
            # Unknown key (newer client): fall back to the English name sent by the PC.
            self._attr_name = descriptor.name or descriptor.key
            if descriptor.icon:
                self._attr_icon = descriptor.icon

        try:
            self._attr_entity_category = (
                EntityCategory(descriptor.entity_category) if descriptor.entity_category else None
            )
        except ValueError:
            self._attr_entity_category = None
        self._attr_entity_registry_enabled_default = descriptor.enabled_by_default

    def _apply_descriptor(self, descriptor: Descriptor) -> None:
        """Apply platform specific descriptor fields."""

    @callback
    def async_update_descriptor(self, descriptor: Descriptor) -> None:
        """Apply a changed descriptor (options, event types)."""
        self.descriptor = descriptor
        self._apply_descriptor(descriptor)
        self.async_schedule_write()

    @callback
    def async_handle_event(self, event_type: str, data: dict[str, Any]) -> None:
        """Handle an event for this entity (event entities override this)."""

    # ------------------------------------------------------------- state

    @property
    def _payload(self) -> dict[str, Any] | None:
        return self._manager.state_for(self.descriptor.key)

    @property
    def _value(self) -> Any:
        payload = self._payload
        return None if payload is None else payload.get("state")

    @property
    def _attributes(self) -> dict[str, Any]:
        payload = self._payload
        if payload is None:
            return {}
        attributes = payload.get("attributes")
        return attributes if isinstance(attributes, dict) else {}

    @property
    def available(self) -> bool:
        """Available while connected and the PC reports the entity as available."""
        payload = self._payload
        return (
            self._manager.connected
            and payload is not None
            and bool(payload.get("available", True))
        )

    @property
    def extra_state_attributes(self) -> dict[str, Any] | None:
        """Expose extra attributes sent by the PC."""
        attributes = {
            key: value
            for key, value in self._attributes.items()
            if key not in RESERVED_ATTRIBUTES
        }
        return attributes or None

    @property
    def entity_picture(self) -> str | None:
        """Return a picture (e.g. game cover) if the PC sent one."""
        picture = self._attributes.get("entity_picture")
        return str(picture) if picture else None

    async def _async_command(self, action: str, **params: Any) -> Any:
        return await self._manager.async_command(self.descriptor.key, action, params)

"""Notify entity for PDC HomeConnect (simple notifications; use the notify action for rich ones)."""

from __future__ import annotations

from homeassistant.components.notify import NotifyEntity
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .api import CommandError, NotConnected
from .const import DOMAIN
from .entity import HomeConnectBaseEntity
from .manager import EntityManager
from .models import HomeConnectConfigEntry

PARALLEL_UPDATES = 1


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the notify entity."""
    async_add_entities([HomeConnectNotifyEntity(entry.runtime_data.manager)])


class HomeConnectNotifyEntity(HomeConnectBaseEntity, NotifyEntity):
    """Sends a Windows notification."""

    _attr_translation_key = "notify"

    def __init__(self, manager: EntityManager) -> None:
        """Initialize."""
        super().__init__(manager)
        self._attr_unique_id = f"{manager.device_id}_notify"

    async def async_added_to_hass(self) -> None:
        """Track availability."""
        await super().async_added_to_hass()
        self.async_on_remove(
            self._manager.async_add_connection_listener(self._handle_connection)
        )

    @callback
    def _handle_connection(self) -> None:
        self.async_write_ha_state()

    @property
    def available(self) -> bool:
        """Available while connected."""
        return self._manager.connected

    async def async_send_message(self, message: str, title: str | None = None) -> None:
        """Send a notification."""
        try:
            await self._manager.client.call(
                "notify", {"message": message, "title": title or ""}
            )
        except NotConnected as err:
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key="not_connected",
                translation_placeholders={"name": self._manager.entry.title},
            ) from err
        except CommandError as err:
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key="command_failed",
                translation_placeholders={"error": err.message},
            ) from err

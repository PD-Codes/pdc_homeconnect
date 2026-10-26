"""Creates and updates entities from the descriptors sent by the PC."""

from __future__ import annotations

from collections.abc import Callable
import logging
from typing import TYPE_CHECKING, Any

from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers.device_registry import CONNECTION_NETWORK_MAC, DeviceInfo
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.storage import Store

from .api import CommandError, HomeConnectClient, NotConnected
from .const import CONF_DEVICE_ID, CONF_MACS, DOMAIN, EVENT_PREFIX, PROTOCOL_VERSION
from .models import Descriptor

if TYPE_CHECKING:
    from .entity import HomeConnectEntity
    from .models import HomeConnectConfigEntry

_LOGGER = logging.getLogger(__name__)

type EntityFactory = Callable[[EntityManager, Descriptor], HomeConnectEntity | None]

SAVE_DELAY = 10


class EntityManager:
    """Keeps entities in sync with the PC and routes updates to them."""

    def __init__(
        self,
        hass: HomeAssistant,
        entry: HomeConnectConfigEntry,
        client: HomeConnectClient,
        store: Store[dict[str, Any]],
    ) -> None:
        """Initialize."""
        self.hass = hass
        self.entry = entry
        self.client = client
        self._store = store
        self.device_id: str = entry.data[CONF_DEVICE_ID]
        self.device_info = DeviceInfo(identifiers={(DOMAIN, self.device_id)})
        self._platforms: dict[str, tuple[AddConfigEntryEntitiesCallback, EntityFactory]] = {}
        self._descriptors: dict[str, Descriptor] = {}
        self._entities: dict[str, HomeConnectEntity] = {}
        self._states: dict[str, dict[str, Any]] = {}
        self._connection_listeners: list[Callable[[], None]] = []
        self._device: dict[str, Any] = {}
        # Device registry id (set during setup).
        self.ha_device_id: str | None = None

    # ------------------------------------------------------------------ setup

    async def async_load(self) -> None:
        """Load cached descriptors so entities exist while the PC is offline."""
        data = await self._store.async_load()
        if not data:
            return
        self._device = data.get("device") or {}
        self._set_descriptors(data.get("entities") or [], save=False)

    @callback
    def async_register_platform(
        self,
        platform: str,
        async_add_entities: AddConfigEntryEntitiesCallback,
        factory: EntityFactory,
    ) -> None:
        """Register a platform; creates entities for already known descriptors."""
        self._platforms[platform] = (async_add_entities, factory)
        self._create_missing(platform)

    @callback
    def async_add_connection_listener(self, listener: Callable[[], None]) -> Callable[[], None]:
        """Call listener when the connection state changes."""
        self._connection_listeners.append(listener)

        def remove() -> None:
            self._connection_listeners.remove(listener)

        return remove

    # ------------------------------------------------------------ properties

    @property
    def connected(self) -> bool:
        """Return True if the PC is connected."""
        return self.client.connected

    @property
    def device(self) -> dict[str, Any]:
        """Last known device info."""
        return self.client.device or self._device

    def state_for(self, key: str) -> dict[str, Any] | None:
        """Return the last state payload for an entity."""
        return self._states.get(key)

    def descriptors(self) -> list[Descriptor]:
        """Return all known descriptors."""
        return list(self._descriptors.values())

    # ------------------------------------------------------- client listener

    @callback
    def on_connection_changed(self, connected: bool) -> None:
        """Update availability of all entities."""
        for entity in list(self._entities.values()):
            entity.async_schedule_write()
        for listener in list(self._connection_listeners):
            listener()

    @callback
    def on_device(self, device: dict[str, Any]) -> None:
        """Update the device registry with fresh info from the PC."""
        self._device = device
        registry = dr.async_get(self.hass)
        if self.ha_device_id is None or (entry := registry.async_get(self.ha_device_id)) is None:
            return
        macs = [str(m).lower() for m in device.get("macs") or []]
        registry.async_update_device(
            entry.id,
            manufacturer=device.get("manufacturer") or None,
            model=device.get("model") or None,
            sw_version=device.get("version") or None,
            hw_version=device.get("os") or None,
            merge_connections={(CONNECTION_NETWORK_MAC, mac) for mac in macs},
        )
        if macs and macs != self.entry.data.get(CONF_MACS):
            self.hass.config_entries.async_update_entry(
                self.entry, data={**self.entry.data, CONF_MACS: macs}
            )
        self._schedule_save()
        for listener in list(self._connection_listeners):
            listener()

    @callback
    def on_entities(self, entities: list[dict[str, Any]]) -> None:
        """Replace the descriptor list."""
        self._set_descriptors(entities, save=True)

    @callback
    def on_states(self, states: dict[str, dict[str, Any]]) -> None:
        """Apply state updates."""
        for key, payload in states.items():
            if not isinstance(payload, dict):
                continue
            self._states[key] = payload
            if (entity := self._entities.get(key)) is not None:
                entity.async_schedule_write()

    @callback
    def on_event(self, key: str, event_type: str, data: dict[str, Any]) -> None:
        """Trigger an event entity."""
        entity = self._entities.get(key)
        if entity is not None:
            entity.async_handle_event(event_type, data)

    @callback
    def on_bus_event(self, name: str, data: dict[str, Any]) -> None:
        """Fire a Home Assistant bus event, e.g. pdc_homeconnect_notification_action."""
        self.hass.bus.async_fire(
            f"{EVENT_PREFIX}_{name}",
            {**data, "device_id": self.ha_device_id, "pc": self.entry.title},
        )

    @callback
    def on_auth_failed(self) -> None:
        """Start the re-authentication flow."""
        self.entry.async_start_reauth(self.hass)

    @callback
    def on_protocol_mismatch(self, protocol: int) -> None:
        """Create a repair issue."""
        from homeassistant.helpers import issue_registry as ir  # noqa: PLC0415

        issue = "client_outdated" if protocol < PROTOCOL_VERSION else "integration_outdated"
        ir.async_create_issue(
            self.hass,
            DOMAIN,
            f"{issue}_{self.entry.entry_id}",
            is_fixable=False,
            severity=ir.IssueSeverity.ERROR,
            translation_key=issue,
            translation_placeholders={
                "name": self.entry.title,
                "client_protocol": str(protocol),
                "protocol": str(PROTOCOL_VERSION),
            },
        )

    # --------------------------------------------------------------- actions

    async def async_command(
        self, key: str, action: str, params: dict[str, Any] | None = None
    ) -> Any:
        """Send a command for an entity; raises HomeAssistantError on failure."""
        from homeassistant.exceptions import HomeAssistantError  # noqa: PLC0415

        try:
            return await self.client.command(key, action, params)
        except NotConnected as err:
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key="not_connected",
                translation_placeholders={"name": self.entry.title},
            ) from err
        except CommandError as err:
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key="command_failed",
                translation_placeholders={"error": err.message},
            ) from err

    # -------------------------------------------------------------- internals

    def _set_descriptors(self, raw_list: list[dict[str, Any]], *, save: bool) -> None:
        new: dict[str, Descriptor] = {}
        for raw in raw_list:
            if isinstance(raw, dict) and (descriptor := Descriptor.from_dict(raw)):
                new[descriptor.key] = descriptor

        # Entities that disappeared or changed fundamentally are removed (registry entries stay).
        for key, entity in list(self._entities.items()):
            descriptor = new.get(key)
            if descriptor is None or descriptor.identity() != entity.descriptor.identity():
                self._entities.pop(key)
                self.hass.async_create_task(entity.async_remove(force_remove=False))
            elif descriptor.raw != entity.descriptor.raw:
                entity.async_update_descriptor(descriptor)

        self._descriptors = new
        for platform in self._platforms:
            self._create_missing(platform)
        if save:
            self._schedule_save()

    def _create_missing(self, platform: str) -> None:
        if platform not in self._platforms:
            return
        add_entities, factory = self._platforms[platform]
        created: list[HomeConnectEntity] = []
        for key, descriptor in self._descriptors.items():
            if descriptor.platform != platform or key in self._entities:
                continue
            entity = factory(self, descriptor)
            if entity is None:
                continue
            self._entities[key] = entity
            created.append(entity)
        if created:
            add_entities(created)

    def _schedule_save(self) -> None:
        self._store.async_delay_save(self._data_to_save, SAVE_DELAY)

    @callback
    def _data_to_save(self) -> dict[str, Any]:
        return {
            "device": self._device,
            "entities": [d.raw for d in self._descriptors.values()],
        }

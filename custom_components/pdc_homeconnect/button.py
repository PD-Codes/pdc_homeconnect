"""Buttons for PDC HomeConnect."""

from __future__ import annotations

import ipaddress
import logging
import socket

from homeassistant.components.button import ButtonEntity
from homeassistant.const import CONF_HOST
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from .const import CONF_MACS
from .entity import HomeConnectBaseEntity, HomeConnectEntity
from .manager import EntityManager
from .models import HomeConnectConfigEntry

_LOGGER = logging.getLogger(__name__)

PARALLEL_UPDATES = 1


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up buttons."""
    manager = entry.runtime_data.manager
    async_add_entities([WakeOnLanButton(manager)])
    manager.async_register_platform("button", async_add_entities, HomeConnectButton)


class HomeConnectButton(HomeConnectEntity, ButtonEntity):
    """Button created from a PC descriptor."""

    async def async_press(self) -> None:
        """Press the button."""
        await self._async_command("press")


def magic_packet(mac: str) -> bytes:
    """Build a Wake-on-LAN magic packet."""
    raw = bytes.fromhex(mac.replace(":", "").replace("-", ""))
    if len(raw) != 6:
        raise ValueError(f"Invalid MAC address {mac}")
    return b"\xff" * 6 + raw * 16


def broadcast_targets(host: str) -> list[str]:
    """Global broadcast plus the /24 directed broadcast of the PC's address."""
    targets = ["255.255.255.255"]
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        return targets
    if isinstance(address, ipaddress.IPv4Address):
        network = ipaddress.ip_network(f"{address}/24", strict=False)
        targets.append(str(network.broadcast_address))
    return targets


def send_wol(macs: list[str], host: str) -> None:
    """Send magic packets (blocking, run in executor)."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        for mac in macs:
            packet = magic_packet(mac)
            for target in broadcast_targets(host):
                for port in (9, 7):
                    try:
                        sock.sendto(packet, (target, port))
                    except OSError as err:
                        _LOGGER.debug("WoL to %s:%s failed: %s", target, port, err)


class WakeOnLanButton(HomeConnectBaseEntity, ButtonEntity):
    """Wakes the PC via Wake-on-LAN (works while the PC is off)."""

    _attr_translation_key = "wake_on_lan"

    def __init__(self, manager: EntityManager) -> None:
        """Initialize."""
        super().__init__(manager)
        self._attr_unique_id = f"{manager.device_id}_wake_on_lan"

    async def async_added_to_hass(self) -> None:
        """Track connection changes (MACs arrive with the device info)."""
        await super().async_added_to_hass()
        self.async_on_remove(
            self._manager.async_add_connection_listener(self._handle_update)
        )

    @callback
    def _handle_update(self) -> None:
        self.async_write_ha_state()

    @property
    def _macs(self) -> list[str]:
        return list(self._manager.entry.data.get(CONF_MACS) or [])

    @property
    def available(self) -> bool:
        """Available when at least one MAC address is known."""
        return bool(self._macs)

    async def async_press(self) -> None:
        """Send the magic packet."""
        await self.hass.async_add_executor_job(
            send_wol, self._macs, self._manager.entry.data[CONF_HOST]
        )
        self._manager.client.kick()

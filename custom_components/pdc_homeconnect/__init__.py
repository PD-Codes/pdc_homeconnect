"""PDC HomeConnect: connects Windows PCs and servers with Home Assistant."""

from __future__ import annotations

import logging

from homeassistant.const import CONF_HOST, CONF_PORT
from homeassistant.core import HomeAssistant
from homeassistant.helpers import config_validation as cv, device_registry as dr
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.device_registry import CONNECTION_NETWORK_MAC
from homeassistant.helpers.storage import Store
from homeassistant.helpers.typing import ConfigType

from .api import HomeConnectClient, HomeConnectError, async_unpair
from .const import (
    CONF_CERT_SHA256,
    CONF_DEVICE_ID,
    CONF_MACS,
    CONF_TOKEN,
    DEFAULT_PORT,
    DOMAIN,
    PLATFORMS,
    STORAGE_VERSION,
)
from .manager import EntityManager
from .models import HomeConnectConfigEntry, HomeConnectRuntime
from .services import async_setup_services

_LOGGER = logging.getLogger(__name__)

CONFIG_SCHEMA = cv.config_entry_only_config_schema(DOMAIN)


def _store(hass: HomeAssistant, entry: HomeConnectConfigEntry) -> Store:
    return Store(hass, STORAGE_VERSION, f"{DOMAIN}.{entry.entry_id}")


async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Register the actions."""
    async_setup_services(hass)
    return True


async def async_setup_entry(hass: HomeAssistant, entry: HomeConnectConfigEntry) -> bool:
    """Set up a PC."""
    client = HomeConnectClient(
        async_get_clientsession(hass),
        entry.data[CONF_HOST],
        entry.data.get(CONF_PORT, DEFAULT_PORT),
        entry.data[CONF_TOKEN],
        entry.data.get(CONF_CERT_SHA256),
        entry.title,
    )
    manager = EntityManager(hass, entry, client, _store(hass, entry))
    await manager.async_load()
    entry.runtime_data = HomeConnectRuntime(client=client, manager=manager)

    device = manager.device
    device_entry = dr.async_get(hass).async_get_or_create(
        config_entry_id=entry.entry_id,
        identifiers={(DOMAIN, entry.data[CONF_DEVICE_ID])},
        connections={(CONNECTION_NETWORK_MAC, mac) for mac in entry.data.get(CONF_MACS) or []},
        name=entry.title,
        manufacturer=device.get("manufacturer") or "PD-Codes",
        model=device.get("model") or "Windows PC",
        sw_version=device.get("version"),
        hw_version=device.get("os"),
    )
    manager.ha_device_id = device_entry.id

    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)

    client.set_listener(manager)
    entry.async_create_background_task(hass, client.run(), f"{DOMAIN}_{entry.entry_id}_connection")
    return True


async def async_unload_entry(hass: HomeAssistant, entry: HomeConnectConfigEntry) -> bool:
    """Unload a PC."""
    unloaded = await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
    await entry.runtime_data.client.stop()
    return unloaded


async def async_remove_entry(hass: HomeAssistant, entry: HomeConnectConfigEntry) -> None:
    """Remove the pairing on the PC (best effort) and delete cached data."""
    try:
        await async_unpair(
            async_get_clientsession(hass),
            entry.data[CONF_HOST],
            entry.data.get(CONF_PORT, DEFAULT_PORT),
            entry.data[CONF_TOKEN],
            entry.data.get(CONF_CERT_SHA256),
        )
    except (HomeConnectError, OSError) as err:
        _LOGGER.debug("Could not unpair %s: %s", entry.title, err)
    await _store(hass, entry).async_remove()

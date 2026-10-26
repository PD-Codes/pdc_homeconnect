"""Update entity for the Windows client."""

from __future__ import annotations

from datetime import timedelta
import logging
import time
from typing import Any

import aiohttp

from homeassistant.components.update import UpdateEntity, UpdateEntityFeature
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.event import async_track_time_interval

from .api import CommandError, NotConnected
from .const import DOMAIN, GITHUB_REPO
from .entity import HomeConnectBaseEntity
from .manager import EntityManager
from .models import HomeConnectConfigEntry

_LOGGER = logging.getLogger(__name__)

PARALLEL_UPDATES = 1
CHECK_INTERVAL = timedelta(hours=6)
CACHE_KEY = f"{DOMAIN}_latest_release"
INSTALL_TIMEOUT = 15 * 60


async def async_get_latest_release(hass: HomeAssistant, force: bool = False) -> dict[str, Any] | None:
    """Fetch the latest GitHub release (shared and cached for all PCs)."""
    cache: dict[str, Any] = hass.data.setdefault(CACHE_KEY, {})
    if not force and cache.get("fetched", 0) > time.monotonic() - CHECK_INTERVAL.total_seconds() + 60:
        return cache.get("release")
    session = async_get_clientsession(hass)
    try:
        async with session.get(
            f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest",
            headers={"Accept": "application/vnd.github+json"},
            timeout=aiohttp.ClientTimeout(total=20),
        ) as resp:
            if resp.status != 200:
                _LOGGER.debug("GitHub release check returned %s", resp.status)
                release = None
            else:
                release = await resp.json()
    except (aiohttp.ClientError, TimeoutError) as err:
        _LOGGER.debug("GitHub release check failed: %s", err)
        return cache.get("release")
    cache["fetched"] = time.monotonic()
    cache["release"] = release
    return release


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the client update entity."""
    async_add_entities([ClientUpdateEntity(entry.runtime_data.manager)])


class ClientUpdateEntity(HomeConnectBaseEntity, UpdateEntity):
    """Shows when a newer Windows client is available and installs it."""

    _attr_translation_key = "client"
    _attr_entity_category = EntityCategory.CONFIG
    _attr_supported_features = UpdateEntityFeature.INSTALL | UpdateEntityFeature.RELEASE_NOTES
    _attr_title = "PDC HomeConnect"

    def __init__(self, manager: EntityManager) -> None:
        """Initialize."""
        super().__init__(manager)
        self._attr_unique_id = f"{manager.device_id}_client_update"
        self._release: dict[str, Any] | None = None
        self._install_started: float | None = None
        self._version_at_install: str | None = None

    async def async_added_to_hass(self) -> None:
        """Start periodic release checks."""
        await super().async_added_to_hass()
        self.async_on_remove(
            self._manager.async_add_connection_listener(self._handle_connection)
        )
        self.async_on_remove(
            async_track_time_interval(self.hass, self._async_check, CHECK_INTERVAL)
        )
        self.hass.async_create_task(self._async_check())

    async def _async_check(self, *_: Any) -> None:
        self._release = await async_get_latest_release(self.hass)
        self.async_write_ha_state()

    @callback
    def _handle_connection(self) -> None:
        if (
            self._install_started is not None
            and self.installed_version
            and self.installed_version != self._version_at_install
        ):
            self._install_started = None
        self.async_write_ha_state()

    @property
    def available(self) -> bool:
        """Available while the version is known."""
        return bool(self._manager.device.get("version"))

    @property
    def installed_version(self) -> str | None:
        """Installed client version."""
        return self._manager.device.get("version")

    @property
    def latest_version(self) -> str | None:
        """Latest released version."""
        if not self._release or not self._release.get("tag_name"):
            return self.installed_version
        return str(self._release["tag_name"]).lstrip("vV")

    @property
    def release_url(self) -> str | None:
        """Release page."""
        return (self._release or {}).get("html_url")

    @property
    def in_progress(self) -> bool:
        """True while an update is being installed."""
        if self._install_started is None:
            return False
        if time.monotonic() - self._install_started > INSTALL_TIMEOUT:
            self._install_started = None
            return False
        return True

    async def async_release_notes(self) -> str | None:
        """Release notes from GitHub."""
        return (self._release or {}).get("body")

    async def async_install(self, version: str | None, backup: bool, **kwargs: Any) -> None:
        """Ask the PC to download and install the latest client."""
        try:
            await self._manager.client.call("update_client", {"version": self.latest_version}, timeout=120)
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
        self._version_at_install = self.installed_version
        self._install_started = time.monotonic()
        self.async_write_ha_state()

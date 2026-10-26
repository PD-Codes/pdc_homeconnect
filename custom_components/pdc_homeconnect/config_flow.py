"""Config flow for PDC HomeConnect."""

from __future__ import annotations

from collections.abc import Mapping
import ipaddress
import logging
from typing import Any

import voluptuous as vol

from homeassistant.config_entries import ConfigFlow, ConfigFlowResult
from homeassistant.const import CONF_HOST, CONF_NAME, CONF_PORT
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo

from .api import (
    CannotConnect,
    HomeConnectError,
    PairingFailed,
    UnsupportedProtocol,
    async_pair,
    async_probe,
    async_request_pairing,
)
from .const import (
    CONF_CERT_SHA256,
    CONF_DEVICE_ID,
    CONF_MACS,
    CONF_TOKEN,
    DEFAULT_PORT,
    DOMAIN,
)

_LOGGER = logging.getLogger(__name__)

CODE_SCHEMA = vol.Schema({vol.Required("code"): str})


def _host_schema(host: str | None = None, port: int = DEFAULT_PORT) -> vol.Schema:
    return vol.Schema(
        {
            vol.Required(CONF_HOST, default=host or vol.UNDEFINED): str,
            vol.Required(CONF_PORT, default=port): vol.All(vol.Coerce(int), vol.Range(min=1, max=65535)),
        }
    )


class HomeConnectConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle a config flow."""

    VERSION = 1

    def __init__(self) -> None:
        """Initialize."""
        self._host: str | None = None
        self._port: int = DEFAULT_PORT
        self._device: dict[str, Any] = {}
        self._pairing_requested = False

    @property
    def _client_name(self) -> str:
        return self.hass.config.location_name or "Home Assistant"

    # ---------------------------------------------------------------- user

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        """Manual setup by address."""
        errors: dict[str, str] = {}
        if user_input is not None:
            self._host = user_input[CONF_HOST].strip()
            self._port = user_input[CONF_PORT]
            try:
                self._device = await async_probe(async_get_clientsession(self.hass), self._host, self._port)
            except UnsupportedProtocol:
                return self.async_abort(reason="unsupported_protocol")
            except HomeConnectError:
                errors["base"] = "cannot_connect"
            else:
                await self.async_set_unique_id(self._device["id"])
                self._abort_if_unique_id_configured(
                    updates={CONF_HOST: self._host, CONF_PORT: self._port}
                )
                return await self.async_step_pair()

        return self.async_show_form(
            step_id="user",
            data_schema=_host_schema(self._host, self._port),
            errors=errors,
        )

    # ------------------------------------------------------------ zeroconf

    async def async_step_zeroconf(self, discovery_info: ZeroconfServiceInfo) -> ConfigFlowResult:
        """Handle a PC announced via mDNS."""
        properties = discovery_info.properties
        device_id = properties.get("id")
        if not device_id:
            return self.async_abort(reason="cannot_connect")
        port = discovery_info.port or DEFAULT_PORT

        candidates = [str(discovery_info.ip_address)]
        candidates += [
            str(address)
            for address in discovery_info.ip_addresses
            if str(address) not in candidates
            and isinstance(address, ipaddress.IPv4Address)
            and not address.is_link_local
        ]

        await self.async_set_unique_id(device_id)
        # Wake up the existing entry's client so it reconnects immediately after the PC booted.
        for entry in self._async_current_entries(include_ignore=False):
            if entry.unique_id == device_id and hasattr(entry, "runtime_data"):
                runtime = getattr(entry, "runtime_data", None)
                if runtime is not None and entry.data.get(CONF_HOST) in candidates:
                    runtime.client.kick()
                    return self.async_abort(reason="already_configured")

        session = async_get_clientsession(self.hass)
        for host in candidates:
            try:
                self._device = await async_probe(session, host, port)
            except HomeConnectError:
                continue
            if self._device.get("id") == device_id:
                self._host = host
                break
        else:
            return self.async_abort(reason="cannot_connect")

        self._port = port
        self._abort_if_unique_id_configured(updates={CONF_HOST: self._host, CONF_PORT: self._port})
        name = self._device.get("name") or properties.get("name") or self._host
        self.context["title_placeholders"] = {"name": name}
        return await self.async_step_zeroconf_confirm()

    async def async_step_zeroconf_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        """Confirm a discovered PC."""
        if user_input is not None:
            return await self.async_step_pair()
        self._set_confirm_only()
        return self.async_show_form(
            step_id="zeroconf_confirm",
            description_placeholders={"name": self._name},
        )

    # ------------------------------------------------------------- pairing

    @property
    def _name(self) -> str:
        return str(self._device.get("name") or self._host or "PC")

    async def async_step_pair(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        """Request a pairing code and ask the user to enter it."""
        assert self._host is not None
        session = async_get_clientsession(self.hass)
        errors: dict[str, str] = {}

        if user_input is not None:
            try:
                device, token = await async_pair(
                    session, self._host, self._port, user_input["code"], self._client_name
                )
            except PairingFailed as err:
                errors["base"] = err.reason if err.reason in (
                    "invalid_code",
                    "no_active_code",
                    "too_many_attempts",
                ) else "invalid_code"
                if err.reason in ("no_active_code", "too_many_attempts"):
                    self._pairing_requested = False
            except CannotConnect:
                errors["base"] = "cannot_connect"
            except HomeConnectError:
                _LOGGER.exception("Unexpected pairing error")
                errors["base"] = "unknown"
            else:
                return await self._async_finish(device, token)

        if not self._pairing_requested:
            try:
                self._device = await async_request_pairing(
                    session, self._host, self._port, self._client_name
                ) or self._device
                self._pairing_requested = True
            except HomeConnectError:
                errors["base"] = "cannot_connect"

        return self.async_show_form(
            step_id="pair",
            data_schema=CODE_SCHEMA,
            errors=errors,
            description_placeholders={"name": self._name},
        )

    async def _async_finish(self, device: dict[str, Any], token: str) -> ConfigFlowResult:
        data = {
            CONF_HOST: self._host,
            CONF_PORT: self._port,
            CONF_TOKEN: token,
            CONF_CERT_SHA256: device.get("cert_sha256"),
            CONF_DEVICE_ID: device["id"],
            CONF_NAME: device.get("name") or self._host,
            CONF_MACS: [str(m).lower() for m in device.get("macs") or []],
        }
        if self.source == "reauth":
            entry = self._get_reauth_entry()
            if entry.unique_id != device["id"]:
                return self.async_abort(reason="already_configured")
            return self.async_update_reload_and_abort(entry, data=data)

        await self.async_set_unique_id(device["id"])
        self._abort_if_unique_id_configured()
        return self.async_create_entry(title=data[CONF_NAME], data=data)

    # -------------------------------------------------------------- reauth

    async def async_step_reauth(self, entry_data: Mapping[str, Any]) -> ConfigFlowResult:
        """Re-pair after the token or certificate was rejected."""
        self._host = entry_data[CONF_HOST]
        self._port = entry_data.get(CONF_PORT, DEFAULT_PORT)
        self._device = {"name": entry_data.get(CONF_NAME), "id": entry_data.get(CONF_DEVICE_ID)}
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        """Confirm re-pairing."""
        if user_input is not None:
            return await self.async_step_pair()
        return self.async_show_form(
            step_id="reauth_confirm",
            description_placeholders={"name": self._name},
        )

    # --------------------------------------------------------- reconfigure

    async def async_step_reconfigure(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        """Change host or port."""
        entry = self._get_reconfigure_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            host = user_input[CONF_HOST].strip()
            port = user_input[CONF_PORT]
            try:
                device = await async_probe(async_get_clientsession(self.hass), host, port)
            except HomeConnectError:
                errors["base"] = "cannot_connect"
            else:
                if device.get("id") != entry.unique_id:
                    errors["base"] = "wrong_device"
                else:
                    return self.async_update_reload_and_abort(
                        entry, data_updates={CONF_HOST: host, CONF_PORT: port}
                    )
        return self.async_show_form(
            step_id="reconfigure",
            data_schema=_host_schema(entry.data[CONF_HOST], entry.data.get(CONF_PORT, DEFAULT_PORT)),
            errors=errors,
            description_placeholders={"name": entry.title},
        )

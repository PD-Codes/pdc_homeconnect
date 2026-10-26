"""Actions (services) of PDC HomeConnect."""

from __future__ import annotations

import base64
import logging
from pathlib import Path
from typing import Any

import aiohttp
import voluptuous as vol

from homeassistant.config_entries import ConfigEntryState
from homeassistant.const import ATTR_DEVICE_ID
from homeassistant.core import HomeAssistant, ServiceCall, SupportsResponse
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import config_validation as cv, device_registry as dr
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import CommandError, NotConnected
from .const import DOMAIN
from .models import HomeConnectConfigEntry

_LOGGER = logging.getLogger(__name__)

MAX_IMAGE_BYTES = 5 * 1024 * 1024

DEVICE_FIELD = {vol.Required(ATTR_DEVICE_ID): vol.All(cv.ensure_list, [cv.string])}

ACTION_ITEM = vol.Schema(
    {
        vol.Required("action"): cv.string,
        vol.Required("title"): cv.string,
    }
)

NOTIFY_SCHEMA = vol.Schema(
    {
        **DEVICE_FIELD,
        vol.Required("message"): cv.string,
        vol.Optional("title"): cv.string,
        vol.Optional("image"): cv.string,
        vol.Optional("actions"): vol.All(cv.ensure_list, [ACTION_ITEM], vol.Length(max=5)),
        vol.Optional("reply"): cv.string,
        vol.Optional("scenario", default="default"): vol.In(["default", "reminder", "alarm", "urgent"]),
        vol.Optional("sound", default="default"): vol.In(
            ["default", "silent", "im", "mail", "reminder", "sms", "alarm", "call"]
        ),
        vol.Optional("duration", default="short"): vol.In(["short", "long"]),
        vol.Optional("tag"): cv.string,
        vol.Optional("fullscreen_fallback", default="auto"): vol.In(["auto", "sound", "speak"]),
    }
)

CLEAR_SCHEMA = vol.Schema({**DEVICE_FIELD, vol.Optional("tag"): cv.string})
OPEN_URL_SCHEMA = vol.Schema({**DEVICE_FIELD, vol.Required("url"): cv.string})
SPEAK_SCHEMA = vol.Schema(
    {**DEVICE_FIELD, vol.Required("message"): cv.string, vol.Optional("language"): cv.string}
)
CLIPBOARD_SCHEMA = vol.Schema({**DEVICE_FIELD, vol.Required("text"): cv.string})
POWER_SCHEMA = vol.Schema(
    {
        **DEVICE_FIELD,
        vol.Required("action"): vol.In(["shutdown", "restart", "sleep", "hibernate", "logoff", "abort"]),
        vol.Optional("delay", default=0): vol.All(vol.Coerce(int), vol.Range(min=0, max=86400)),
        vol.Optional("force", default=False): cv.boolean,
    }
)
RUN_COMMAND_SCHEMA = vol.Schema({**DEVICE_FIELD, vol.Required("command"): cv.string})
VM_SCHEMA = vol.Schema(
    {
        **DEVICE_FIELD,
        vol.Required("vm"): cv.string,
        vol.Required("action"): vol.In(["start", "shutdown", "turn_off", "save", "pause", "resume", "reset"]),
    }
)
SERVICE_CONTROL_SCHEMA = vol.Schema(
    {
        **DEVICE_FIELD,
        vol.Required("service"): cv.string,
        vol.Required("action"): vol.In(["start", "stop", "restart"]),
    }
)


def _entries(hass: HomeAssistant, device_ids: list[str]) -> list[HomeConnectConfigEntry]:
    registry = dr.async_get(hass)
    entries: list[HomeConnectConfigEntry] = []
    for device_id in device_ids:
        device = registry.async_get(device_id)
        if device is None:
            continue
        for entry_id in device.config_entries:
            entry = hass.config_entries.async_get_entry(entry_id)
            if (
                entry is not None
                and entry.domain == DOMAIN
                and entry.state is ConfigEntryState.LOADED
                and entry not in entries
            ):
                entries.append(entry)
    if not entries:
        raise ServiceValidationError(translation_domain=DOMAIN, translation_key="no_devices")
    return entries


async def _call_all(
    hass: HomeAssistant, call: ServiceCall, method: str, params: dict[str, Any]
) -> dict[str, Any]:
    results: dict[str, Any] = {}
    for entry in _entries(hass, call.data[ATTR_DEVICE_ID]):
        try:
            results[entry.title] = await entry.runtime_data.client.call(method, params)
        except NotConnected as err:
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key="not_connected",
                translation_placeholders={"name": entry.title},
            ) from err
        except CommandError as err:
            raise HomeAssistantError(
                translation_domain=DOMAIN,
                translation_key="command_failed",
                translation_placeholders={"error": err.message},
            ) from err
    return results


async def async_load_image(hass: HomeAssistant, source: str) -> dict[str, str]:
    """Load an image from a camera, URL or /local path and return it base64 encoded."""
    try:
        if source.startswith("camera."):
            from homeassistant.components.camera import async_get_image  # noqa: PLC0415

            image = await async_get_image(hass, source)
            content, content_type = image.content, image.content_type
        elif source.startswith(("http://", "https://")):
            session = async_get_clientsession(hass)
            async with session.get(source, timeout=aiohttp.ClientTimeout(total=15)) as resp:
                resp.raise_for_status()
                content = await resp.content.read(MAX_IMAGE_BYTES + 1)
                content_type = resp.headers.get("Content-Type", "image/jpeg").split(";")[0]
        elif source.startswith("/local/"):
            path = Path(hass.config.path("www", source.removeprefix("/local/")))
            if not hass.config.is_allowed_path(str(path)):
                raise ValueError("Path is not allowed")
            content = await hass.async_add_executor_job(path.read_bytes)
            suffix = path.suffix.lower().lstrip(".")
            content_type = "image/png" if suffix == "png" else "image/gif" if suffix == "gif" else "image/jpeg"
        else:
            raise ValueError("Use camera.<name>, an http(s) URL or a /local/ path")
    except (HomeAssistantError, aiohttp.ClientError, OSError, ValueError, TimeoutError) as err:
        raise HomeAssistantError(
            translation_domain=DOMAIN,
            translation_key="image_failed",
            translation_placeholders={"error": str(err) or type(err).__name__},
        ) from err
    if len(content) > MAX_IMAGE_BYTES:
        raise HomeAssistantError(
            translation_domain=DOMAIN,
            translation_key="image_failed",
            translation_placeholders={"error": "Image is larger than 5 MB"},
        )
    return {"content_type": content_type, "data": base64.b64encode(content).decode()}


def async_setup_services(hass: HomeAssistant) -> None:
    """Register all actions."""

    async def notify(call: ServiceCall) -> None:
        params: dict[str, Any] = {
            key: call.data[key]
            for key in ("message", "title", "actions", "reply", "scenario", "sound", "duration", "tag", "fullscreen_fallback")
            if key in call.data
        }
        if image := call.data.get("image"):
            params["image"] = await async_load_image(hass, image)
        await _call_all(hass, call, "notify", params)

    async def clear_notifications(call: ServiceCall) -> None:
        await _call_all(hass, call, "clear_notifications", {"tag": call.data.get("tag")})

    async def open_url(call: ServiceCall) -> None:
        await _call_all(hass, call, "open_url", {"url": call.data["url"]})

    async def speak(call: ServiceCall) -> None:
        await _call_all(
            hass, call, "speak", {"message": call.data["message"], "language": call.data.get("language")}
        )

    async def set_clipboard(call: ServiceCall) -> None:
        await _call_all(hass, call, "set_clipboard", {"text": call.data["text"]})

    async def power(call: ServiceCall) -> None:
        await _call_all(
            hass,
            call,
            "power",
            {"action": call.data["action"], "delay": call.data["delay"], "force": call.data["force"]},
        )

    async def run_command(call: ServiceCall) -> dict[str, Any]:
        return await _call_all(hass, call, "run_command", {"command": call.data["command"]})

    async def vm_control(call: ServiceCall) -> None:
        await _call_all(hass, call, "vm_control", {"vm": call.data["vm"], "action": call.data["action"]})

    async def service_control(call: ServiceCall) -> None:
        await _call_all(
            hass, call, "service_control", {"service": call.data["service"], "action": call.data["action"]}
        )

    hass.services.async_register(DOMAIN, "notify", notify, schema=NOTIFY_SCHEMA)
    hass.services.async_register(DOMAIN, "clear_notifications", clear_notifications, schema=CLEAR_SCHEMA)
    hass.services.async_register(DOMAIN, "open_url", open_url, schema=OPEN_URL_SCHEMA)
    hass.services.async_register(DOMAIN, "speak", speak, schema=SPEAK_SCHEMA)
    hass.services.async_register(DOMAIN, "set_clipboard", set_clipboard, schema=CLIPBOARD_SCHEMA)
    hass.services.async_register(DOMAIN, "power", power, schema=POWER_SCHEMA)
    hass.services.async_register(
        DOMAIN,
        "run_command",
        run_command,
        schema=RUN_COMMAND_SCHEMA,
        supports_response=SupportsResponse.OPTIONAL,
    )
    hass.services.async_register(DOMAIN, "vm_control", vm_control, schema=VM_SCHEMA)
    hass.services.async_register(DOMAIN, "service_control", service_control, schema=SERVICE_CONTROL_SCHEMA)

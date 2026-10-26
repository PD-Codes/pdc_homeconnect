"""Setup, entities, commands and actions."""

from __future__ import annotations

from typing import Any

import pytest
from pytest_homeassistant_custom_component.common import MockConfigEntry

from homeassistant.config_entries import ConfigEntryState
from homeassistant.const import CONF_HOST, CONF_NAME, CONF_PORT, STATE_UNAVAILABLE
from homeassistant.core import HomeAssistant
from homeassistant.helpers import device_registry as dr, entity_registry as er

from custom_components.pdc_homeconnect.const import (
    CONF_CERT_SHA256,
    CONF_DEVICE_ID,
    CONF_MACS,
    CONF_TOKEN,
    DOMAIN,
)

from .fake_pc import FakePC, wait_for


def _entry(fake_pc: FakePC, token: str = "good-token") -> MockConfigEntry:
    return MockConfigEntry(
        domain=DOMAIN,
        title="Gaming PC",
        unique_id="pc1",
        data={
            CONF_HOST: "127.0.0.1",
            CONF_PORT: fake_pc.port,
            CONF_TOKEN: token,
            CONF_CERT_SHA256: fake_pc.fingerprint,
            CONF_DEVICE_ID: "pc1",
            CONF_NAME: "Gaming PC",
            CONF_MACS: ["aa:bb:cc:dd:ee:ff"],
        },
    )


async def _setup(hass: HomeAssistant, fake_pc: FakePC, token: str = "good-token") -> MockConfigEntry:
    entry = _entry(fake_pc, token)
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    return entry


async def test_entities_created_and_updated(hass: HomeAssistant, fake_pc: FakePC) -> None:
    """Descriptors create entities; states and events flow in."""
    entry = await _setup(hass, fake_pc)
    await wait_for(lambda: hass.states.get("sensor.gaming_pc_cpu_load") is not None)
    await hass.async_block_till_done()

    assert hass.states.get("binary_sensor.gaming_pc_online").state == "on"
    assert hass.states.get("sensor.gaming_pc_cpu_load").state == "12"
    disk = hass.states.get("sensor.gaming_pc_disk_c_usage")
    assert disk is not None and disk.state == "61"
    assert disk.attributes["free_gb"] == 180.2
    assert hass.states.get("sensor.gaming_pc_activity").state == "gaming"
    assert hass.states.get("sensor.gaming_pc_last_boot").state == "2026-10-26T08:00:00+00:00"
    # Unknown translation key falls back to the client's English name.
    assert hass.states.get("sensor.gaming_pc_something_new").state == "x"
    assert hass.states.get("binary_sensor.gaming_pc_gaming").state == "on"
    assert hass.states.get("switch.gaming_pc_dark_mode").state == "off"
    assert hass.states.get("number.gaming_pc_display_1_brightness").state == "70.0"
    assert hass.states.get("select.gaming_pc_audio_output").state == "Speakers"
    assert hass.states.get("media_player.gaming_pc_media").state == "playing"
    assert hass.states.get("button.gaming_pc_wake_on_lan").state != STATE_UNAVAILABLE

    # Device registry got the MAC connection.
    device = dr.async_entries_for_config_entry(dr.async_get(hass), entry.entry_id)[0]
    assert (dr.CONNECTION_NETWORK_MAC, "aa:bb:cc:dd:ee:ff") in device.connections

    # State push.
    await fake_pc.broadcast({"type": "states", "states": {"cpu_load": {"state": 77, "available": True}}})
    await wait_for(lambda: hass.states.get("sensor.gaming_pc_cpu_load").state == "77")

    # Commands.
    await hass.services.async_call("switch", "turn_on", {"entity_id": "switch.gaming_pc_dark_mode"}, blocking=True)
    await hass.services.async_call("button", "press", {"entity_id": "button.gaming_pc_lock"}, blocking=True)
    await hass.services.async_call(
        "number", "set_value", {"entity_id": "number.gaming_pc_display_1_brightness", "value": 40}, blocking=True
    )
    await hass.services.async_call(
        "select", "select_option", {"entity_id": "select.gaming_pc_audio_output", "option": "Headset"}, blocking=True
    )
    await hass.services.async_call(
        "media_player", "volume_set", {"entity_id": "media_player.gaming_pc_media", "volume_level": 0.5}, blocking=True
    )
    commands = [(c["key"], c["action"], c["params"]) for c in fake_pc.commands]
    assert ("dark_mode", "turn_on", {}) in commands
    assert ("lock", "press", {}) in commands
    assert ("monitor_1_brightness", "set_value", {"value": 40.0}) in commands
    assert ("audio_output", "select_option", {"option": "Headset"}) in commands
    assert ("media", "volume_set", {"volume": 0.5}) in commands

    # Events.
    await fake_pc.broadcast({"type": "event", "key": "hotkey", "event_type": "toggle_light", "data": {"keys": "Ctrl+Alt+L"}})
    await wait_for(lambda: hass.states.get("event.gaming_pc_hotkey").attributes.get("event_type") == "toggle_light")

    bus_events: list[Any] = []
    hass.bus.async_listen(f"{DOMAIN}_notification_action", bus_events.append)
    await fake_pc.broadcast({"type": "bus_event", "event": "notification_action", "data": {"action": "open"}})
    await wait_for(lambda: len(bus_events) == 1)
    assert bus_events[0].data["action"] == "open"
    assert bus_events[0].data["device_id"] == device.id

    # Descriptor removal removes the entity (registry entry stays).
    await fake_pc.broadcast({"type": "entities", "revision": 2, "entities": [e for e in fake_pc.entities if e["key"] != "dark_mode"]})
    await wait_for(lambda: hass.states.get("switch.gaming_pc_dark_mode") is None or hass.states.get("switch.gaming_pc_dark_mode").state == STATE_UNAVAILABLE)
    assert er.async_get(hass).async_get("switch.gaming_pc_dark_mode") is not None

    # Disconnect -> unavailable.
    await fake_pc.stop()
    await wait_for(lambda: hass.states.get("binary_sensor.gaming_pc_online").state == "off")
    assert hass.states.get("sensor.gaming_pc_cpu_load").state == STATE_UNAVAILABLE

    assert await hass.config_entries.async_unload(entry.entry_id)
    assert entry.state is ConfigEntryState.NOT_LOADED


async def test_actions(hass: HomeAssistant, fake_pc: FakePC) -> None:
    """Actions are routed to the PC."""
    await _setup(hass, fake_pc)
    await wait_for(lambda: hass.states.get("binary_sensor.gaming_pc_online").state == "on")
    entry = hass.config_entries.async_entries(DOMAIN)[0]
    device = dr.async_entries_for_config_entry(dr.async_get(hass), entry.entry_id)[0]

    await hass.services.async_call(
        DOMAIN,
        "notify",
        {
            "device_id": device.id,
            "message": "Door",
            "title": "Ring",
            "actions": [{"action": "open", "title": "Open"}],
        },
        blocking=True,
    )
    call = fake_pc.calls[-1]
    assert call["method"] == "notify"
    assert call["params"]["message"] == "Door"
    assert call["params"]["actions"] == [{"action": "open", "title": "Open"}]
    assert call["params"]["scenario"] == "default"

    result = await hass.services.async_call(
        DOMAIN, "run_command", {"device_id": device.id, "command": "x"}, blocking=True, return_response=True
    )
    assert result == {"Gaming PC": {"ok": True}}

    await hass.services.async_call(
        "notify", "send_message", {"entity_id": "notify.gaming_pc_notification", "message": "hi"}, blocking=True
    )
    assert fake_pc.calls[-1]["params"]["message"] == "hi"
    assert await hass.config_entries.async_unload(entry.entry_id)


async def test_offline_start_uses_cache(hass: HomeAssistant, fake_pc: FakePC, hass_storage: dict[str, Any]) -> None:
    """Entities exist (unavailable) when the PC is off at startup."""
    entry = _entry(fake_pc)
    hass_storage[f"{DOMAIN}.{entry.entry_id}"] = {
        "version": 1,
        "key": f"{DOMAIN}.{entry.entry_id}",
        "data": {"device": {"version": "0.1.0"}, "entities": fake_pc.entities},
    }
    await fake_pc.stop()
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()
    state = hass.states.get("sensor.gaming_pc_cpu_load")
    assert state is not None
    assert state.state == STATE_UNAVAILABLE
    assert hass.states.get("binary_sensor.gaming_pc_online").state == "off"
    assert await hass.config_entries.async_unload(entry.entry_id)


async def test_invalid_token_starts_reauth(hass: HomeAssistant, fake_pc: FakePC) -> None:
    """A rejected token triggers the reauth flow."""
    await _setup(hass, fake_pc, token="revoked")
    await wait_for(lambda: len(hass.config_entries.flow.async_progress_by_handler(DOMAIN)) == 1)
    flow = hass.config_entries.flow.async_progress_by_handler(DOMAIN)[0]
    assert flow["context"]["source"] == "reauth"
    entry = hass.config_entries.async_entries(DOMAIN)[0]
    assert await hass.config_entries.async_unload(entry.entry_id)


@pytest.mark.parametrize("method", ["fail"])
async def test_call_errors_raise(hass: HomeAssistant, fake_pc: FakePC, method: str) -> None:
    """Errors from the PC become HomeAssistantError."""
    entry = await _setup(hass, fake_pc)
    await wait_for(lambda: entry.runtime_data.client.connected)
    from custom_components.pdc_homeconnect.api import CommandError

    with pytest.raises(CommandError):
        await entry.runtime_data.client.call(method)
    assert await hass.config_entries.async_unload(entry.entry_id)

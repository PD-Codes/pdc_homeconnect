"""End-to-end test against the real C# hub (PdcHomeConnect.DevHost).

Skipped unless PDC_DEVHOST_PORT and PDC_DEVHOST_DATA are set, e.g.:

    PDC_PORT=48999 PDC_DATA=/tmp/pdc dotnet run --project client/src/PdcHomeConnect.DevHost &
    PDC_DEVHOST_PORT=48999 PDC_DEVHOST_DATA=/tmp/pdc pytest tests/test_e2e_devhost.py
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import Any

import pytest

from homeassistant.config_entries import SOURCE_USER
from homeassistant.const import CONF_HOST, CONF_PORT
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.helpers import device_registry as dr

from custom_components.pdc_homeconnect.const import DOMAIN

from .fake_pc import wait_for

PORT = os.environ.get("PDC_DEVHOST_PORT")
DATA = os.environ.get("PDC_DEVHOST_DATA")

pytestmark = pytest.mark.skipif(not PORT or not DATA, reason="DevHost not running")


async def test_devhost_end_to_end(hass: HomeAssistant, socket_enabled) -> None:
    """Pair with the real hub, receive entities, send commands and actions."""
    code_file = Path(DATA) / "pairing-code.txt"
    if code_file.exists():
        code_file.unlink()

    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": SOURCE_USER})
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_HOST: "127.0.0.1", CONF_PORT: int(PORT)}
    )
    assert result["step_id"] == "pair"
    await wait_for(code_file.exists)
    code = code_file.read_text().strip()

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {"code": code})
    assert result["type"] is FlowResultType.CREATE_ENTRY
    entry = result["result"]
    await hass.async_block_till_done()

    await wait_for(lambda: hass.states.get("sensor.devhost_cpu_load") is not None, 10)
    await wait_for(lambda: hass.states.get("sensor.devhost_cpu_load").state not in ("unavailable", "unknown"), 10)
    assert hass.states.get("binary_sensor.devhost_online").state == "on"
    assert hass.states.get("sensor.devhost_disk_c_usage").state == "61"
    assert hass.states.get("sensor.devhost_activity").state == "gaming"
    game = hass.states.get("sensor.devhost_current_game")
    assert game.state == "Satisfactory"
    assert game.attributes["entity_picture"].startswith("https://")

    await hass.services.async_call("switch", "turn_on", {"entity_id": "switch.devhost_dark_mode"}, blocking=True)
    await wait_for(lambda: hass.states.get("switch.devhost_dark_mode").state == "on")

    await hass.services.async_call(
        "select", "select_option", {"entity_id": "select.devhost_audio_output", "option": "Headset"}, blocking=True
    )
    await wait_for(lambda: hass.states.get("select.devhost_audio_output").state == "Headset")

    await hass.services.async_call(
        "media_player", "media_pause", {"entity_id": "media_player.devhost_media"}, blocking=True
    )
    await wait_for(lambda: hass.states.get("media_player.devhost_media").state == "paused")
    image = await entry.runtime_data.client.get_blob("media_cover")
    assert image is not None and image[1] == "image/png"

    events: list[Any] = []
    hass.bus.async_listen(f"{DOMAIN}_notification_action", events.append)
    device = dr.async_entries_for_config_entry(dr.async_get(hass), entry.entry_id)[0]
    await hass.services.async_call(
        DOMAIN,
        "notify",
        {"device_id": device.id, "message": "Test", "actions": [{"action": "yes", "title": "Yes"}], "tag": "t1"},
        blocking=True,
    )
    await wait_for(lambda: len(events) == 1, 5)
    assert events[0].data["action"] == "yes"
    await wait_for(lambda: hass.states.get("event.devhost_notification_action").attributes.get("event_type") == "action")

    assert await hass.config_entries.async_remove(entry.entry_id)

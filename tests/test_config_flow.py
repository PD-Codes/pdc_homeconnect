"""Config flow tests."""

from __future__ import annotations

from collections.abc import Generator
from ipaddress import ip_address
from unittest.mock import patch

import pytest

from homeassistant.config_entries import SOURCE_USER, SOURCE_ZEROCONF
from homeassistant.const import CONF_HOST, CONF_PORT
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType
from homeassistant.helpers.service_info.zeroconf import ZeroconfServiceInfo

from custom_components.pdc_homeconnect.const import (
    CONF_CERT_SHA256,
    CONF_DEVICE_ID,
    CONF_TOKEN,
    DOMAIN,
)

from .fake_pc import FakePC


@pytest.fixture(autouse=True)
def mock_setup_entry() -> Generator[None]:
    """Do not set up the created entry in config flow tests."""
    with patch("custom_components.pdc_homeconnect.async_setup_entry", return_value=True):
        yield


async def test_user_flow_with_pairing(hass: HomeAssistant, fake_pc: FakePC) -> None:
    """Manual setup: probe, request code, wrong code, right code."""
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": SOURCE_USER})
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "user"

    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_HOST: "127.0.0.1", CONF_PORT: fake_pc.port}
    )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "pair"
    assert fake_pc.pair_requests == 1

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {"code": "000000"})
    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {"base": "invalid_code"}
    assert fake_pc.pair_requests == 1

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {"code": "123456"})
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["title"] == "Gaming PC"
    data = result["data"]
    assert data[CONF_TOKEN] == "paired-token"
    assert data[CONF_CERT_SHA256] == fake_pc.fingerprint
    assert data[CONF_DEVICE_ID] == "pc1"
    assert result["result"].unique_id == "pc1"


async def test_user_flow_cannot_connect(hass: HomeAssistant, socket_enabled) -> None:
    """Unreachable address shows an error."""
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": SOURCE_USER})
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {CONF_HOST: "127.0.0.1", CONF_PORT: 1}
    )
    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {"base": "cannot_connect"}


async def test_zeroconf_flow(hass: HomeAssistant, fake_pc: FakePC) -> None:
    """Discovery via mDNS, confirm, pair."""
    info = ZeroconfServiceInfo(
        ip_address=ip_address("127.0.0.1"),
        ip_addresses=[ip_address("127.0.0.1")],
        hostname="pdc-gaming-pc.local.",
        name="Gaming PC._pdc-homeconnect._tcp.local.",
        port=fake_pc.port,
        type="_pdc-homeconnect._tcp.local.",
        properties={"id": "pc1", "name": "Gaming PC", "version": "0.1.0"},
    )
    result = await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": SOURCE_ZEROCONF}, data=info
    )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "zeroconf_confirm"

    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["step_id"] == "pair"
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {"code": "123 456"})
    # The fake only accepts the exact code; the real client strips spaces.
    assert result["type"] is FlowResultType.FORM
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {"code": "123456"})
    assert result["type"] is FlowResultType.CREATE_ENTRY

    # A second announcement for the same PC aborts.
    result = await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": SOURCE_ZEROCONF}, data=info
    )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"

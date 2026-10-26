"""Fixtures for PDC HomeConnect tests."""

from __future__ import annotations

from collections.abc import AsyncGenerator

import pytest

from .fake_pc import FakePC

pytest_plugins = ["pytest_homeassistant_custom_component"]


@pytest.fixture(autouse=True)
def auto_enable_custom_integrations(enable_custom_integrations):
    """Enable loading of the custom integration."""
    return


@pytest.fixture
async def fake_pc(socket_enabled) -> AsyncGenerator[FakePC]:
    """Run a fake PC on localhost (real sockets)."""
    pc = FakePC()
    await pc.start()
    yield pc
    await pc.stop()

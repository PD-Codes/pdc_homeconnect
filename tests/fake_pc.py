"""In-process fake of the Windows client (WebSocket + TLS) for tests."""

from __future__ import annotations

import asyncio
import datetime
import hashlib
import ssl
import tempfile
from pathlib import Path
from typing import Any

from aiohttp import WSMsgType, web
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID

DEFAULT_ENTITIES: list[dict[str, Any]] = [
    {"key": "cpu_load", "platform": "sensor", "translation_key": "cpu_load", "name": "CPU load", "unit": "%", "state_class": "measurement", "precision": 0, "enabled_by_default": True},
    {"key": "disk_usage_c", "platform": "sensor", "translation_key": "disk_usage", "placeholders": {"disk": "C:"}, "name": "Disk C: usage", "unit": "%", "enabled_by_default": True},
    {"key": "activity", "platform": "sensor", "translation_key": "activity", "name": "Activity", "device_class": "enum", "options": ["gaming", "active", "afk"], "enabled_by_default": True},
    {"key": "last_boot", "platform": "sensor", "translation_key": "last_boot", "device_class": "timestamp", "entity_category": "diagnostic", "enabled_by_default": True},
    {"key": "future_sensor", "platform": "sensor", "translation_key": "something_new", "name": "Something new", "enabled_by_default": True},
    {"key": "gaming", "platform": "binary_sensor", "translation_key": "gaming", "name": "Gaming", "enabled_by_default": True},
    {"key": "dark_mode", "platform": "switch", "translation_key": "dark_mode", "name": "Dark mode", "enabled_by_default": True},
    {"key": "lock", "platform": "button", "translation_key": "lock", "name": "Lock", "enabled_by_default": True},
    {"key": "monitor_1_brightness", "platform": "number", "translation_key": "monitor_brightness", "placeholders": {"monitor": "1"}, "min": 0, "max": 100, "step": 1, "unit": "%", "mode": "slider", "enabled_by_default": True},
    {"key": "audio_output", "platform": "select", "translation_key": "audio_output", "options": ["Speakers", "Headset"], "enabled_by_default": True},
    {"key": "hotkey", "platform": "event", "translation_key": "hotkey", "event_types": ["toggle_light"], "enabled_by_default": True},
    {"key": "media", "platform": "media_player", "translation_key": "media", "features": ["play", "pause", "volume_set", "volume_mute", "play_media"], "enabled_by_default": True},
]

DEFAULT_STATES: dict[str, dict[str, Any]] = {
    "cpu_load": {"state": 12, "available": True},
    "disk_usage_c": {"state": 61, "available": True, "attributes": {"free_gb": 180.2}},
    "activity": {"state": "gaming", "available": True},
    "last_boot": {"state": "2026-10-26T08:00:00+00:00", "available": True},
    "future_sensor": {"state": "x", "available": True},
    "gaming": {"state": True, "available": True, "attributes": {"game": "Satisfactory"}},
    "dark_mode": {"state": False, "available": True},
    "lock": {"state": None, "available": True},
    "monitor_1_brightness": {"state": 70, "available": True},
    "audio_output": {"state": "Speakers", "available": True},
    "media": {"state": "playing", "available": True, "attributes": {"media_title": "Song", "volume_level": 0.3, "media_image_hash": "abc", "app_name": "Spotify"}},
}


def _make_cert(directory: Path) -> tuple[Path, Path, str]:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "fakepc")])
    now = datetime.datetime.now(datetime.UTC)
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(days=1))
        .not_valid_after(now + datetime.timedelta(days=30))
        .sign(key, hashes.SHA256())
    )
    cert_path = directory / "cert.pem"
    key_path = directory / "key.pem"
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    key_path.write_bytes(
        key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.TraditionalOpenSSL,
            serialization.NoEncryption(),
        )
    )
    fingerprint = hashlib.sha256(cert.public_bytes(serialization.Encoding.DER)).hexdigest()
    return cert_path, key_path, fingerprint


class FakePC:
    """A minimal implementation of the client protocol."""

    def __init__(self, device_id: str = "pc1", name: str = "Gaming PC") -> None:
        self.device_id = device_id
        self.name = name
        self.code = "123456"
        self.valid_tokens: set[str] = {"good-token"}
        self.entities = [dict(e) for e in DEFAULT_ENTITIES]
        self.states = {k: dict(v) for k, v in DEFAULT_STATES.items()}
        self.commands: list[dict[str, Any]] = []
        self.calls: list[dict[str, Any]] = []
        self.pair_requests = 0
        self.port = 0
        self.fingerprint = ""
        self._runner: web.AppRunner | None = None
        self._sockets: set[web.WebSocketResponse] = set()
        self._tmp = tempfile.TemporaryDirectory()

    @property
    def device(self) -> dict[str, Any]:
        return {
            "id": self.device_id,
            "name": self.name,
            "version": "0.1.0",
            "manufacturer": "PD-Codes",
            "model": "Test",
            "os": "Windows 11",
            "cert_sha256": self.fingerprint,
            "macs": ["AA:BB:CC:DD:EE:FF"],
        }

    async def start(self) -> None:
        cert, key, self.fingerprint = _make_cert(Path(self._tmp.name))
        context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
        context.load_cert_chain(cert, key)
        app = web.Application()
        app.router.add_get("/ws", self._handle)
        self._runner = web.AppRunner(app)
        await self._runner.setup()
        site = web.TCPSite(self._runner, "127.0.0.1", self.port or 0, ssl_context=context)
        await site.start()
        self.port = site._server.sockets[0].getsockname()[1]  # noqa: SLF001

    async def stop(self) -> None:
        for ws in list(self._sockets):
            await ws.close()
        if self._runner is not None:
            await self._runner.cleanup()
            self._runner = None

    async def broadcast(self, message: dict[str, Any]) -> None:
        for ws in list(self._sockets):
            await ws.send_json(message)

    async def _handle(self, request: web.Request) -> web.WebSocketResponse:
        ws = web.WebSocketResponse()
        await ws.prepare(request)
        await ws.send_json({"type": "hello", "protocol": 1, "server": "pdc_homeconnect", "device": self.device})
        authenticated = False
        async for msg in ws:
            if msg.type is not WSMsgType.TEXT:
                continue
            data = msg.json()
            kind = data.get("type")
            if kind == "hello":
                continue
            if kind == "auth":
                if data.get("token") in self.valid_tokens:
                    authenticated = True
                    self._sockets.add(ws)
                    await ws.send_json({"type": "auth_ok"})
                    await ws.send_json({"type": "device", "device": self.device})
                    await ws.send_json({"type": "entities", "revision": 1, "entities": self.entities})
                    await ws.send_json({"type": "states", "states": self.states})
                else:
                    await ws.send_json({"type": "auth_failed", "reason": "invalid_token"})
                    await ws.close()
            elif kind == "pair_request":
                self.pair_requests += 1
                await ws.send_json({"type": "pair_pending", "expires_in": 300})
            elif kind == "pair":
                if data.get("code") == self.code:
                    self.valid_tokens.add("paired-token")
                    await ws.send_json({"type": "pair_ok", "token": "paired-token"})
                else:
                    await ws.send_json({"type": "pair_failed", "reason": "invalid_code"})
            elif not authenticated:
                await ws.send_json({"type": "error", "id": data.get("id"), "error": {"code": "unauthorized", "message": "auth"}})
            elif kind == "command":
                self.commands.append(data)
                await ws.send_json({"type": "result", "id": data["id"], "success": True})
            elif kind == "call":
                self.calls.append(data)
                if data.get("method") == "fail":
                    await ws.send_json({"type": "result", "id": data["id"], "success": False, "error": {"code": "failed", "message": "boom"}})
                else:
                    await ws.send_json({"type": "result", "id": data["id"], "success": True, "data": {"ok": True}})
            elif kind == "get_blob":
                await ws.send_json({"type": "result", "id": data["id"], "success": True, "data": {"content_type": "image/png", "hash": "abc", "data": "AQID"}})
            elif kind == "unpair":
                await ws.send_json({"type": "result", "id": data.get("id", 0), "success": True})
        self._sockets.discard(ws)
        return ws


async def wait_for(predicate, timeout: float = 5.0) -> None:
    """Wait until predicate() is truthy."""
    async with asyncio.timeout(timeout):
        while not predicate():
            await asyncio.sleep(0.02)

"""WebSocket client for the PDC HomeConnect Windows client."""

from __future__ import annotations

import asyncio
import base64
import logging
from typing import Any, Protocol

import aiohttp

from homeassistant.helpers.json import json_dumps
from homeassistant.util.json import json_loads

from .const import (
    CONNECT_TIMEOUT,
    HEARTBEAT,
    INTEGRATION_VERSION,
    MAX_RECONNECT_DELAY,
    PROTOCOL_VERSION,
    REQUEST_TIMEOUT,
    WS_PATH,
)

_LOGGER = logging.getLogger(__name__)

MAX_MESSAGE_SIZE = 64 * 1024 * 1024
CLIENT_NAME = "home-assistant"


class HomeConnectError(Exception):
    """Base error."""


class CannotConnect(HomeConnectError):
    """The PC could not be reached."""


class InvalidAuth(HomeConnectError):
    """The stored token or certificate is no longer valid."""


class UnsupportedProtocol(HomeConnectError):
    """The client speaks a different protocol version."""

    def __init__(self, protocol: int) -> None:
        """Initialize."""
        super().__init__(f"Unsupported protocol {protocol}")
        self.protocol = protocol


class PairingFailed(HomeConnectError):
    """Pairing was rejected."""

    def __init__(self, reason: str) -> None:
        """Initialize."""
        super().__init__(reason)
        self.reason = reason


class NotConnected(HomeConnectError):
    """No active connection."""


class CommandError(HomeConnectError):
    """The PC returned an error for a command."""

    def __init__(self, code: str | None, message: str | None) -> None:
        """Initialize."""
        super().__init__(message or code or "unknown error")
        self.code = code or "failed"
        self.message = message or "Unknown error"


def build_url(host: str, port: int) -> str:
    """Build the WebSocket URL (handles IPv6 literals)."""
    if ":" in host and not host.startswith("["):
        host = f"[{host}]"
    return f"wss://{host}:{port}{WS_PATH}"


def ssl_for(cert_sha256: str | None) -> aiohttp.Fingerprint | bool:
    """Pin the certificate fingerprint if known (trust on first use otherwise)."""
    if cert_sha256:
        return aiohttp.Fingerprint(bytes.fromhex(cert_sha256))
    return False


async def _receive(ws: aiohttp.ClientWebSocketResponse, timeout: float) -> dict[str, Any]:
    """Receive one JSON object."""
    async with asyncio.timeout(timeout):
        while True:
            msg = await ws.receive()
            if msg.type is aiohttp.WSMsgType.TEXT:
                data = json_loads(msg.data)
                if isinstance(data, dict):
                    return data
            elif msg.type in (
                aiohttp.WSMsgType.CLOSE,
                aiohttp.WSMsgType.CLOSED,
                aiohttp.WSMsgType.CLOSING,
                aiohttp.WSMsgType.ERROR,
            ):
                raise CannotConnect("Connection closed")


async def _receive_type(
    ws: aiohttp.ClientWebSocketResponse, types: set[str], timeout: float
) -> dict[str, Any]:
    """Receive messages until one of the given types arrives."""
    async with asyncio.timeout(timeout):
        while True:
            data = await _receive(ws, timeout)
            if data.get("type") in types:
                return data


async def _open(
    session: aiohttp.ClientSession,
    host: str,
    port: int,
    cert_sha256: str | None,
    heartbeat: float | None = None,
) -> tuple[aiohttp.ClientWebSocketResponse, dict[str, Any]]:
    """Open a connection and read the hello message."""
    try:
        async with asyncio.timeout(CONNECT_TIMEOUT):
            ws = await session.ws_connect(
                build_url(host, port),
                ssl=ssl_for(cert_sha256),
                heartbeat=heartbeat,
                max_msg_size=MAX_MESSAGE_SIZE,
            )
    except aiohttp.ServerFingerprintMismatch as err:
        raise InvalidAuth("Certificate fingerprint changed") from err
    except (aiohttp.ClientError, TimeoutError, OSError) as err:
        raise CannotConnect(str(err) or type(err).__name__) from err

    try:
        hello = await _receive_type(ws, {"hello"}, CONNECT_TIMEOUT)
        protocol = int(hello.get("protocol", 0))
        if protocol != PROTOCOL_VERSION:
            raise UnsupportedProtocol(protocol)
        await ws.send_str(
            json_dumps(
                {
                    "type": "hello",
                    "client": CLIENT_NAME,
                    "version": INTEGRATION_VERSION,
                    "protocol": PROTOCOL_VERSION,
                }
            )
        )
    except (TimeoutError, aiohttp.ClientError, ValueError) as err:
        await ws.close()
        raise CannotConnect(str(err) or type(err).__name__) from err
    except HomeConnectError:
        await ws.close()
        raise
    return ws, hello


async def async_probe(
    session: aiohttp.ClientSession, host: str, port: int
) -> dict[str, Any]:
    """Connect without authentication and return the device info."""
    ws, hello = await _open(session, host, port, None)
    await ws.close()
    return hello.get("device") or {}


async def async_request_pairing(
    session: aiohttp.ClientSession, host: str, port: int, client_name: str
) -> dict[str, Any]:
    """Ask the PC to show a pairing code. Returns the device info."""
    ws, hello = await _open(session, host, port, None)
    try:
        await ws.send_str(json_dumps({"type": "pair_request", "client_name": client_name}))
        reply = await _receive_type(ws, {"pair_pending", "error"}, CONNECT_TIMEOUT)
        if reply.get("type") == "error":
            raise PairingFailed((reply.get("error") or {}).get("code", "failed"))
    except TimeoutError as err:
        raise CannotConnect("Timeout") from err
    finally:
        await ws.close()
    return hello.get("device") or {}


async def async_pair(
    session: aiohttp.ClientSession,
    host: str,
    port: int,
    code: str,
    client_name: str,
) -> tuple[dict[str, Any], str]:
    """Send the pairing code. Returns (device info, token)."""
    ws, hello = await _open(session, host, port, None)
    try:
        await ws.send_str(
            json_dumps({"type": "pair", "code": code, "client_name": client_name})
        )
        reply = await _receive_type(ws, {"pair_ok", "pair_failed"}, CONNECT_TIMEOUT)
    except TimeoutError as err:
        raise CannotConnect("Timeout") from err
    finally:
        await ws.close()
    if reply.get("type") != "pair_ok" or not reply.get("token"):
        raise PairingFailed(str(reply.get("reason") or "invalid_code"))
    return hello.get("device") or {}, str(reply["token"])


async def async_unpair(
    session: aiohttp.ClientSession,
    host: str,
    port: int,
    token: str,
    cert_sha256: str | None,
) -> None:
    """Remove this Home Assistant from the PC's paired clients (best effort)."""
    ws, _ = await _open(session, host, port, cert_sha256)
    try:
        await ws.send_str(json_dumps({"type": "auth", "token": token}))
        reply = await _receive_type(ws, {"auth_ok", "auth_failed"}, CONNECT_TIMEOUT)
        if reply.get("type") == "auth_ok":
            await ws.send_str(json_dumps({"type": "unpair", "id": 1}))
            await _receive_type(ws, {"result"}, CONNECT_TIMEOUT)
    finally:
        await ws.close()


class ClientListener(Protocol):
    """Receives updates from the client."""

    def on_connection_changed(self, connected: bool) -> None:
        """Connection state changed."""

    def on_device(self, device: dict[str, Any]) -> None:
        """Device info received."""

    def on_entities(self, entities: list[dict[str, Any]]) -> None:
        """Full entity descriptor list received."""

    def on_states(self, states: dict[str, dict[str, Any]]) -> None:
        """State updates received."""

    def on_event(self, key: str, event_type: str, data: dict[str, Any]) -> None:
        """Event entity triggered."""

    def on_bus_event(self, name: str, data: dict[str, Any]) -> None:
        """Home Assistant bus event requested."""

    def on_auth_failed(self) -> None:
        """Credentials were rejected; re-pairing required."""

    def on_protocol_mismatch(self, protocol: int) -> None:
        """Client speaks another protocol version."""


class HomeConnectClient:
    """Persistent connection to one PC with automatic reconnect."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        host: str,
        port: int,
        token: str,
        cert_sha256: str | None,
        name: str,
    ) -> None:
        """Initialize."""
        self._session = session
        self.host = host
        self.port = port
        self._token = token
        self._cert_sha256 = cert_sha256
        self.name = name
        self._listener: ClientListener | None = None
        self._ws: aiohttp.ClientWebSocketResponse | None = None
        self._pending: dict[int, asyncio.Future[dict[str, Any]]] = {}
        self._next_id = 0
        self._stopped = False
        self._kick = asyncio.Event()
        self._connected = False
        self.device: dict[str, Any] = {}
        self.last_error: str | None = None

    @property
    def connected(self) -> bool:
        """Return True while authenticated and connected."""
        return self._connected

    def set_listener(self, listener: ClientListener) -> None:
        """Register the listener that receives all updates."""
        self._listener = listener

    def kick(self) -> None:
        """Retry connecting immediately (e.g. after a zeroconf announcement)."""
        self._kick.set()

    def update_address(self, host: str, port: int) -> None:
        """Change the address used for the next connection attempt."""
        self.host = host
        self.port = port
        self.kick()

    async def run(self) -> None:
        """Connect and keep the connection alive until stopped."""
        delay = 1.0
        while not self._stopped:
            try:
                await self._session_loop()
                delay = 1.0
            except InvalidAuth as err:
                _LOGGER.warning("%s rejected the credentials: %s", self.name, err)
                self.last_error = str(err)
                if self._listener:
                    self._listener.on_auth_failed()
                return
            except UnsupportedProtocol as err:
                self.last_error = str(err)
                if self._listener:
                    self._listener.on_protocol_mismatch(err.protocol)
                delay = MAX_RECONNECT_DELAY * 20
            except CannotConnect as err:
                self.last_error = str(err)
                _LOGGER.debug("Cannot connect to %s: %s", self.name, err)
            except asyncio.CancelledError:
                raise
            except Exception:  # noqa: BLE001 - keep the loop alive whatever happens
                _LOGGER.exception("Unexpected error in connection to %s", self.name)
            finally:
                self._set_connected(False)

            if self._stopped:
                return
            try:
                async with asyncio.timeout(delay):
                    await self._kick.wait()
            except TimeoutError:
                pass
            self._kick.clear()
            delay = min(delay * 2, float(MAX_RECONNECT_DELAY))

    async def stop(self) -> None:
        """Stop the connection loop."""
        self._stopped = True
        self._kick.set()
        if self._ws is not None and not self._ws.closed:
            await self._ws.close()

    async def _session_loop(self) -> None:
        ws, hello = await _open(
            self._session, self.host, self.port, self._cert_sha256, heartbeat=HEARTBEAT
        )
        try:
            await ws.send_str(json_dumps({"type": "auth", "token": self._token}))
            reply = await _receive_type(ws, {"auth_ok", "auth_failed"}, CONNECT_TIMEOUT)
            if reply.get("type") == "auth_failed":
                raise InvalidAuth(str(reply.get("reason") or "invalid_token"))

            self.device = hello.get("device") or {}
            self._ws = ws
            self.last_error = None
            if self._listener:
                self._listener.on_device(self.device)
            self._set_connected(True)
            _LOGGER.debug("Connected to %s", self.name)

            async for msg in ws:
                if msg.type is aiohttp.WSMsgType.TEXT:
                    try:
                        data = json_loads(msg.data)
                    except ValueError:
                        _LOGGER.debug("Invalid JSON from %s", self.name)
                        continue
                    if isinstance(data, dict):
                        self._dispatch(data)
                elif msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.ERROR):
                    break
        except TimeoutError as err:
            raise CannotConnect("Timeout") from err
        except aiohttp.ClientError as err:
            raise CannotConnect(str(err)) from err
        finally:
            self._ws = None
            for future in self._pending.values():
                if not future.done():
                    future.set_exception(NotConnected())
            self._pending.clear()
            if not ws.closed:
                await ws.close()

    def _set_connected(self, connected: bool) -> None:
        if self._connected == connected:
            return
        self._connected = connected
        if self._listener:
            self._listener.on_connection_changed(connected)

    def _dispatch(self, data: dict[str, Any]) -> None:
        msg_type = data.get("type")
        listener = self._listener
        if msg_type == "result" or (msg_type == "error" and data.get("id") is not None):
            future = self._pending.get(int(data.get("id") or 0))
            if future is not None and not future.done():
                future.set_result(data)
            return
        if listener is None:
            return
        if msg_type == "states":
            listener.on_states(data.get("states") or {})
        elif msg_type == "entities":
            listener.on_entities(list(data.get("entities") or []))
        elif msg_type == "event":
            listener.on_event(
                str(data.get("key")), str(data.get("event_type")), data.get("data") or {}
            )
        elif msg_type == "bus_event":
            listener.on_bus_event(str(data.get("event")), data.get("data") or {})
        elif msg_type == "device":
            self.device = data.get("device") or {}
            listener.on_device(self.device)
        elif msg_type == "bye":
            # The PC shuts down or goes to sleep; it closes the socket right after this.
            _LOGGER.debug("%s is going away: %s", self.name, data.get("reason"))
            self._set_connected(False)

    async def _request(self, payload: dict[str, Any], timeout: float = REQUEST_TIMEOUT) -> Any:
        ws = self._ws
        if ws is None or ws.closed or not self._connected:
            raise NotConnected
        self._next_id += 1
        request_id = self._next_id
        payload["id"] = request_id
        future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        self._pending[request_id] = future
        try:
            await ws.send_str(json_dumps(payload))
            async with asyncio.timeout(timeout):
                result = await future
        except TimeoutError as err:
            raise CommandError("timeout", "The PC did not answer in time") from err
        except (aiohttp.ClientError, ConnectionResetError) as err:
            raise NotConnected from err
        finally:
            self._pending.pop(request_id, None)

        if result.get("type") == "error" or not result.get("success"):
            error = result.get("error") or {}
            raise CommandError(error.get("code"), error.get("message"))
        return result.get("data")

    async def command(
        self, key: str, action: str, params: dict[str, Any] | None = None
    ) -> Any:
        """Send an entity command."""
        return await self._request(
            {"type": "command", "key": key, "action": action, "params": params or {}}
        )

    async def call(
        self, method: str, params: dict[str, Any] | None = None, timeout: float = REQUEST_TIMEOUT
    ) -> Any:
        """Call a method (action) on the PC."""
        return await self._request(
            {"type": "call", "method": method, "params": params or {}}, timeout
        )

    async def get_blob(self, key: str) -> tuple[bytes, str] | None:
        """Fetch binary data such as cover art."""
        try:
            data = await self._request({"type": "get_blob", "key": key}, 15)
        except CommandError:
            return None
        if not isinstance(data, dict) or "data" not in data:
            return None
        return base64.b64decode(data["data"]), str(data.get("content_type") or "image/jpeg")

"""Media player for PDC HomeConnect (Windows media session, volume, TTS playback)."""

from __future__ import annotations

import base64
from datetime import datetime
import logging
import mimetypes
from typing import Any

from homeassistant.components import media_source
from homeassistant.components.media_player import (
    ATTR_MEDIA_ANNOUNCE,
    BrowseMedia,
    MediaPlayerEntity,
    MediaPlayerEntityFeature,
    MediaPlayerState,
    MediaType,
    async_process_play_media_url,
)
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.util import dt as dt_util

from .const import BLOB_MEDIA_COVER
from .entity import HomeConnectEntity
from .models import Descriptor, HomeConnectConfigEntry

_LOGGER = logging.getLogger(__name__)

PARALLEL_UPDATES = 1

FEATURES: dict[str, MediaPlayerEntityFeature] = {
    "play": MediaPlayerEntityFeature.PLAY,
    "pause": MediaPlayerEntityFeature.PAUSE,
    "stop": MediaPlayerEntityFeature.STOP,
    "next_track": MediaPlayerEntityFeature.NEXT_TRACK,
    "previous_track": MediaPlayerEntityFeature.PREVIOUS_TRACK,
    "volume_set": MediaPlayerEntityFeature.VOLUME_SET,
    "volume_mute": MediaPlayerEntityFeature.VOLUME_MUTE,
    "volume_step": MediaPlayerEntityFeature.VOLUME_STEP,
    "seek": MediaPlayerEntityFeature.SEEK,
    "play_media": MediaPlayerEntityFeature.PLAY_MEDIA
    | MediaPlayerEntityFeature.MEDIA_ANNOUNCE
    | MediaPlayerEntityFeature.BROWSE_MEDIA,
}

STATES = {
    "playing": MediaPlayerState.PLAYING,
    "paused": MediaPlayerState.PAUSED,
    "idle": MediaPlayerState.IDLE,
    "buffering": MediaPlayerState.BUFFERING,
    "on": MediaPlayerState.ON,
    "off": MediaPlayerState.OFF,
}

# Media attributes are exposed through the standard media player properties.
MEDIA_ATTRIBUTES = frozenset(
    {
        "media_title",
        "media_artist",
        "media_album_name",
        "media_album_artist",
        "media_duration",
        "media_position",
        "media_position_updated_at",
        "volume_level",
        "is_volume_muted",
        "source",
        "media_image_hash",
        "media_content_type",
        "entity_picture",
    }
)

MAX_INLINE_AUDIO = 20 * 1024 * 1024


async def async_setup_entry(
    hass: HomeAssistant,
    entry: HomeConnectConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Set up the media player."""
    entry.runtime_data.manager.async_register_platform(
        "media_player", async_add_entities, HomeConnectMediaPlayer
    )


class HomeConnectMediaPlayer(HomeConnectEntity, MediaPlayerEntity):
    """Controls the current Windows media session and the system volume."""

    def _apply_descriptor(self, descriptor: Descriptor) -> None:
        features = MediaPlayerEntityFeature(0)
        for name in descriptor.features or []:
            features |= FEATURES.get(name, MediaPlayerEntityFeature(0))
        self._attr_supported_features = features

    @property
    def state(self) -> MediaPlayerState | None:
        """Return the playback state."""
        value = self._value
        return STATES.get(str(value).lower()) if value is not None else None

    @property
    def extra_state_attributes(self) -> dict[str, Any] | None:
        """Expose only non-standard attributes."""
        attributes = {
            key: value
            for key, value in self._attributes.items()
            if key not in MEDIA_ATTRIBUTES
        }
        return attributes or None

    @property
    def entity_picture(self) -> str | None:
        """Use the media player artwork proxy."""
        return MediaPlayerEntity.entity_picture.fget(self)  # type: ignore[attr-defined]

    @property
    def media_title(self) -> str | None:
        """Title of the current media."""
        return self._attributes.get("media_title")

    @property
    def media_artist(self) -> str | None:
        """Artist of the current media."""
        return self._attributes.get("media_artist")

    @property
    def media_album_name(self) -> str | None:
        """Album of the current media."""
        return self._attributes.get("media_album_name")

    @property
    def media_album_artist(self) -> str | None:
        """Album artist of the current media."""
        return self._attributes.get("media_album_artist")

    @property
    def media_content_type(self) -> str | None:
        """Content type of the current media."""
        return self._attributes.get("media_content_type") or (
            MediaType.MUSIC if self.media_title else None
        )

    @property
    def media_duration(self) -> int | None:
        """Duration in seconds."""
        value = self._attributes.get("media_duration")
        return int(value) if isinstance(value, (int, float)) and value > 0 else None

    @property
    def media_position(self) -> int | None:
        """Position in seconds."""
        value = self._attributes.get("media_position")
        return int(value) if isinstance(value, (int, float)) else None

    @property
    def media_position_updated_at(self) -> datetime | None:
        """When the position was last updated."""
        value = self._attributes.get("media_position_updated_at")
        return dt_util.parse_datetime(str(value)) if value else None

    @property
    def volume_level(self) -> float | None:
        """Master volume 0..1."""
        value = self._attributes.get("volume_level")
        return float(value) if isinstance(value, (int, float)) else None

    @property
    def is_volume_muted(self) -> bool | None:
        """Master mute."""
        value = self._attributes.get("is_volume_muted")
        return bool(value) if value is not None else None

    @property
    def source(self) -> str | None:
        """App that plays the media."""
        return self._attributes.get("source")

    @property
    def media_image_hash(self) -> str | None:
        """Hash of the cover art (changes when the cover changes)."""
        return self._attributes.get("media_image_hash")

    async def async_get_media_image(self) -> tuple[bytes | None, str | None]:
        """Fetch the cover art from the PC."""
        if not self.media_image_hash or not self._manager.connected:
            return None, None
        blob = await self._manager.client.get_blob(BLOB_MEDIA_COVER)
        if blob is None:
            return None, None
        return blob

    async def async_media_play(self) -> None:
        """Play."""
        await self._async_command("media_play")

    async def async_media_pause(self) -> None:
        """Pause."""
        await self._async_command("media_pause")

    async def async_media_stop(self) -> None:
        """Stop."""
        await self._async_command("media_stop")

    async def async_media_next_track(self) -> None:
        """Next track."""
        await self._async_command("media_next_track")

    async def async_media_previous_track(self) -> None:
        """Previous track."""
        await self._async_command("media_previous_track")

    async def async_media_seek(self, position: float) -> None:
        """Seek."""
        await self._async_command("media_seek", position=position)

    async def async_set_volume_level(self, volume: float) -> None:
        """Set the master volume."""
        await self._async_command("volume_set", volume=volume)

    async def async_mute_volume(self, mute: bool) -> None:
        """Mute or unmute."""
        await self._async_command("volume_mute", mute=mute)

    async def async_volume_up(self) -> None:
        """Volume up."""
        await self._async_command("volume_up")

    async def async_volume_down(self) -> None:
        """Volume down."""
        await self._async_command("volume_down")

    async def async_browse_media(
        self, media_content_type: MediaType | str | None = None, media_content_id: str | None = None
    ) -> BrowseMedia:
        """Browse audio from media sources (TTS, local media, radio)."""
        return await media_source.async_browse_media(
            self.hass,
            media_content_id,
            content_filter=lambda item: item.media_content_type.startswith("audio/"),
        )

    async def async_play_media(
        self, media_type: MediaType | str, media_id: str, **kwargs: Any
    ) -> None:
        """Play audio on the PC (e.g. TTS announcements)."""
        params: dict[str, Any] = {
            "media_type": str(media_type),
            "announce": bool(kwargs.get(ATTR_MEDIA_ANNOUNCE)),
        }
        if media_source.is_media_source_id(media_id):
            if media_id.startswith("media-source://tts/"):
                # TTS audio is sent inline so the PC does not need to reach Home Assistant.
                from homeassistant.components import tts  # noqa: PLC0415

                extension, data = await tts.async_get_media_source_audio(self.hass, media_id)
                if len(data) <= MAX_INLINE_AUDIO:
                    params["data"] = base64.b64encode(data).decode()
                    params["content_type"] = (
                        mimetypes.guess_type(f"x.{extension}")[0] or "audio/mpeg"
                    )
                    await self._async_command("play_media", **params)
                    return
            play_item = await media_source.async_resolve_media(self.hass, media_id, self.entity_id)
            media_id = play_item.url
            params["content_type"] = play_item.mime_type
        params["url"] = async_process_play_media_url(self.hass, media_id)
        await self._async_command("play_media", **params)

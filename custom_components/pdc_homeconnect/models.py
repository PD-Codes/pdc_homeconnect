"""Data models for PDC HomeConnect."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

from homeassistant.config_entries import ConfigEntry

if TYPE_CHECKING:
    from .api import HomeConnectClient
    from .manager import EntityManager


def _str(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text or None


def _float(value: Any) -> float | None:
    try:
        return None if value is None else float(value)
    except (TypeError, ValueError):
        return None


@dataclass(frozen=True, slots=True)
class Descriptor:
    """Entity description sent by the PC."""

    key: str
    platform: str
    translation_key: str | None = None
    placeholders: dict[str, str] = field(default_factory=dict)
    name: str | None = None
    icon: str | None = None
    device_class: str | None = None
    state_class: str | None = None
    unit: str | None = None
    precision: int | None = None
    suggested_unit: str | None = None
    entity_category: str | None = None
    enabled_by_default: bool = True
    options: list[str] | None = None
    event_types: list[str] | None = None
    min: float | None = None
    max: float | None = None
    step: float | None = None
    mode: str | None = None
    features: list[str] | None = None
    raw: dict[str, Any] = field(default_factory=dict, compare=False)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> Descriptor | None:
        """Create a descriptor from the wire format. Returns None if invalid."""
        key = _str(data.get("key"))
        platform = _str(data.get("platform"))
        if not key or not platform:
            return None
        precision = data.get("precision")
        return cls(
            key=key,
            platform=platform,
            translation_key=_str(data.get("translation_key")),
            placeholders={
                str(k): str(v) for k, v in (data.get("placeholders") or {}).items()
            },
            name=_str(data.get("name")),
            icon=_str(data.get("icon")),
            device_class=_str(data.get("device_class")),
            state_class=_str(data.get("state_class")),
            unit=_str(data.get("unit")),
            precision=int(precision) if isinstance(precision, (int, float)) else None,
            suggested_unit=_str(data.get("suggested_unit")),
            entity_category=_str(data.get("entity_category")),
            enabled_by_default=bool(data.get("enabled_by_default", True)),
            options=[str(o) for o in data["options"]] if isinstance(data.get("options"), list) else None,
            event_types=[str(o) for o in data["event_types"]] if isinstance(data.get("event_types"), list) else None,
            min=_float(data.get("min")),
            max=_float(data.get("max")),
            step=_float(data.get("step")),
            mode=_str(data.get("mode")),
            features=[str(o) for o in data["features"]] if isinstance(data.get("features"), list) else None,
            raw=dict(data),
        )

    def identity(self) -> tuple[Any, ...]:
        """Fields that require re-creating the entity when they change."""
        return (
            self.platform,
            self.translation_key,
            tuple(sorted(self.placeholders.items())),
            self.name,
            self.device_class,
            self.state_class,
            self.unit,
            self.entity_category,
            self.mode,
            tuple(self.features or ()),
        )


@dataclass(slots=True)
class HomeConnectRuntime:
    """Runtime data stored on the config entry."""

    client: HomeConnectClient
    manager: EntityManager


type HomeConnectConfigEntry = ConfigEntry[HomeConnectRuntime]

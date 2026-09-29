"""Zone catalogue loaded from zones.yaml."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

import yaml

ZONES_FILE = Path(__file__).with_name("zones.yaml")


@dataclass(frozen=True)
class Zone:
    id: str
    name: str
    stage: str
    kind: str
    bbox: tuple[float, float, float, float]
    event_time_utc: datetime
    ignition_time_utc: datetime | None
    usgs_event_id: str | None
    gem: dict | None = None
    why: str = ""
    labels: dict = field(default_factory=dict)
    footprint_caveat: str = ""
    local_inputs: tuple[dict, ...] = ()

    @property
    def center(self) -> tuple[float, float]:
        x0, y0, x1, y1 = self.bbox
        return (x0 + x1) / 2, (y0 + y1) / 2


def _parse_time(s: str | None) -> datetime | None:
    if not s:
        return None
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


def load_zones(path: Path = ZONES_FILE) -> dict[str, Zone]:
    raw = yaml.safe_load(Path(path).read_text())
    zones = {}
    for z in raw["zones"]:
        bbox = tuple(float(v) for v in z["bbox"])
        if not (bbox[0] < bbox[2] and bbox[1] < bbox[3]):
            raise ValueError(f"zone {z['id']}: bbox must be [lon_min, lat_min, lon_max, lat_max]")
        zones[z["id"]] = Zone(
            id=z["id"], name=z["name"], stage=z["stage"], kind=z["kind"], bbox=bbox,
            event_time_utc=_parse_time(z["event_time_utc"]),
            ignition_time_utc=_parse_time(z.get("ignition_time_utc")),
            usgs_event_id=z.get("usgs_event_id"), gem=z.get("gem"), why=(z.get("why") or "").strip(),
            labels=z.get("labels") or {}, footprint_caveat=(z.get("footprint_caveat") or "").strip(),
            local_inputs=tuple(z.get("local_inputs") or ()),
        )
    return zones


def get_zone(zone_id: str) -> Zone:
    zones = load_zones()
    if zone_id not in zones:
        raise KeyError(f"unknown zone '{zone_id}'. Known: {', '.join(zones)}")
    return zones[zone_id]

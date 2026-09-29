"""Paged download from an ArcGIS FeatureServer layer to GeoJSON.

Used for CAL FIRE DINS damage inspections (Eaton / Palisades 2025 and older
California incidents). ArcGIS hosts and data.ca.gov are BLOCKED from the cloud
session; run on a local PC. Typical size: 5-30 MB per incident.

Find the layer URL on the dataset page ("I want to use this" -> "View API
resources" -> GeoService), e.g. .../FeatureServer/0 .
"""

from __future__ import annotations

import json
from pathlib import Path

from .. import net

# DINS "DAMAGE" values -> ordinal label used by the pipeline.
DINS_DAMAGE_CLASSES = {
    "No Damage": 0,
    "Affected (1-9%)": 1,
    "Minor (10-25%)": 2,
    "Major (26-50%)": 3,
    "Destroyed (>50%)": 4,
}


def fetch_layer(layer_url: str, dest: Path, where: str = "1=1", page: int = 2000,
                bbox=None) -> Path:
    layer_url = layer_url.rstrip("/")
    features, offset = [], 0
    while True:
        params = {"where": where, "outFields": "*", "f": "geojson", "outSR": 4326,
                  "resultOffset": offset, "resultRecordCount": page}
        if bbox is not None:
            params.update({"geometry": ",".join(map(str, bbox)), "geometryType": "esriGeometryEnvelope",
                           "inSR": 4326, "spatialRel": "esriSpatialRelIntersects"})
        batch = net.get_json(f"{layer_url}/query", params=params)
        feats = batch.get("features", [])
        features.extend(feats)
        if len(feats) < page and not batch.get("exceededTransferLimit"):
            break
        offset += len(feats)
    dest = Path(dest)
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_text(json.dumps({"type": "FeatureCollection", "features": features}))
    return dest


def dins_label(damage: str | None) -> int:
    """Ordinal damage class 0-4, or -1 if missing/unknown."""
    if not damage:
        return -1
    return DINS_DAMAGE_CLASSES.get(damage.strip(), -1)

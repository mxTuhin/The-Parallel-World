"""Attach observed fire outcomes to graph nodes, and check that the footprint set
actually contains the buildings that burned.

Label codes (int8, ordinal like DINS): -1 unknown, 0 no damage, 1 affected,
2 minor, 3 major, 4 destroyed / burned.
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import shapely
from pyproj import Transformer

from .sources.arcgis import dins_label

UNKNOWN, NO_DAMAGE, DESTROYED = -1, 0, 4


def _to_local(geoms, crs_proj4: str):
    tf = Transformer.from_crs("EPSG:4326", crs_proj4, always_xy=True)
    return shapely.transform(np.asarray(geoms, dtype=object),
                             lambda xy: np.column_stack(tf.transform(xy[:, 0], xy[:, 1])))


def read_geojson(path: Path):
    fc = json.loads(Path(path).read_text())
    geoms = np.array([shapely.geometry.shape(f["geometry"]) if f.get("geometry") else None
                      for f in fc["features"]], dtype=object)
    props = [f.get("properties") or {} for f in fc["features"]]
    return geoms, props


def from_burned_polygons(node_geoms_local, burned_lonlat, crs_proj4: str,
                         min_overlap: float = 0.5) -> np.ndarray:
    """Buildings with >= min_overlap of their footprint inside a burned polygon -> 4, else 0.

    Burned-area maps cover the whole event, so every building in the zone gets a
    definite label.
    """
    burned = shapely.union_all(_to_local([g for g in burned_lonlat if g is not None], crs_proj4))
    burned = shapely.make_valid(burned)
    inter = shapely.area(shapely.intersection(node_geoms_local, burned))
    frac = inter / np.maximum(shapely.area(node_geoms_local), 1e-9)
    return np.where(frac >= min_overlap, DESTROYED, NO_DAMAGE).astype(np.int8)


def from_damage_points(node_geoms_local, points_lonlat, classes: list[int], crs_proj4: str,
                       max_dist_m: float = 15.0) -> np.ndarray:
    """Assign each inspected point (e.g. DINS) to the nearest footprint within max_dist_m.

    Buildings never inspected stay -1 (unknown): inspections only cover the
    fire perimeter, so absence of a point is not evidence of no damage.
    """
    labels = np.full(len(node_geoms_local), UNKNOWN, dtype=np.int8)
    pts = _to_local(points_lonlat, crs_proj4)
    tree = shapely.STRtree(node_geoms_local)
    pi, ni = tree.query_nearest(pts, max_distance=max_dist_m, return_distance=False)
    for p, nidx in zip(pi, ni):
        c = classes[p]
        if c > labels[nidx]:          # several points on one footprint: keep the worst damage
            labels[nidx] = c
    return labels


def dins_classes(props: list[dict]) -> list[int]:
    return [dins_label(p.get("DAMAGE") or p.get("damage")) for p in props]


def footprint_coverage_check(node_geoms_local, burned_lonlat, crs_proj4: str,
                             ring_m: float = 100.0) -> dict:
    """Compare building coverage inside the burned area with a ring around it.

    If present-day footprints were deleted after the fire, coverage inside the
    burned area collapses relative to the surrounding (unburned) blocks. A ratio
    well below ~0.5 means pre-event footprints are needed before calibrating.
    """
    burned = shapely.make_valid(shapely.union_all(_to_local([g for g in burned_lonlat if g is not None],
                                                            crs_proj4)))
    ring = shapely.difference(shapely.buffer(burned, ring_m), burned)
    bldg = shapely.union_all(node_geoms_local)
    cov_in = shapely.area(shapely.intersection(bldg, burned)) / max(shapely.area(burned), 1e-9)
    cov_out = shapely.area(shapely.intersection(bldg, ring)) / max(shapely.area(ring), 1e-9)
    ratio = cov_in / cov_out if cov_out > 0 else float("nan")
    return {"coverage_inside": round(float(cov_in), 4), "coverage_ring": round(float(cov_out), 4),
            "ratio": round(float(ratio), 3),
            "verdict": "ok" if ratio >= 0.5 else "PRE-EVENT FOOTPRINTS LIKELY MISSING"}

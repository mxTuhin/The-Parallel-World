"""Building fire graph: nodes = buildings, directed edges = possible fire transfer.

Geometry is projected to a local transverse-Mercator frame centred on the zone,
so coordinates are metres relative to the zone centre (float32-safe for any zone
up to ~100 km across).

Edge fields are purely geometric; the engine turns them into heat flux at run
time using wind and fire state:
  gap_m        closest distance between the two footprints (0 = touching)
  bearing_rad  direction src -> dst centroid, radians CCW from east
  facing_m     width over which the two footprints face each other, measured
               perpendicular to the src->dst line (min of the two projections)
Edges are stored as CSR over *incoming* edges (grouped by dst), so a GPU kernel
can gather heat into each building without atomics.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np
import shapely
from pyproj import CRS, Transformer

HEIGHT_FROM_OVERTURE, HEIGHT_FROM_FLOORS, HEIGHT_DEFAULT, HEIGHT_FROM_GBA = 0, 1, 2, 3
STOREY_HEIGHT_M = 3.0
DEFAULT_HEIGHT_M = 6.0     # two storeys; typical low-rise wooden housing


def local_proj4(lon0: float, lat0: float) -> str:
    return (f"+proj=tmerc +lat_0={lat0} +lon_0={lon0} +k=1 +x_0=0 +y_0=0 "
            "+ellps=WGS84 +units=m +no_defs")


@dataclass
class BuildingGraph:
    crs_proj4: str
    origin_lonlat: tuple[float, float]
    node: dict[str, np.ndarray]           # per-building arrays, length n
    in_offsets: np.ndarray                # uint32, n+1
    edge: dict[str, np.ndarray]           # per-edge arrays aligned with in_offsets (src, gap_m, ...)
    grid: dict                            # firebrand spatial hash
    meta: dict = field(default_factory=dict)

    @property
    def n_nodes(self) -> int:
        return len(self.in_offsets) - 1

    @property
    def n_edges(self) -> int:
        return int(self.in_offsets[-1])


def infer_heights(height: np.ndarray, floors: np.ndarray, gba: np.ndarray | None = None):
    """Height per building and a code saying where it came from."""
    n = len(height)
    h = np.full(n, DEFAULT_HEIGHT_M, dtype=np.float32)
    src = np.full(n, HEIGHT_DEFAULT, dtype=np.uint8)
    if gba is not None:
        ok = np.isfinite(gba) & (gba > 0)
        h[ok], src[ok] = gba[ok], HEIGHT_FROM_GBA
    ok = np.isfinite(floors) & (floors > 0)
    h[ok], src[ok] = floors[ok] * STOREY_HEIGHT_M, HEIGHT_FROM_FLOORS
    ok = np.isfinite(height) & (height > 0)
    h[ok], src[ok] = height[ok], HEIGHT_FROM_OVERTURE
    return h, src


def _projected_width(coords: np.ndarray, counts: np.ndarray, starts: np.ndarray,
                     idx: np.ndarray, normal: np.ndarray) -> np.ndarray:
    """Width of polygons `idx` projected on unit vectors `normal` (one per row)."""
    out = np.zeros(len(idx), dtype=np.float64)
    maxv = int(counts[idx].max()) if len(idx) else 0
    if maxv == 0:
        return out
    batch = max(1, 4_000_000 // max(maxv, 1))
    for b0 in range(0, len(idx), batch):
        sel = idx[b0:b0 + batch]
        k = np.arange(maxv)[None, :]
        valid = k < counts[sel][:, None]
        rows = np.where(valid, starts[sel][:, None] + k, 0)
        pts = coords[rows]                                        # (B, maxv, 2)
        proj = pts[..., 0] * normal[b0:b0 + batch, 0, None] + pts[..., 1] * normal[b0:b0 + batch, 1, None]
        hi = np.where(valid, proj, -np.inf).max(axis=1)
        lo = np.where(valid, proj, np.inf).min(axis=1)
        out[b0:b0 + batch] = np.where(counts[sel] > 0, hi - lo, 0.0)
    return out


def build(geoms_lonlat: np.ndarray, *, height=None, floors=None, gba_height=None,
          origin_lonlat: tuple[float, float], radius_m: float = 40.0, grid_cell_m: float = 25.0,
          min_area_m2: float = 4.0) -> tuple[BuildingGraph, np.ndarray]:
    """Build the graph from shapely geometries in lon/lat.

    Returns the graph and the index array mapping graph nodes back to the input
    rows (tiny slivers below `min_area_m2` are dropped).
    """
    lon0, lat0 = origin_lonlat
    proj4 = local_proj4(lon0, lat0)
    tf = Transformer.from_crs("EPSG:4326", CRS.from_proj4(proj4), always_xy=True)
    geoms = shapely.transform(np.asarray(geoms_lonlat, dtype=object),
                              lambda xy: np.column_stack(tf.transform(xy[:, 0], xy[:, 1])))
    geoms = shapely.make_valid(geoms)
    area = shapely.area(geoms)
    keep = np.flatnonzero(area >= min_area_m2)
    geoms = geoms[keep]
    area = area[keep]
    n = len(geoms)

    cent = shapely.centroid(geoms)
    cx, cy = shapely.get_x(cent), shapely.get_y(cent)
    def _col(values):
        if values is None:
            return np.full(n, np.nan)
        return np.asarray(values, dtype=np.float64)[keep]

    h, hsrc = infer_heights(_col(height), _col(floors), _col(gba_height) if gba_height is not None else None)

    # Candidate pairs within radius, then exact footprint-to-footprint gaps.
    tree = shapely.STRtree(geoms)
    a, b = tree.query(geoms, predicate="dwithin", distance=radius_m)
    m = a != b
    dst, src = a[m], b[m]                      # edge src -> dst, both directions appear
    gap = shapely.distance(geoms[src], geoms[dst])
    dx, dy = cx[dst] - cx[src], cy[dst] - cy[src]
    bearing = np.arctan2(dy, dx)
    dist_c = np.hypot(dx, dy)
    normal = np.column_stack([-dy, dx]) / np.maximum(dist_c, 1e-9)[:, None]

    ring = shapely.get_exterior_ring(shapely.convex_hull(geoms))   # None for degenerate hulls
    coords, owner = shapely.get_coordinates(ring, return_index=True)
    counts = np.bincount(owner, minlength=n)
    starts = np.concatenate([[0], np.cumsum(counts)[:-1]])
    w_src = _projected_width(coords, counts, starts, src, normal)
    w_dst = _projected_width(coords, counts, starts, dst, normal)
    facing = np.minimum(w_src, w_dst)

    order = np.lexsort((src, dst))
    dst, src = dst[order], src[order]
    in_offsets = np.zeros(n + 1, dtype=np.uint32)
    np.cumsum(np.bincount(dst, minlength=n), out=in_offsets[1:])

    # Firebrand landing grid (spatial hash): which buildings sit in each cell.
    x0, y0 = float(cx.min()) - grid_cell_m, float(cy.min()) - grid_cell_m
    nx = int(np.ceil((cx.max() - x0) / grid_cell_m)) + 2
    ny = int(np.ceil((cy.max() - y0) / grid_cell_m)) + 2
    cell = ((cy - y0) // grid_cell_m).astype(np.int64) * nx + ((cx - x0) // grid_cell_m).astype(np.int64)
    corder = np.argsort(cell, kind="stable")
    cell_offsets = np.zeros(nx * ny + 1, dtype=np.uint32)
    np.cumsum(np.bincount(cell, minlength=nx * ny), out=cell_offsets[1:])

    graph = BuildingGraph(
        crs_proj4=proj4,
        origin_lonlat=(lon0, lat0),
        node={
            "x_m": cx.astype(np.float32),
            "y_m": cy.astype(np.float32),
            "area_m2": area.astype(np.float32),
            "perimeter_m": shapely.length(geoms).astype(np.float32),
            "height_m": h.astype(np.float32),
            "height_src": hsrc,
        },
        in_offsets=in_offsets,
        edge={
            "src": src.astype(np.uint32),
            "gap_m": gap[order].astype(np.float32),
            "bearing_rad": bearing[order].astype(np.float32),
            "facing_m": facing[order].astype(np.float32),
        },
        grid={"cell_m": float(grid_cell_m), "x0_m": x0, "y0_m": y0, "nx": nx, "ny": ny,
              "cell_offsets": cell_offsets, "cell_items": corder.astype(np.uint32)},
        meta={"radius_m": radius_m, "min_area_m2": min_area_m2},
    )
    graph.meta["geoms_local"] = geoms          # kept in memory for label joins; not exported
    return graph, keep


def degree_stats(g: BuildingGraph) -> dict:
    deg = np.diff(g.in_offsets.astype(np.int64))
    if len(deg) == 0:
        return {}
    return {"mean": float(deg.mean()), "median": float(np.median(deg)), "max": int(deg.max()),
            "isolated": int((deg == 0).sum())}

"""Copernicus GLO-30 DEM (AWS S3, anonymous). 1x1 degree Cloud-Optimized GeoTIFF
tiles, ~5-40 MB each. Reachable from the cloud session."""

from __future__ import annotations

import math
from pathlib import Path

import numpy as np

from .. import config, net


def tile_name(lat: float, lon: float) -> str:
    """Tile covering (lat, lon), e.g. Copernicus_DSM_COG_10_N37_00_E136_00_DEM."""
    la, lo = math.floor(lat), math.floor(lon)
    ns = f"N{la:02d}" if la >= 0 else f"S{-la:02d}"
    ew = f"E{lo:03d}" if lo >= 0 else f"W{-lo:03d}"
    return f"Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM"


def tiles_for_bbox(bbox) -> list[str]:
    x0, y0, x1, y1 = bbox
    names = []
    for la in range(math.floor(y0), math.floor(y1) + 1):
        for lo in range(math.floor(x0), math.floor(x1) + 1):
            names.append(tile_name(la + 0.5, lo + 0.5))
    return names


def fetch_tiles(bbox, dest_dir: Path, allow_large: bool = False) -> list[Path]:
    out = []
    for name in tiles_for_bbox(bbox):
        url = f"{config.COPDEM_BASE}/{name}/{name}.tif"
        out.append(net.download(url, Path(dest_dir) / f"{name}.tif", allow_large=allow_large))
    return out


def sample(tile_paths: list[Path], lons: np.ndarray, lats: np.ndarray) -> np.ndarray:
    """Elevation (m) at points; NaN where no tile covers a point."""
    import rasterio

    z = np.full(len(lons), np.nan, dtype=np.float64)
    for p in tile_paths:
        with rasterio.open(p) as src:
            b = src.bounds
            inside = (lons >= b.left) & (lons < b.right) & (lats > b.bottom) & (lats <= b.top)
            if not inside.any():
                continue
            vals = np.array([v[0] for v in src.sample(zip(lons[inside], lats[inside]))], dtype=np.float64)
            if src.nodata is not None:
                vals[vals == src.nodata] = np.nan
            z[inside] = vals
    return z

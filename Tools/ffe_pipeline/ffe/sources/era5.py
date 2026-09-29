"""ERA5 hourly surface weather from the NSF NCAR mirror on AWS (ds633.0).

Each monthly variable file is ~1.1-1.6 GB NetCDF4, but it is chunked, so HTTP
range reads pull only the chunk covering the zone (a few MB). Reachable from the
cloud session. Variables: 10 m wind (u, v), 2 m temperature and dewpoint (for
relative humidity).
"""

from __future__ import annotations

import calendar
import csv
import math
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np

from .. import config

# ECMWF parameter codes and the variable names used inside the NCAR files.
VARIABLES = {
    "u10": ("128_165_10u", "VAR_10U"),
    "v10": ("128_166_10v", "VAR_10V"),
    "t2m": ("128_167_2t", "VAR_2T"),
    "d2m": ("128_168_2d", "VAR_2D"),
}


def monthly_url(var: str, year: int, month: int) -> str:
    code, _ = VARIABLES[var]
    last = calendar.monthrange(year, month)[1]
    ym = f"{year:04d}{month:02d}"
    return (f"{config.ERA5_BASE}/e5.oper.an.sfc/{ym}/"
            f"e5.oper.an.sfc.{code}.ll025sc.{ym}0100_{ym}{last:02d}23.nc")


def grid_index(lat: float, lon: float) -> tuple[int, int]:
    """Nearest index on the ERA5 0.25 deg grid (lat 90 -> -90, lon 0 -> 359.75)."""
    iy = int(round((90.0 - lat) / 0.25))
    ix = int(round((lon % 360.0) / 0.25)) % 1440
    return iy, ix


def _months(t0: datetime, t1: datetime):
    y, m = t0.year, t0.month
    while (y, m) <= (t1.year, t1.month):
        yield y, m
        y, m = (y + 1, 1) if m == 12 else (y, m + 1)


def _read_point(var: str, year: int, month: int, iy: int, ix: int, h0: int, h1: int) -> np.ndarray:
    import fsspec
    import h5py

    fs = fsspec.filesystem("https", client_kwargs={"trust_env": True})
    _, name = VARIABLES[var]
    with fs.open(monthly_url(var, year, month), "rb", block_size=1 << 18, cache_type="blockcache") as f:
        with h5py.File(f, "r") as h:
            return np.asarray(h[name][h0:h1 + 1, iy, ix], dtype=np.float64)


def relative_humidity(t_k: np.ndarray, td_k: np.ndarray) -> np.ndarray:
    """Magnus formula, percent."""
    t, td = t_k - 273.15, td_k - 273.15
    a, b = 17.625, 243.04
    return 100.0 * np.exp(a * td / (b + td) - a * t / (b + t))


def fetch_point_series(lat: float, lon: float, t_start: datetime, t_end: datetime, dest: Path,
                       overwrite: bool = False) -> Path:
    """Hourly weather at the ERA5 cell nearest (lat, lon) between t_start and t_end (UTC)."""
    dest = Path(dest)
    if dest.exists() and not overwrite:
        return dest
    t_start = t_start.astimezone(timezone.utc).replace(minute=0, second=0, microsecond=0)
    t_end = t_end.astimezone(timezone.utc)
    iy, ix = grid_index(lat, lon)
    rows = []
    for y, m in _months(t_start, t_end):
        month_start = datetime(y, m, 1, tzinfo=timezone.utc)
        month_end = datetime(y, m, calendar.monthrange(y, m)[1], 23, tzinfo=timezone.utc)
        a, b = max(t_start, month_start), min(t_end, month_end)
        h0 = int((a - month_start).total_seconds() // 3600)
        h1 = int((b - month_start).total_seconds() // 3600)
        data = {v: _read_point(v, y, m, iy, ix, h0, h1) for v in VARIABLES}
        rh = relative_humidity(data["t2m"], data["d2m"])
        for k in range(h1 - h0 + 1):
            u, v = data["u10"][k], data["v10"][k]
            rows.append({
                "time_utc": (month_start + timedelta(hours=h0 + k)).isoformat().replace("+00:00", "Z"),
                "u10": round(u, 3), "v10": round(v, 3),
                "speed": round(math.hypot(u, v), 3),
                # meteorological convention: direction the wind blows FROM, degrees clockwise from north
                "dir_from_deg": round((math.degrees(math.atan2(-u, -v)) + 360.0) % 360.0, 1),
                "t2m_c": round(data["t2m"][k] - 273.15, 2),
                "rh_pct": round(float(rh[k]), 1),
            })
    dest.parent.mkdir(parents=True, exist_ok=True)
    with open(dest, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0]))
        w.writeheader()
        w.writerows(rows)
    return dest


def read_series(path: Path) -> list[dict]:
    with open(path, newline="") as f:
        return [{k: (v if k == "time_utc" else float(v)) for k, v in r.items()} for r in csv.DictReader(f)]

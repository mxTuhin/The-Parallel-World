"""USGS ShakeMap ground motion.

Host earthquake.usgs.gov is BLOCKED from the cloud session; fetch on a local PC
with `ffe fetch <zone> --only shakemap`, or download grid.xml by hand into
FFEData/zones/<zone>/local_inputs/shakemap_grid.xml. Typical grid.xml: 1-20 MB.

grid.xml layout (ShakeMap v3/v4):
  <shakemap_grid ...>
    <grid_specification lon_min= lat_min= lon_max= lat_max=
                        nominal_lon_spacing= nominal_lat_spacing= nlon= nlat= />
    <grid_field index="1" name="LON" units="dd" /> ... PGA (%g), PGV (cm/s), MMI ...
    <grid_data> whitespace-separated rows, lon fastest, starting at the NORTH-west corner </grid_data>
"""

from __future__ import annotations

import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .. import config, net


@dataclass
class ShakeGrid:
    lon_min: float
    lat_min: float
    lon_max: float
    lat_max: float
    dlon: float
    dlat: float
    nlon: int
    nlat: int
    fields: dict[str, np.ndarray]   # name -> (nlat, nlon), row 0 = north
    units: dict[str, str]
    event_id: str = ""

    def sample(self, name: str, lons: np.ndarray, lats: np.ndarray) -> np.ndarray:
        """Bilinear interpolation; NaN outside the grid."""
        a = self.fields[name]
        fx = (np.asarray(lons) - self.lon_min) / self.dlon
        fy = (self.lat_max - np.asarray(lats)) / self.dlat
        out = np.full(fx.shape, np.nan)
        ok = (fx >= 0) & (fx <= self.nlon - 1) & (fy >= 0) & (fy <= self.nlat - 1)
        x, y = fx[ok], fy[ok]
        x0 = np.minimum(np.floor(x).astype(int), self.nlon - 2)
        y0 = np.minimum(np.floor(y).astype(int), self.nlat - 2)
        tx, ty = x - x0, y - y0
        v = (a[y0, x0] * (1 - tx) * (1 - ty) + a[y0, x0 + 1] * tx * (1 - ty)
             + a[y0 + 1, x0] * (1 - tx) * ty + a[y0 + 1, x0 + 1] * tx * ty)
        out[ok] = v
        return out


def _strip_ns(tag: str) -> str:
    return tag.split("}", 1)[-1]


def parse_grid_xml(path: Path) -> ShakeGrid:
    root = ET.parse(path).getroot()
    spec, fields_meta, data_text = None, {}, None
    for el in root:
        tag = _strip_ns(el.tag)
        if tag == "grid_specification":
            spec = el.attrib
        elif tag == "grid_field":
            fields_meta[int(el.attrib["index"])] = (el.attrib["name"], el.attrib.get("units", ""))
        elif tag == "grid_data":
            data_text = el.text
    if spec is None or data_text is None or not fields_meta:
        raise ValueError(f"{path}: not a ShakeMap grid.xml")
    nlon, nlat = int(spec["nlon"]), int(spec["nlat"])
    ncol = max(fields_meta)
    flat = np.array(data_text.split(), dtype=np.float64)
    if flat.size != nlon * nlat * ncol:
        raise ValueError(f"{path}: expected {nlon * nlat * ncol} values, got {flat.size}")
    table = flat.reshape(nlat * nlon, ncol)
    fields = {name: table[:, idx - 1].reshape(nlat, nlon) for idx, (name, _) in fields_meta.items()}
    return ShakeGrid(
        lon_min=float(spec["lon_min"]), lat_min=float(spec["lat_min"]),
        lon_max=float(spec["lon_max"]), lat_max=float(spec["lat_max"]),
        dlon=float(spec["nominal_lon_spacing"]), dlat=float(spec["nominal_lat_spacing"]),
        nlon=nlon, nlat=nlat, fields=fields,
        units={name: u for name, u in fields_meta.values()},
        event_id=root.attrib.get("event_id", ""),
    )


def grid_xml_url(event_id: str) -> str:
    """Resolve the grid.xml URL for a USGS event (needs access to earthquake.usgs.gov)."""
    detail = net.get_json(config.USGS_EVENT_API, params={"eventid": event_id, "format": "geojson"})
    shakemaps = detail["properties"]["products"].get("shakemap") or []
    if not shakemaps:
        raise LookupError(f"USGS event {event_id} has no ShakeMap product")
    contents = shakemaps[0]["contents"]
    for key in ("download/grid.xml", "download/grid.xml.zip"):
        if key in contents:
            return contents[key]["url"]
    raise LookupError(f"USGS event {event_id}: ShakeMap has no grid.xml")


def fetch_grid(event_id: str, dest: Path, allow_large: bool = False) -> Path:
    url = grid_xml_url(event_id)
    if url.endswith(".zip"):
        import zipfile

        zpath = net.download(url, Path(dest).with_suffix(".zip"), allow_large=allow_large)
        with zipfile.ZipFile(zpath) as z:
            name = next(n for n in z.namelist() if n.endswith("grid.xml"))
            Path(dest).write_bytes(z.read(name))
        return Path(dest)
    return net.download(url, dest, allow_large=allow_large)


def lookup_events(time_utc: str, minmag: float = 6.0, window_min: int = 10) -> list[dict]:
    """Find USGS event ids near a time (for zones whose id is not yet known)."""
    from datetime import datetime, timedelta

    t = datetime.fromisoformat(time_utc.replace("Z", "+00:00"))
    res = net.get_json(config.USGS_EVENT_API, params={
        "format": "geojson", "minmagnitude": minmag,
        "starttime": (t - timedelta(minutes=window_min)).isoformat(),
        "endtime": (t + timedelta(minutes=window_min)).isoformat(),
    })
    return [{"id": f["id"], "mag": f["properties"]["mag"], "place": f["properties"]["place"],
             "time_ms": f["properties"]["time"]} for f in res["features"]]

"""Overture Maps (AWS S3, anonymous) — building footprints, roads, places.

Buildings conflate OpenStreetMap, Microsoft ML Buildings and Google Open
Buildings. The global building layer is ~277 GB (512 GeoParquet files, release
2026-09-23.1); a bbox query reads only the row groups it needs, so a small zone
transfers ~1-20 MB. Reachable from the cloud session.
"""

from __future__ import annotations

from pathlib import Path

import pyarrow as pa
import pyarrow.compute as pc
import pyarrow.dataset as ds
import pyarrow.fs as pafs
import pyarrow.parquet as pq

from .. import config, net

BUILDING_COLUMNS = ["id", "geometry", "height", "num_floors", "class", "subtype",
                    "facade_material", "roof_material", "sources", "bbox"]
SEGMENT_COLUMNS = ["id", "geometry", "subtype", "class", "bbox"]
PLACE_COLUMNS = ["id", "geometry", "names", "basic_category", "taxonomy", "bbox"]


def _filesystem() -> pafs.S3FileSystem:
    kwargs = {"anonymous": True, "region": config.OVERTURE_REGION}
    proxy = net.https_proxy_options()
    if proxy:
        kwargs["proxy_options"] = proxy
    return pafs.S3FileSystem(**kwargs)


def _dataset(theme: str, type_: str) -> ds.Dataset:
    path = f"{config.OVERTURE_BUCKET}/release/{config.OVERTURE_RELEASE}/theme={theme}/type={type_}"
    return ds.dataset(path, filesystem=_filesystem(), format="parquet")


def bbox_filter(bbox) -> ds.Expression:
    x0, y0, x1, y1 = bbox
    return ((pc.field("bbox", "xmin") < x1) & (pc.field("bbox", "xmax") > x0)
            & (pc.field("bbox", "ymin") < y1) & (pc.field("bbox", "ymax") > y0))


def fetch(theme: str, type_: str, bbox, dest: Path, columns: list[str], *,
          overwrite: bool = False, row_filter: ds.Expression | None = None) -> Path:
    dest = Path(dest)
    if dest.exists() and not overwrite:
        return dest
    flt = bbox_filter(bbox)
    if row_filter is not None:
        flt = flt & row_filter
    table = _dataset(theme, type_).to_table(columns=columns, filter=flt)
    table = table.replace_schema_metadata({
        **(table.schema.metadata or {}),
        b"ffe.source": f"overture {config.OVERTURE_RELEASE} theme={theme} type={type_}".encode(),
        b"ffe.bbox": ",".join(map(str, bbox)).encode(),
    })
    dest.parent.mkdir(parents=True, exist_ok=True)
    pq.write_table(table, dest, compression="zstd")
    return dest


def fetch_buildings(bbox, dest: Path, **kw) -> Path:
    return fetch("buildings", "building", bbox, dest, BUILDING_COLUMNS, **kw)


def fetch_roads(bbox, dest: Path, **kw) -> Path:
    return fetch("transportation", "segment", bbox, dest, SEGMENT_COLUMNS,
                 row_filter=pc.field("subtype") == "road", **kw)


def fetch_fire_stations(bbox, dest: Path, **kw) -> Path:
    return fetch("places", "place", bbox, dest, PLACE_COLUMNS,
                 row_filter=pc.field("basic_category") == "fire_station", **kw)


def primary_source(sources_cell) -> str:
    """Dataset name of the first source record ('OpenStreetMap', 'Microsoft ML Buildings', ...)."""
    if not sources_cell:
        return "unknown"
    return sources_cell[0].get("dataset") or "unknown"


def read(path: Path) -> pa.Table:
    return pq.read_table(path)

"""Per-zone orchestration: plan -> fetch -> build -> export."""

from __future__ import annotations

import json
from datetime import timedelta
from pathlib import Path

import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq
import shapely
from pyproj import Transformer

from . import config, ffeg, graph as graphmod, labels as labelmod
from .net import DownloadTooLarge, HostBlocked
from .sources import dem, era5, gem, overture, shakemap
from .zones import Zone

FOOTPRINT_SRC_CODES = {"OpenStreetMap": 0, "Microsoft ML Buildings": 1, "Google Open Buildings": 2}
FOOTPRINT_SRC_PREFIRE_FILE = 4
FOOTPRINT_SRC_OTHER = 3

# where: "cloud"  = reachable from the cloud session and from a PC
#        "pc"     = host blocked in the cloud session; run on the local PC
#        "manual" = obtained by hand (request, digitize, portal download)
SOURCES = {
    "buildings":     {"where": "cloud", "file": "raw/overture_buildings.parquet", "typical_mb": "1-20 per zone"},
    "roads":         {"where": "cloud", "file": "raw/overture_roads.parquet", "typical_mb": "1-10 per zone"},
    "fire_stations": {"where": "cloud", "file": "raw/overture_fire_stations.parquet", "typical_mb": "<1"},
    "weather":       {"where": "cloud", "file": "raw/era5_point.csv", "typical_mb": "<1 (range reads ~4 MB/var)"},
    "dem":           {"where": "cloud", "file": "raw/dem/", "typical_mb": "5-40 per 1x1 deg tile"},
    "gem":           {"where": "cloud", "file": "raw/gem_taxonomy_summary.csv", "typical_mb": "<0.1"},
    "shakemap":      {"where": "pc", "file": "local_inputs/shakemap_grid.xml", "typical_mb": "1-20"},
}


def _zone_path(zone: Zone, rel: str) -> Path:
    return config.zone_dir(zone.id) / rel


def plan(zone: Zone) -> list[dict]:
    rows = []
    for name, s in SOURCES.items():
        if name == "shakemap" and zone.kind != "ffe":
            continue
        if name == "gem" and not zone.gem:
            continue
        p = _zone_path(zone, s["file"])
        exists = p.exists() and (not p.is_dir() or any(p.iterdir()))
        rows.append({"item": name, "where": s["where"], "path": str(p), "present": exists,
                     "typical_mb": s["typical_mb"]})
    for li in zone.local_inputs:
        p = config.zone_dir(zone.id) / "local_inputs" / li["file"]
        if li["file"] == "shakemap_grid.xml":
            continue  # already listed above
        rows.append({"item": li["file"], "where": "manual", "path": str(p), "present": p.exists(),
                     "typical_mb": str(li.get("size_mb", "?")), "what": li.get("what", "")})
    return rows


def fetch(zone: Zone, only: list[str] | None = None, allow_large: bool = False, log=print) -> dict:
    """Fetch every source that the current network allows; report the rest."""
    results = {}
    raw = config.raw_dir(zone.id)
    config.local_inputs_dir(zone.id)

    def run(name, fn):
        if only and name not in only:
            return
        try:
            out = fn()
            results[name] = {"status": "ok", "path": str(out)}
            log(f"  [ok]      {name}: {out}")
        except HostBlocked as e:
            results[name] = {"status": "blocked", "detail": str(e)}
            log(f"  [blocked] {name}: {e}  -> run this step on the local PC")
        except DownloadTooLarge as e:
            results[name] = {"status": "too_large", "detail": str(e)}
            log(f"  [skip]    {name}: {e}")
        except Exception as e:  # keep going: one broken source must not block the others
            results[name] = {"status": "error", "detail": f"{type(e).__name__}: {e}"}
            log(f"  [error]   {name}: {type(e).__name__}: {str(e).splitlines()[0][:200]}")

    run("buildings", lambda: overture.fetch_buildings(zone.bbox, raw / "overture_buildings.parquet"))
    run("roads", lambda: overture.fetch_roads(zone.bbox, raw / "overture_roads.parquet"))
    run("fire_stations", lambda: overture.fetch_fire_stations(_pad_bbox(zone.bbox, 0.05),
                                                               raw / "overture_fire_stations.parquet"))
    t_ref = zone.ignition_time_utc or zone.event_time_utc
    lon, lat = zone.center
    run("weather", lambda: era5.fetch_point_series(lat, lon, t_ref - timedelta(hours=6),
                                                   t_ref + timedelta(hours=36), raw / "era5_point.csv"))
    run("dem", lambda: dem.fetch_tiles(zone.bbox, raw / "dem", allow_large=allow_large)[0].parent)
    if zone.gem:
        run("gem", lambda: gem.fetch_taxonomy_summary(zone.gem["region"], zone.gem["country"],
                                                      raw / "gem_taxonomy_summary.csv"))
    if zone.kind == "ffe" and zone.usgs_event_id:
        dest = config.local_inputs_dir(zone.id) / "shakemap_grid.xml"
        run("shakemap", lambda: dest if dest.exists() else
            shakemap.fetch_grid(zone.usgs_event_id, dest, allow_large=allow_large))
    return results


def _pad_bbox(bbox, deg):
    return (bbox[0] - deg, bbox[1] - deg, bbox[2] + deg, bbox[3] + deg)


def _load_footprints(zone: Zone):
    """Pre-event footprints file wins over Overture when present."""
    prefire = config.zone_dir(zone.id) / "local_inputs" / "prefire_buildings.geojson"
    if prefire.exists():
        geoms, props = labelmod.read_geojson(prefire)
        n = len(geoms)
        return (geoms, np.full(n, np.nan), np.full(n, np.nan),
                np.full(n, FOOTPRINT_SRC_PREFIRE_FILE, np.uint8), f"prefire file {prefire.name}")
    path = config.zone_dir(zone.id) / "raw" / "overture_buildings.parquet"
    if not path.exists():
        raise FileNotFoundError(f"{path} missing: run `ffe fetch {zone.id}` first")
    t = pq.read_table(path)
    geoms = shapely.from_wkb(np.asarray(t["geometry"].to_pylist(), dtype=object))
    height = np.array([np.nan if v is None else v for v in t["height"].to_pylist()], dtype=np.float64)
    floors = np.array([np.nan if v is None else v for v in t["num_floors"].to_pylist()], dtype=np.float64)
    src = np.array([FOOTPRINT_SRC_CODES.get(overture.primary_source(s), FOOTPRINT_SRC_OTHER)
                    for s in t["sources"].to_pylist()], dtype=np.uint8)
    meta = (t.schema.metadata or {}).get(b"ffe.source", b"overture").decode()
    return geoms, height, floors, src, meta


def build(zone: Zone, radius_m: float = 40.0, grid_cell_m: float = 25.0, log=print) -> dict:
    zdir = config.zone_dir(zone.id)
    out_dir = config.derived_dir(zone.id)
    geoms, height, floors, fsrc, fp_source = _load_footprints(zone)
    log(f"  footprints: {len(geoms)} from {fp_source}")

    g, keep = graphmod.build(geoms, height=height, floors=floors, origin_lonlat=zone.center,
                             radius_m=radius_m, grid_cell_m=grid_cell_m)
    n = g.n_nodes
    local_geoms = g.meta.pop("geoms_local")
    inv = Transformer.from_crs(g.crs_proj4, "EPSG:4326", always_xy=True)
    lon, lat = inv.transform(g.node["x_m"].astype(np.float64), g.node["y_m"].astype(np.float64))
    lon, lat = np.asarray(lon), np.asarray(lat)
    report = {"zone": zone.id, "n_nodes": n, "n_edges": g.n_edges, "radius_m": radius_m,
              "footprint_source": fp_source, "degree": graphmod.degree_stats(g), "warnings": []}

    extra = {"footprint_src": fsrc[keep]}
    hs = np.bincount(g.node["height_src"], minlength=4)
    report["height_sources"] = {"overture": int(hs[0]), "floors": int(hs[1]), "default": int(hs[2]),
                                "gba": int(hs[3])}
    if hs[2] > 0.5 * n:
        report["warnings"].append(f"{hs[2]}/{n} heights are defaults; add GlobalBuildingAtlas heights (PC step)")

    tiles = sorted((zdir / "raw" / "dem").glob("*.tif"))
    extra["elevation_m"] = (dem.sample(tiles, lon, lat) if tiles else np.full(n, np.nan)).astype(np.float32)
    if not tiles:
        report["warnings"].append("no DEM tiles; elevation is NaN")

    grid_xml = zdir / "local_inputs" / "shakemap_grid.xml"
    if grid_xml.exists():
        sg = shakemap.parse_grid_xml(grid_xml)
        for fld, name in (("PGA", "pga_pctg"), ("PGV", "pgv_cms"), ("MMI", "mmi")):
            if fld in sg.fields:
                extra[name] = sg.sample(fld, lon, lat).astype(np.float32)
        report["shakemap"] = {"event_id": sg.event_id, "fields": list(sg.fields)}
    elif zone.kind == "ffe":
        report["warnings"].append("ShakeMap missing (PC step: ffe fetch --only shakemap)")

    lab = labelmod.UNKNOWN * np.ones(n, dtype=np.int8)
    label_file = zdir / "local_inputs" / (zone.labels.get("file") or "labels.geojson")
    if label_file.exists():
        lgeoms, lprops = labelmod.read_geojson(label_file)
        if zone.labels.get("kind") == "burned_area_polygon":
            lab = labelmod.from_burned_polygons(local_geoms, lgeoms, g.crs_proj4)
            report["footprint_check"] = labelmod.footprint_coverage_check(local_geoms, lgeoms, g.crs_proj4)
            if report["footprint_check"]["verdict"] != "ok":
                report["warnings"].append("burned area has too few footprints: " + zone.footprint_caveat)
        else:
            lab = labelmod.from_damage_points(local_geoms, lgeoms, labelmod.dins_classes(lprops), g.crs_proj4)
        report["labels"] = {str(k): int(v) for k, v in zip(*np.unique(lab, return_counts=True))}
    else:
        report["warnings"].append(f"labels missing: {label_file.name} ({zone.labels.get('source', '')})")
    extra["label"] = lab

    weather = None
    wpath = zdir / "raw" / "era5_point.csv"
    if wpath.exists():
        weather = era5.read_series(wpath)
    gem_prior = None
    gpath = zdir / "raw" / "gem_taxonomy_summary.csv"
    if gpath.exists():
        shares = gem.macro_shares(gpath)
        gem_prior = {"res_macro_shares": {k: round(v, 4) for k, v in shares.items()},
                     "res_combustible_share": round(gem.combustible_share(shares), 4)}
        report["gem"] = gem_prior

    manifest_extra = {
        "zone": {"id": zone.id, "name": zone.name, "stage": zone.stage, "kind": zone.kind,
                 "bbox": list(zone.bbox), "event_time_utc": zone.event_time_utc.isoformat(),
                 "ignition_time_utc": zone.ignition_time_utc.isoformat() if zone.ignition_time_utc else None},
        "provenance": {"footprints": fp_source, "overture_release": config.OVERTURE_RELEASE,
                       "dem_tiles": [t.name for t in tiles]},
        "weather_hourly": weather,
        "gem_prior": gem_prior,
        "codes": {"label": "-1 unknown, 0 none, 1 affected, 2 minor, 3 major, 4 destroyed/burned",
                  "height_src": "0 overture, 1 floors*3m, 2 default 6m, 3 GlobalBuildingAtlas",
                  "footprint_src": "0 OSM, 1 Microsoft, 2 Google, 3 other, 4 pre-event file"},
    }
    ffeg_path = ffeg.write(out_dir / "graph.ffeg", g, extra_node=extra, manifest_extra=manifest_extra)

    cols = {**g.node, **extra, "lon": lon, "lat": lat,
            "in_degree": np.diff(g.in_offsets.astype(np.int64))}
    pq.write_table(pa.table(cols), out_dir / "nodes.parquet", compression="zstd")
    report["outputs"] = {"ffeg": str(ffeg_path), "ffeg_mb": round(ffeg_path.stat().st_size / 1e6, 3),
                         "nodes_parquet": str(out_dir / "nodes.parquet")}
    (out_dir / "report.json").write_text(json.dumps(report, indent=2))
    return report

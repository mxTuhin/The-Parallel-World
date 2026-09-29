# ffe-pipeline: open data → building fire graph → Unity

Python pipeline for the fire-following-earthquake (FFE) research plan
(`Documentation/Research/ResearchPlan_FFE.md`). For one small **study zone** it:

1. fetches open data (building footprints, roads, fire stations, weather, terrain, construction mix, ground shaking);
2. builds a **building fire graph**: buildings as nodes, and directed edges between buildings within a radius, carrying gap, bearing and facing width;
3. attaches **observed outcomes** (burned area or damage inspections) and checks that the footprints are pre-event;
4. exports **`graph.ffeg`**, a self-describing binary the Unity engine loads, plus `nodes.parquet` and `report.json` for analysis.

The approach is to start small and validate before scaling up. Zones are listed in `ffe/zones.yaml` as a ladder:

| Stage | Zone | Why it comes first |
|---|---|---|
| Z1 | `wajima2024` | Smallest documented FFE conflagration (~240 buildings burned) |
| Z1b | `itoigawa2016` | Wind-driven spread benchmark already matched by a published model |
| Z2 | `eaton2025_core` | Thousands of open building-level labels (CAL FIRE DINS) |
| Z2b | `lahaina2023` | Hold-out test |
| Z3 | `kobe1995_nagata` | Classic multi-ignition FFE; needs 1995 footprints |

## Setup (cloud session or local PC)

```bash
cd Tools/ffe_pipeline
python -m venv .venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -e ".[dev]"
python -m pytest -q                # 18 offline tests
```

Data goes to `<repo>/FFEData/` (git-ignored). Override the location with `FFE_DATA_DIR`.

## Commands

```bash
python -m ffe zones                       # list zones
python -m ffe plan wajima2024             # what the zone needs, where each item can come from, what is present
python -m ffe fetch wajima2024            # download everything the network allows
python -m ffe fetch wajima2024 --only shakemap
python -m ffe build wajima2024            # graph + labels + export -> FFEData/zones/wajima2024/derived/
python -m ffe fetch-arcgis eaton2025_core <FeatureServer layer URL>   # DINS damage labels
python -m ffe usgs-lookup --time 1995-01-16T20:46 --minmag 6.5        # find a USGS event id
```

## Cloud session vs local PC

| | Cloud research session | Local PC |
|---|---|---|
| Download limit | 90 MB per file (auto-detected) | **none**: the chosen zone decides the size |
| Overture footprints, roads, fire stations | yes (S3 bbox query) | yes |
| ERA5 weather | yes (HTTP range reads, a few MB) | yes |
| Copernicus DEM | yes | yes |
| GEM construction summary | yes | yes |
| USGS ShakeMap | **blocked** | yes |
| CAL FIRE DINS labels | **blocked** | yes |
| NASA Earthdata (FIRMS, Black Marble, ARIA) | **blocked** | yes (free Earthdata login) |
| GlobalBuildingAtlas heights | **blocked** | yes |

A blocked step never fails the whole run. `fetch` reports it and prints the exact command to re-run on the PC. Manually obtained files (digitized burned areas, pre-event footprints) go in `FFEData/zones/<zone>/local_inputs/` under the names that `plan` shows.

## PC checklist for the first zone (Z1, Wajima)

1. `python -m ffe fetch wajima2024`: everything, including ShakeMap `us6000m0xl`.
2. Burned-area polygon → `local_inputs/burned_area.geojson`. Digitize from the GSI (Geospatial Information Authority of Japan) Noto 2024 burned-area map, or from Nishino (2025).
3. `python -m ffe build wajima2024`, then read `report.json → footprint_check`.
   - If it reports **PRE-EVENT FOOTPRINTS LIKELY MISSING**, add `local_inputs/prefire_buildings.geojson` (GSI Kiban Chizu building outlines from before 2024) and rebuild.
4. Heights: 3,885 of 4,313 Wajima buildings currently use the 6 m default. Add GlobalBuildingAtlas heights (next pipeline step).

## Outputs per zone

```
FFEData/zones/<zone>/
  raw/            downloaded inputs (parquet, csv, tif)
  local_inputs/   files you add by hand
  derived/
    graph.ffeg     engine input (format spec in ffe/ffeg.py)
    nodes.parquet  one row per building (all node fields + lon/lat + degree)
    report.json    QA: counts, degree, height sources, labels, footprint check, warnings
```

## FFEG v1 in one paragraph

The file starts with `"FFEG"`, a uint32 version and a uint64 manifest length, followed by the JSON manifest and then 16-byte-aligned little-endian arrays:
* **node arrays:** `x_m`, `y_m`, `area_m2`, `perimeter_m`, `height_m`, `height_src`, `footprint_src`, `elevation_m`, `label`, and `pga_pctg` / `pgv_cms` / `mmi` when ShakeMap is present;
* **CSR of incoming edges:** `in_offsets`, then per edge `src`, `gap_m`, `bearing_rad`, `facing_m`;
* **firebrand grid:** `cell_offsets`, `cell_items`.

The manifest also carries the zone metadata, the hourly ERA5 weather, the GEM construction prior and provenance. Coordinates are metres in a local transverse-Mercator frame centred on the zone.

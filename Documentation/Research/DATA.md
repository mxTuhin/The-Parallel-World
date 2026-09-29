# Data Inventory: Where, How, How Big

*For the fire-following-earthquake (FFE) research plan. Sizes marked **measured** were measured from the cloud session on 2026-09-29. Everything else is an estimate to confirm on the PC.*

**Rule of thumb:** the whole method runs on a small zone first. Each small zone needs only **about 5–30 MB** of data. The large global files are never downloaded whole: they are queried by bounding box or byte range.

## 1. What a zone needs

| # | Data | Role in the research | Source and host | Access | Licence | Size | Cloud session | Pipeline |
|---|---|---|---|---|---|---|---|---|
| 1 | Building footprints | Graph nodes and geometry | Overture Maps buildings (OSM + Microsoft + Google conflation), `s3://overturemaps-us-west-2`, release 2026-09-23.1 | Anonymous S3, bbox query | ODbL / CDLA | Global 277 GB (512 files, **measured**); per zone **0.4–15 MB** (**measured**, table 2) | ✅ | `fetch --only buildings` |
| 2 | Roads | Fire-engine access, road blockage | Overture transportation segments | Anonymous S3 | ODbL | Per zone <1 MB (**measured**: Wajima 76 KB) | ✅ | `--only roads` |
| 3 | Fire stations | Suppression model | Overture places (`basic_category = fire_station`) | Anonymous S3 | CDLA / ODbL | <10 KB (**measured**) | ✅ | `--only fire_stations` |
| 4 | Wind, temperature, humidity | Spread direction; firebrand transport | ERA5 hourly via NSF NCAR mirror `nsf-ncar-era5.s3.amazonaws.com` (ds633.0) | Anonymous HTTPS range reads | Copernicus licence (free) | Monthly variable file 1.1–1.6 GB (**measured**), but a zone read transfers **one chunk (~a few MB) per variable** (**measured**: 3.3 s) | ✅ | `--only weather` |
| 5 | Terrain | Slope effect on spread (a gap named in the FFE review) | Copernicus GLO-30 DEM `copernicus-dem-30m.s3.amazonaws.com` | Anonymous S3 | Copernicus DEM licence (free) | **5.3 MB** per 1°×1° tile (**measured**, N37 E136) | ✅ | `--only dem` |
| 6 | Construction mix (prior) | Share of combustible (wooden) buildings | GEM Global Exposure Model v2026.0.0, raw GitHub | HTTPS | CC BY-NC-SA 4.0 | Taxonomy summary **44 KB** (**measured**) | ✅ | `--only gem` |
| 7 | Ground shaking (PGA, PGV, MMI) | Damage state → ignitions | USGS ShakeMap `grid.xml` (event API at earthquake.usgs.gov) | HTTPS, no key | Public domain | ~1–20 MB per event (est.) | ❌ blocked → **PC** | `--only shakemap` |
| 8 | Historical ShakeMaps (1900–2019) | Hindcasts of older events | USGS ShakeMap Atlas (ComCat, contributor "ShakeMap Atlas") | HTTPS | Public domain | Per event as #7 | ❌ → PC | `usgs-lookup`, then `--only shakemap` |
| 9 | Building heights | Flame height, view factors, firebrand release | GlobalBuildingAtlas (TUM, mediaTUM; also Google Earth Engine community catalogue) | HTTPS download per tile | Open (see dataset page) | Per-tile raster/polygons; likely >90 MB per tile (est.) | ❌ blocked → **PC** | next pipeline step (`--gba`) |
| 10 | Building-level damage labels (California) | Calibration and validation of spread | CAL FIRE DINS (e.g. "DINS 2025 Eaton Public View"), ArcGIS FeatureServer / data.ca.gov | HTTPS, no key | Public | ~10–30 MB per incident (est.) | ❌ blocked → **PC** | `fetch-arcgis <zone> <layer URL>` |
| 11 | Burned-area polygons (Japan) | Labels for Wajima, Itoigawa, Kobe | GSI Japan maps; Nishino (2025); municipal/FDMA reports | Manual digitizing / portal | Varies | <1 MB | manual | `local_inputs/burned_area.geojson` |
| 12 | Pre-event footprints | Replace present-day footprints where burned buildings were removed | GSI Kiban Chizu (Japan); BRI 1995 GIS (Kobe, by request); OSM history | Portal / request | Varies | 1–20 MB per zone (est.) | manual | `local_inputs/prefire_buildings.geojson` |
| 13 | Satellite active fire | Fire timing; forecast updating (loop L3) | NASA FIRMS (VIIRS 375 m, MODIS) | Free MAP_KEY | Open | CSV archives: KB–MB per region and period (est.) | ❌ blocked → PC | later step |
| 14 | Night lights / power outage | Ignition covariate (electrical re-ignition) | NASA Black Marble VNP46A2 (LAADS DAAC) | Free Earthdata login | Open | Tens of MB per daily tile (est.) | ❌ blocked → PC | later step |
| 15 | Satellite damage maps | Damage-state check | NASA JPL ARIA Damage Proxy Maps | Earthdata / ARIA share | Open | Tens–hundreds of MB per event (est.) | ❌ blocked → PC | later step |
| 16 | 1 km exposure (full) | Construction mix at 1 km | GEM repository country folders (`csv.gz`) | git (sparse checkout of one country) | CC BY-NC-SA 4.0 | Large repository; clone one country only | PC | later step |
| 17 | Ignition records | Ignition model (RQ1) | US: Davidson (2009) GLMM data; Japan: FDMA/JAFSE via Nishino | Papers / collaboration | Varies | <1 MB | manual | later step |
| 18 | Stochastic earthquake sets | Global risk (RQ3) | GEM Global Hazard Mosaic + OpenQuake engine | Download + run | CC BY-NC-SA 4.0 | GB-scale per region (est.) | PC | stage 5 |

## 2. Measured results for real zones (cloud session)

| Zone | Buildings (Overture) | Main footprint sources | Height known | Graph edges (40 m) | `graph.ffeg` | Raw data |
|---|---|---|---|---|---|---|
| **wajima2024** (Z1) | 4,313 | OSM 3,755; Microsoft 354; other 205 | 428 / 4,313 (from floors) | 116,562 | 2.04 MB | ~5.7 MB |
| **itoigawa2016** (Z1b) | 3,011 | OSM 2,394; Microsoft 531 | 2 / 3,011 | 70,034 | 1.25 MB | ~4.8 MB |
| **eaton2025_core** (Z2) | 18,942 | OSM 29k / Microsoft 8.6k in the wider bbox | 17,955 / 18,942 | 286,160 | 5.35 MB | ~44 MB (41 MB is the DEM tile) |
| kobe1995_nagata (Z3) | 35,344 (present-day; **not valid for 1995**) | OSM 28,188 | | | | 10.7 MB footprints |
| lahaina2023 (Z2b) | 1,667 | OSM 1,083; Microsoft 584 | 402 | | | 0.6 MB footprints |

Build time for a zone graph: ~2 s. Mean neighbours within 40 m: Wajima 27, Itoigawa 23, Altadena 15. Dense Japanese wooden towns versus US suburbs is exactly the connectivity contrast RQ2 tests.

**Key data findings:**
* **Heights in Japan are nearly absent from Overture**, so GlobalBuildingAtlas (a PC step) is required before calibrating. US zones already have heights.
* **Present-day footprints may omit burned buildings.** `build` runs a footprint-coverage check against the burned area and warns when pre-event footprints are needed. This is mandatory before calibrating Wajima, Itoigawa and Lahaina, and Kobe cannot be done at all without the 1995 footprints.
* **Wajima weather** (ERA5, event day): a northerly wind of 4–7 m/s before the quake that eased to about 1–3 m/s over the evening. The fire ran under light-to-moderate wind, which makes it a spread test driven by density rather than wind.

## 3. Hosts blocked in the cloud session (all fine on a home connection)

earthquake.usgs.gov · data.ca.gov · *.arcgis.com / gis.data.ca.gov · firms.modaps.eosdis.nasa.gov · urs.earthdata.nasa.gov · ladsweb.modaps.eosdis.nasa.gov · goldsmr4.gesdisc.eosdis.nasa.gov · e4ftl01.cr.usgs.gov · cds.climate.copernicus.eu · mediatum.ub.tum.de · opentopography.org · download.geofabrik.de · overpass-api.de · zenodo.org · huggingface.co · github.com (raw.githubusercontent.com works).

To use them in future cloud sessions, add them under the environment's **Network access** settings.

## 4. Local-PC storage budget

| Scope | Disk |
|---|---|
| One pilot zone (all inputs + derived) | 10–100 MB (bigger if a GlobalBuildingAtlas tile is kept) |
| All five ladder zones | < 2 GB including GBA tiles |
| One city (10⁵–10⁶ buildings) | 0.2–2 GB |
| Stage 5 global runs | tens of GB of derived summaries; hazard sets GB-scale per region |

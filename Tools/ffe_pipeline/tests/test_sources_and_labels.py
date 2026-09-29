import http.server
import threading
from datetime import datetime, timezone

import numpy as np
import pytest
import shapely

from ffe import config, labels as labelmod, net
from ffe.sources import arcgis, dem, era5, shakemap
from test_graph_and_format import LAT0, LON0, squares_lonlat
from ffe import graph as graphmod

GRID_XML = """<?xml version="1.0" encoding="US-ASCII" standalone="yes"?>
<shakemap_grid xmlns="http://earthquake.usgs.gov/eqcenter/shakemap" event_id="test01" shakemap_version="4">
<grid_specification lon_min="136.0" lat_min="37.0" lon_max="137.0" lat_max="38.0"
  nominal_lon_spacing="0.5" nominal_lat_spacing="0.5" nlon="3" nlat="3" />
<grid_field index="1" name="LON" units="dd" />
<grid_field index="2" name="LAT" units="dd" />
<grid_field index="3" name="PGV" units="cms" />
<grid_data>
136.0 38.0 10
136.5 38.0 20
137.0 38.0 30
136.0 37.5 40
136.5 37.5 50
137.0 37.5 60
136.0 37.0 70
136.5 37.0 80
137.0 37.0 90
</grid_data>
</shakemap_grid>
"""


def test_shakemap_parse_and_bilinear(tmp_path):
    p = tmp_path / "grid.xml"
    p.write_text(GRID_XML)
    g = shakemap.parse_grid_xml(p)
    assert g.event_id == "test01" and g.fields["PGV"].shape == (3, 3)
    v = g.sample("PGV", np.array([136.0, 136.25, 136.5, 137.5]), np.array([38.0, 37.75, 37.5, 37.5]))
    assert v[0] == pytest.approx(10) and v[2] == pytest.approx(50)
    assert v[1] == pytest.approx((10 + 20 + 40 + 50) / 4)   # centre of the NW cell
    assert np.isnan(v[3])                                    # outside the grid


def test_era5_url_and_grid_index():
    url = era5.monthly_url("u10", 2024, 2)
    assert url.endswith("e5.oper.an.sfc/202402/e5.oper.an.sfc.128_165_10u.ll025sc.2024020100_2024022923.nc")
    assert era5.grid_index(90.0, 0.0) == (0, 0)
    assert era5.grid_index(37.39, 136.90) == (210, 548)
    assert era5.grid_index(34.2, -118.15) == (223, 967)     # negative longitudes wrap to 0-360


def test_relative_humidity_saturation():
    assert era5.relative_humidity(np.array([290.0]), np.array([290.0]))[0] == pytest.approx(100.0)


def test_dem_tile_names():
    assert dem.tile_name(37.39, 136.90) == "Copernicus_DSM_COG_10_N37_00_E136_00_DEM"
    assert dem.tile_name(34.2, -118.15) == "Copernicus_DSM_COG_10_N34_00_W119_00_DEM"
    assert dem.tile_name(-33.5, 18.4) == "Copernicus_DSM_COG_10_S34_00_E018_00_DEM"
    assert len(dem.tiles_for_bbox((136.9, 37.9, 137.1, 38.1))) == 4


def test_dins_label_mapping():
    assert arcgis.dins_label("Destroyed (>50%)") == 4
    assert arcgis.dins_label("No Damage") == 0
    assert arcgis.dins_label(None) == -1 and arcgis.dins_label("???") == -1


def _local_graph():
    specs = [(0, 0, 10), (15, 0, 10), (30, 0, 10), (100, 0, 10)]
    g, _ = graphmod.build(squares_lonlat(specs), origin_lonlat=(LON0, LAT0))
    return g, g.meta["geoms_local"]


def _lonlat_box(x0, y0, x1, y1):
    from pyproj import Transformer
    inv = Transformer.from_crs(graphmod.local_proj4(LON0, LAT0), "EPSG:4326", always_xy=True)
    xs, ys = [x0, x1, x1, x0, x0], [y0, y0, y1, y1, y0]
    lon, lat = inv.transform(xs, ys)
    return shapely.Polygon(zip(lon, lat))


def test_burned_polygon_labels_and_coverage():
    g, local = _local_graph()
    burned = [_lonlat_box(-6, -6, 21, 6)]                 # covers buildings 0 and 1 fully
    lab = labelmod.from_burned_polygons(local, burned, g.crs_proj4)
    assert list(lab) == [4, 4, 0, 0]
    chk = labelmod.footprint_coverage_check(local, burned, g.crs_proj4, ring_m=30)
    assert chk["coverage_inside"] > 0


def test_coverage_check_flags_missing_buildings():
    g, local = _local_graph()
    burned = [_lonlat_box(40, -20, 90, 20)]               # empty area next to buildings
    chk = labelmod.footprint_coverage_check(local, burned, g.crs_proj4, ring_m=30)
    assert chk["verdict"] != "ok"


def test_damage_points_nearest_worst():
    g, local = _local_graph()
    from pyproj import Transformer
    inv = Transformer.from_crs(g.crs_proj4, "EPSG:4326", always_xy=True)
    pts = [shapely.Point(*inv.transform(x, y)) for x, y in [(0, 0), (1, 1), (30, 0), (60, 0)]]
    lab = labelmod.from_damage_points(local, pts, [2, 4, 0, 4], g.crs_proj4, max_dist_m=15)
    assert list(lab) == [4, -1, 0, -1]                    # worst of two points; far point ignored


class _Handler(http.server.BaseHTTPRequestHandler):
    body = b"x" * 3000

    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Length", str(len(self.body)))
        self.end_headers()
        self.wfile.write(self.body)

    def log_message(self, *a):
        pass


@pytest.fixture
def server():
    srv = http.server.HTTPServer(("127.0.0.1", 0), _Handler)
    t = threading.Thread(target=srv.serve_forever, daemon=True)
    t.start()
    yield f"http://127.0.0.1:{srv.server_port}/f.bin"
    srv.shutdown()


def test_download_guard(tmp_path, server, monkeypatch):
    monkeypatch.setattr(config, "MAX_DOWNLOAD_MB", 0.001)          # 1 kB limit
    with pytest.raises(net.DownloadTooLarge):
        net.download(server, tmp_path / "a.bin")
    assert not (tmp_path / "a.bin").exists() and not (tmp_path / "a.bin.part").exists()
    out = net.download(server, tmp_path / "b.bin", allow_large=True)
    assert out.stat().st_size == 3000
    monkeypatch.setattr(config, "MAX_DOWNLOAD_MB", 0)                # 0 = unlimited (local PC default)
    assert net.download(server, tmp_path / "c.bin").stat().st_size == 3000


def test_zone_catalogue_valid():
    from ffe.zones import load_zones
    zones = load_zones()
    assert "wajima2024" in zones
    for z in zones.values():
        assert z.event_time_utc.tzinfo is not None
        assert z.kind in {"ffe", "conflagration", "scaling"}
        if z.kind == "ffe":
            assert z.labels.get("file")

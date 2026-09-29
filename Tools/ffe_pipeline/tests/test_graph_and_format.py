import math

import numpy as np
import pytest
import shapely
from pyproj import Transformer

from ffe import ffeg, graph as graphmod
from ffe.graph import local_proj4

LON0, LAT0 = 136.90, 37.39


def squares_lonlat(specs):
    """Build lon/lat polygons from local-metre squares (x_center, y_center, side)."""
    inv = Transformer.from_crs(local_proj4(LON0, LAT0), "EPSG:4326", always_xy=True)
    out = []
    for cx, cy, side in specs:
        h = side / 2
        xs = np.array([cx - h, cx + h, cx + h, cx - h, cx - h])
        ys = np.array([cy - h, cy - h, cy + h, cy + h, cy - h])
        lon, lat = inv.transform(xs, ys)
        out.append(shapely.Polygon(zip(lon, lat)))
    return np.array(out, dtype=object)


def edge(g, s, d):
    lo, hi = g.in_offsets[d], g.in_offsets[d + 1]
    idx = np.flatnonzero(g.edge["src"][lo:hi] == s)
    return None if len(idx) == 0 else lo + idx[0]


def test_two_squares_gap_bearing_facing():
    # A at (0,0), B at (15,0): 10 m squares, 5 m gap, B due east of A.
    g, keep = graphmod.build(squares_lonlat([(0, 0, 10), (15, 0, 10)]), origin_lonlat=(LON0, LAT0), radius_m=40)
    assert g.n_nodes == 2 and g.n_edges == 2 and len(keep) == 2
    e = edge(g, 0, 1)
    assert g.edge["gap_m"][e] == pytest.approx(5.0, abs=0.01)
    assert g.edge["bearing_rad"][e] == pytest.approx(0.0, abs=1e-3)          # towards east
    assert g.edge["facing_m"][e] == pytest.approx(10.0, abs=0.01)
    e2 = edge(g, 1, 0)
    assert g.edge["bearing_rad"][e2] == pytest.approx(math.pi, abs=1e-3) or \
        g.edge["bearing_rad"][e2] == pytest.approx(-math.pi, abs=1e-3)


def test_facing_is_min_of_both_projections():
    # Big 20 m square next to a small 4 m square: they face each other over 4 m.
    g, _ = graphmod.build(squares_lonlat([(0, 0, 20), (20, 0, 4)]), origin_lonlat=(LON0, LAT0))
    assert g.edge["facing_m"][edge(g, 0, 1)] == pytest.approx(4.0, abs=0.01)


def test_radius_cuts_edges_and_csr_is_consistent():
    specs = [(0, 0, 10), (15, 0, 10), (100, 0, 10)]            # third is 85 m away
    g, _ = graphmod.build(squares_lonlat(specs), origin_lonlat=(LON0, LAT0), radius_m=40)
    assert g.n_edges == 2
    assert edge(g, 0, 2) is None
    assert g.in_offsets[0] == 0 and g.in_offsets[-1] == g.n_edges
    assert np.all(np.diff(g.in_offsets.astype(np.int64)) >= 0)


def test_tiny_slivers_dropped():
    g, keep = graphmod.build(squares_lonlat([(0, 0, 10), (15, 0, 1)]), origin_lonlat=(LON0, LAT0), min_area_m2=4)
    assert g.n_nodes == 1 and list(keep) == [0]


def test_heights_priority():
    h, src = graphmod.infer_heights(np.array([np.nan, 12.0, np.nan]), np.array([3.0, 2.0, np.nan]))
    assert list(h) == [9.0, 12.0, graphmod.DEFAULT_HEIGHT_M]
    assert list(src) == [graphmod.HEIGHT_FROM_FLOORS, graphmod.HEIGHT_FROM_OVERTURE, graphmod.HEIGHT_DEFAULT]


def test_firebrand_grid_covers_every_building_once():
    specs = [(x, y, 8) for x in range(0, 200, 20) for y in range(0, 100, 20)]
    g, _ = graphmod.build(squares_lonlat(specs), origin_lonlat=(LON0, LAT0), grid_cell_m=25)
    items = g.grid["cell_items"]
    assert sorted(items.tolist()) == list(range(g.n_nodes))
    assert g.grid["cell_offsets"][-1] == g.n_nodes


def test_ffeg_roundtrip(tmp_path):
    g, _ = graphmod.build(squares_lonlat([(0, 0, 10), (15, 0, 10), (0, 18, 6)]), origin_lonlat=(LON0, LAT0))
    extra = {"label": np.array([4, 0, -1], dtype=np.int8)}
    path = ffeg.write(tmp_path / "t.ffeg", g, extra_node=extra, manifest_extra={"zone": {"id": "t"}})
    raw = path.read_bytes()
    assert raw[:4] == b"FFEG"
    m, a = ffeg.read(path)
    assert m["n_nodes"] == 3 and m["n_edges"] == g.n_edges and m["zone"]["id"] == "t"
    for name, arr in {**g.node, **g.edge, **extra, "in_offsets": g.in_offsets}.items():
        np.testing.assert_array_equal(a[name], arr)
    data_start = len(raw) - sum(e["nbytes"] + (-e["nbytes"]) % 16 for e in m["arrays"])
    assert data_start % 16 == 0
    assert all(e["offset"] % 16 == 0 for e in m["arrays"])


def test_ffeg_rejects_float64(tmp_path):
    g, _ = graphmod.build(squares_lonlat([(0, 0, 10), (15, 0, 10)]), origin_lonlat=(LON0, LAT0))
    with pytest.raises(TypeError):
        ffeg.write(tmp_path / "bad.ffeg", g, extra_node={"bad": np.zeros(2)})

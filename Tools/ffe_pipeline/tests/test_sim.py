"""Exact solver: closed-form cases, commit-rule equivalence, baseline convergence, RNG, I/O."""

import numpy as np
import pytest
from scipy.integrate import quad

from ffe.sim import exact, model, rng, scenario, subset

INF = np.inf


def chain(n_nodes, a=30.0, b=0.0, tau0=300.0, tg=300.0, td=1800.0, tx=1200.0, q_cr=10.0, n_exp=1.0,
          both_ways=False):
    """Buildings 0..n-1 in a row; edge k-1 -> k (and k -> k-1 if both_ways)."""
    dst, src = [], []
    for k in range(1, n_nodes):
        dst.append(k), src.append(k - 1)
        if both_ways:
            dst.append(k - 1), src.append(k)
    dst, src = np.array(dst), np.array(src)
    order = np.lexsort((src, dst))
    dst, src = dst[order], src[order]
    off = np.zeros(n_nodes + 1, dtype=np.int64)
    np.cumsum(np.bincount(dst, minlength=n_nodes), out=off[1:])
    ones = np.ones(n_nodes)
    return model.Scenario(n=n_nodes, in_offsets=off, src=src.astype(np.int64),
                          a=np.full(len(src), a), b=np.full(len(src), b),
                          tau0=tau0 * ones, tg=tg * ones, td=td * ones, tx=tx * ones,
                          q_cr=q_cr, ftp_n=n_exp, ftp_mu=0.0, ftp_sigma=0.0,
                          ignitions=np.array([0]), t_end=1e7)


def test_two_buildings_closed_form():
    # r = 30*phi - 10 > 0 once phi > 1/3. Dose in growth = 30*300*4/9 - 10*200 = 2000;
    # remaining 8000 at 20 kW/m2 -> 400 s. Ignition at 300 + 300 + 400 = 1000 s.
    sc = chain(2)
    t, _, _ = exact.exact(sc, np.full(2, 1e4), np.full(2, INF))
    assert t[1] == pytest.approx(1000.0, abs=1e-9)


def test_chain_is_arithmetic():
    sc = chain(12)
    t, _, _ = exact.exact(sc, np.full(12, 1e4), np.full(12, INF))
    assert np.allclose(t, 1000.0 * np.arange(12), atol=1e-8)


def test_general_exponent_matches_quadrature():
    n_exp, F = 1.7, 4e4
    sc = chain(2, n_exp=n_exp)
    t, _, _ = exact.exact(sc, np.full(2, F), np.full(2, INF))

    def dose(T):
        f = lambda s: max(30.0 * exact.phi(s, 300.0, 300.0, 1800.0, 1200.0) - 10.0, 0.0) ** n_exp
        return quad(f, 0, T, points=[300, 600, 2400, 3600], limit=200)[0]
    assert dose(t[1]) == pytest.approx(F, rel=1e-7)


def test_firebrand_clock_closed_form():
    # No radiation (a below q_cr), hazard rate b*phi. Hazard reached by the end of growth
    # = b*tg/2; with E chosen above that, crossing is inside the plateau.
    b, E = 1e-3, 0.5
    sc = chain(2, a=0.0, b=b)
    t, _, _ = exact.exact(sc, np.full(2, INF), np.full(2, E))
    expected = 300 + 300 + (E - b * 300 / 2) / b
    assert t[1] == pytest.approx(expected, rel=1e-12)


def test_burnt_out_source_cannot_ignite():
    # Source decays before the dose reaches the threshold -> never ignites.
    sc = chain(2)
    t, _, _ = exact.exact(sc, np.full(2, 1e7), np.full(2, INF))
    assert t[1] == INF


def random_scenario(n=400, seed=0, tau0_spread=0.0):
    return model.synthetic_scenario(n=n, seed=seed, tau0_spread=tau0_spread)[0]


@pytest.mark.parametrize("spread", [0.0, 0.3])
def test_commit_rules_bit_identical(spread):
    sc = random_scenario(tau0_spread=spread)
    for rep in range(15):
        ftp, e = rng.draw_thresholds(sc.n, 3, rep, sc.ftp_mu, sc.ftp_sigma)
        ref, it_seq, _ = exact.exact(sc, ftp, e, exact.SEQUENTIAL)
        for mode in (exact.GLOBAL, exact.LOCAL):
            t, it, _ = exact.exact(sc, ftp, e, mode)
            assert np.array_equal(t, ref)
            assert it <= it_seq


def test_stepped_converges_to_exact():
    sc = random_scenario(seed=4)
    ftp, e = rng.draw_thresholds(sc.n, 5, 0, sc.ftp_mu, sc.ftp_sigma)
    ref, _, _ = exact.exact(sc, ftp, e)
    errs = []
    for dt in (60.0, 15.0, 3.75):
        t = exact.stepped(sc, ftp, e, dt, ignite_mode=exact.IGNITE_INTERPOLATED)
        both = np.isfinite(ref) & np.isfinite(t)
        errs.append(np.abs(t[both] - ref[both]).mean())
    assert errs[0] > errs[1] > errs[2]
    assert errs[2] < 0.3 * errs[0]


def test_philox_known_answers():
    assert rng.philox4x32(0, 0, 0, 0, 0, 0) == (0x6627E8D5, 0xE169C58D, 0xBC57AC4C, 0x9B00DBD8)
    m = 0xFFFFFFFF
    assert rng.philox4x32(m, m, m, m, m, m) == (0x408F276D, 0x41C83B0E, 0xA20BC7C6, 0x6D5451FD)
    assert rng.philox4x32(0x243F6A88, 0x85A308D3, 0x13198A2E, 0x03707344, 0xA4093822, 0x299F31D0) == \
        (0xD16CFE09, 0x94FDCCEB, 0x5001E420, 0x24126EA1)


def test_thresholds_distribution():
    ftp, e = rng.draw_thresholds(20000, 42, 0, np.log(1e4), 0.4)
    z = (np.log(ftp) - np.log(1e4)) / 0.4
    assert abs(z.mean()) < 0.03 and abs(z.std() - 1) < 0.03
    assert abs(e.mean() - 1) < 0.03
    ftp2, _ = rng.draw_thresholds(20000, 42, 1, np.log(1e4), 0.4)
    assert not np.array_equal(ftp, ftp2)


def test_scenario_roundtrip(tmp_path):
    sc = random_scenario(n=50)
    graph = {"x_m": np.zeros(50), "y_m": np.zeros(50), "area_m2": np.ones(50), "height_m": np.ones(50)}
    ftp, e = rng.draw_thresholds(sc.n, 1, 0, sc.ftp_mu, sc.ftp_sigma)
    p = scenario.write(tmp_path / "s.ffes", sc, graph, ref_thresholds=(ftp, e), ref_seed=1, ref_replica=0)
    sc2, man, arr = scenario.read(p)
    assert man["n_edges"] == sc.n_edges and np.array_equal(sc2.src, sc.src)
    assert np.allclose(sc2.a, sc.a, rtol=1e-6) and man["lookahead_s"] == pytest.approx(300.0)
    assert np.allclose(arr["ftp"], ftp, rtol=1e-6)


def test_subset_simulation_on_known_tail():
    # score = first coordinate; P(z0 >= 3) = 1.35e-3.
    res = subset.subset_simulation(lambda z: z[0], dim=20, level=3.0, n=2000, p0=0.1,
                                   rng=np.random.default_rng(0))
    assert res.p == pytest.approx(1.35e-3, rel=0.35)
    assert res.n_evals < 20000

"""Verification and pilot experiments for RQ1-RQ3 (CPU reference).

Every experiment writes JSON to FFEData/zones/<zone>/sim/ and returns a dict.
`ffe sim report` gathers them into Documentation/Research/results/ExactFire_pilot.md.

The Unity GPU runs (RQ2 at scale, crude Monte Carlo ground truth for RQ3) are
local-PC steps; see Documentation/Research/LOCAL_ENGINE_PLAN.md.
"""

from __future__ import annotations

import json
import time
from pathlib import Path

import numpy as np

from .. import config, ffeg
from . import exact, model, rng, scenario, subset

INF = np.inf

# Named physics variants.
#   base      spreads readily
#   critical  near the percolation threshold, so large fires are rare (RQ3 regime)
#   hetero    incubation times vary between buildings (Local vs Global commit rule)
#   ftp_n2    flux-time-product exponent n = 2 (exercises the general-n closed form)
#   calibrated  fitted by `ffe sim calibrate` (results/calibration.json), if present
VARIANTS = {
    "base": {},
    "critical": {"e_flame_kw_m2": 30.0},
    "hetero": {"tau0_sigma": 0.5},
    "ftp_n2": {"ftp_n": 2.0, "ftp_median": 3.0e5},
}
CALIBRATION_FILE = config.REPO_ROOT / "Documentation" / "Research" / "results" / "calibration.json"


def variant_params(variant: str) -> model.Params:
    if variant == "calibrated":
        if not CALIBRATION_FILE.exists():
            raise FileNotFoundError(f"{CALIBRATION_FILE} missing: run `ffe sim calibrate` first")
        return model.Params(**json.loads(CALIBRATION_FILE.read_text())["params"])
    if variant not in VARIANTS:
        raise KeyError(f"unknown variant {variant}; known: {', '.join(VARIANTS)}, calibrated")
    return model.Params(**VARIANTS[variant])


def sim_dir(zone: str) -> Path:
    d = config.zone_dir(zone) / "sim"
    d.mkdir(parents=True, exist_ok=True)
    return d


def era5_wind(zone: str, hours: float = 6.0) -> tuple[float, float]:
    """Mean wind (speed m/s, from-direction deg) over the first `hours` after ignition, from ERA5."""
    from ..sources import era5
    from ..zones import get_zone
    z = get_zone(zone)
    path = config.zone_dir(zone) / "raw" / "era5_point.csv"
    if not path.exists():
        raise FileNotFoundError(f"{path} missing: run `ffe fetch {zone} --only weather`")
    t0 = (z.ignition_time_utc or z.event_time_utc).timestamp()
    rows = [r for r in era5.read_series(path)
            if 0 <= _ts(r["time_utc"]) - t0 < hours * 3600]
    if not rows:
        raise ValueError(f"no ERA5 hours after ignition for {zone}")
    u = float(np.mean([r["u10"] for r in rows]))
    v = float(np.mean([r["v10"] for r in rows]))
    speed = float(np.hypot(u, v))
    dir_from = float((np.degrees(np.arctan2(-u, -v)) + 360.0) % 360.0)
    return speed, dir_from


def _ts(iso: str) -> float:
    from datetime import datetime
    return datetime.fromisoformat(iso.replace("Z", "+00:00")).timestamp()


def resolve_ignitions(zone: str, arr: dict, manifest: dict, ignition: str = "center", layout_seed: int = 0):
    """ignition: 'center' | 'zone' (zones.yaml ignition_lonlat) | 'random:K' | 'LON,LAT'."""
    if ignition == "center":
        return [model.central_building(arr)]
    if ignition == "zone":
        from ..zones import get_zone
        ll = get_zone(zone).ignition_lonlat
        if not ll:
            raise ValueError(f"zone {zone} has no ignition_lonlat in zones.yaml; pass --ignition LON,LAT")
        return [model.nearest_building(arr, manifest, *ll)]
    if ignition.startswith("random:"):
        return list(model.random_buildings(len(arr["x_m"]), int(ignition.split(":")[1]), layout_seed + 1))
    lon, lat = (float(v) for v in ignition.split(","))
    return [model.nearest_building(arr, manifest, lon, lat)]


def load(zone: str, variant: str = "base", wind: float | str = 0.0, wind_dir: float = 180.0,
         t_end_h: float = 24.0, ignition: str = "center", layout_seed: int = 0):
    """Compile a scenario. wind may be a speed in m/s or 'era5' (mean of the first 6 h after ignition)."""
    graph_path = config.derived_dir(zone) / "graph.ffeg"
    if not graph_path.exists():
        raise FileNotFoundError(f"{graph_path} missing: run `ffe fetch {zone}` and `ffe build {zone}` first")
    man, arr = ffeg.read(graph_path)
    if wind == "era5":
        wind, wind_dir = era5_wind(zone)
    ign = resolve_ignitions(zone, arr, man, ignition, layout_seed)
    sc = model.compile_scenario(arr, variant_params(variant), wind_speed_ms=float(wind), wind_dir_from_deg=wind_dir,
                                ignitions=ign, t_end_s=t_end_h * 3600, layout_seed=layout_seed,
                                meta={"zone": zone, "variant": variant, "ignition": ignition})
    return sc, arr


def _save(zone: str, name: str, data: dict) -> dict:
    (sim_dir(zone) / f"{name}.json").write_text(json.dumps(data, indent=1))
    return data


def scenario_name(variant: str, wind, wind_dir: float, ignition: str = "center", t_end_h: float = 24.0) -> str:
    w = "era5" if wind == "era5" else f"U{float(wind):g}_D{wind_dir:g}"
    ig = "" if ignition == "center" else "_" + ignition.replace(":", "").replace(",", "_").replace(".", "p")
    th = "" if t_end_h == 24.0 else f"_T{t_end_h:g}h"
    return f"{variant}_{w}{ig}{th}"


def compile_to_file(zone: str, variant: str, wind, wind_dir: float, seed: int = 1, ignition: str = "center",
                    t_end_h: float = 24.0, stepped_refs=((60.0, "end_hazard"),)) -> Path:
    """Write <sim>/<name>.ffes plus Python reference results for the Unity parity checks:
    <name>.ref_tign.f64 (exact) and <name>.ref_stepped_dt<dt>_<variant>.f64 (time-stepped)."""
    sc, arr = load(zone, variant, wind, wind_dir, t_end_h=t_end_h, ignition=ignition)
    ftp, e = rng.draw_thresholds(sc.n, seed, 0, sc.ftp_mu, sc.ftp_sigma)
    name = scenario_name(variant, wind, wind_dir, ignition, t_end_h) + ".ffes"
    path = scenario.write(sim_dir(zone) / name, sc, arr, ref_thresholds=(ftp, e), ref_seed=seed, ref_replica=0)
    write_references(path, stepped_refs)
    return path


def write_references(path: Path, stepped_refs=((60.0, "end_hazard"),)) -> dict:
    """Python results for the float32 scenario in `path` (what Unity must reproduce)."""
    sc32, man, a32 = scenario.read(path)
    ftp, e = a32["ftp"].astype(float), a32["eth"].astype(float)
    t_ign, iters, _ = exact.exact(sc32, ftp, e, exact.LOCAL)
    # Raw little-endian float64 (inf = not ignited) so C# can read it without numpy.
    t_ign.astype("<f8").tofile(path.with_suffix(".ref_tign.f64"))
    info = {"scenario": path.name, "seed": man["ref_seed"], "replica": man["ref_replica"],
            "burned": int(np.isfinite(t_ign).sum()), "iterations": int(iters),
            "t_last_s": float(t_ign[np.isfinite(t_ign)].max()), "stepped": {}}
    for dt, name in stepped_refs:
        bm, im = STEPPED_VARIANTS[name]
        ts = exact.stepped(sc32, ftp, e, dt, bm, man["ref_seed"], man["ref_replica"], im)
        ts.astype("<f8").tofile(path.with_suffix(f".ref_stepped_dt{dt:g}_{name}.f64"))
        info["stepped"][f"dt{dt:g}_{name}"] = int(np.isfinite(ts).sum())
    path.with_suffix(".ref.json").write_text(json.dumps(info, indent=1))
    return info


def write_fixture(dest_dir: Path, n: int = 300, seed: int = 5, thr_seed: int = 9, replica: int = 3) -> Path:
    """Small synthetic scenario + Python reference result for the Unity EditMode tests."""
    sc, graph = model.synthetic_scenario(n=n, seed=seed, tau0_spread=0.3, extent_m=160.0)
    ftp, e = rng.draw_thresholds(sc.n, thr_seed, replica, sc.ftp_mu, sc.ftp_sigma)
    dest_dir.mkdir(parents=True, exist_ok=True)
    path = scenario.write(dest_dir / "small.ffes", sc, graph, ref_thresholds=(ftp, e),
                          ref_seed=thr_seed, ref_replica=replica)
    write_references(path, stepped_refs=((60.0, "end_hazard"), (60.0, "interp_hazard"), (60.0, "end_bernoulli")))
    return path


# ------------------------------------------------------------------ verification

def verify(zone: str, runs: int = 30, seed: int = 7) -> dict:
    """Commit rules give bit-identical ignition times; windows cut iterations."""
    rows = []
    for variant in VARIANTS:
        for wind in (0.0, 5.0, 10.0):
            sc, _ = load(zone, variant, wind)
            same, it = 0, []
            for r in range(runs):
                ftp, e = rng.draw_thresholds(sc.n, seed, r, sc.ftp_mu, sc.ftp_sigma)
                outs = [exact.exact(sc, ftp, e, m) for m in (exact.SEQUENTIAL, exact.GLOBAL, exact.LOCAL)]
                same += all(np.array_equal(outs[0][0], o[0]) for o in outs[1:])
                it.append([o[1] for o in outs])
            it = np.array(it, float)
            rows.append({"variant": variant, "wind": wind, "runs": runs, "bit_identical_runs": same,
                         "iters_sequential": it[:, 0].mean(), "iters_global": it[:, 1].mean(),
                         "iters_local": it[:, 2].mean()})
    return _save(zone, "verify", {"zone": zone, "rows": rows})


# ------------------------------------------------------------------ RQ1

STEPPED_VARIANTS = {
    "end_hazard": (exact.BRAND_HAZARD, exact.IGNITE_END_OF_STEP),        # common random numbers
    "interp_hazard": (exact.BRAND_HAZARD, exact.IGNITE_INTERPOLATED),
    "end_bernoulli": (exact.BRAND_BERNOULLI, exact.IGNITE_END_OF_STEP),  # usual practice
}


def _tail_chunk(args):
    zone, variant, wind, seed, r0, r1, dts = args
    sc, _ = load(zone, variant, wind)
    out = {"exact": np.empty(r1 - r0, dtype=np.int32)}
    for dt in dts:
        for name in ("end_hazard", "end_bernoulli"):
            out[f"dt{dt:g}_{name}"] = np.empty(r1 - r0, dtype=np.int32)
    for k, r in enumerate(range(r0, r1)):
        f, e = rng.draw_thresholds(sc.n, seed, r, sc.ftp_mu, sc.ftp_sigma)
        out["exact"][k] = np.isfinite(exact.exact(sc, f, e)[0]).sum()
        for dt in dts:
            for name in ("end_hazard", "end_bernoulli"):
                bm, im = STEPPED_VARIANTS[name]
                out[f"dt{dt:g}_{name}"][k] = np.isfinite(exact.stepped(sc, f, e, dt, bm, seed, r, im)).sum()
    return out


def tail_counts(zone: str, variant: str, wind, seed: int, runs: int, dts, procs: int = 1) -> dict:
    """Burned counts of `runs` independent runs for the exact solver and the stepped
    baselines (cached as .npz so a larger local run can be resumed or reused)."""
    path = sim_dir(zone) / f"tail_{variant}_W{wind}_S{seed}_N{runs}_dt{'-'.join(f'{d:g}' for d in dts)}.npz"
    if path.exists():
        return dict(np.load(path))
    step = -(-runs // max(procs * 8, 1))
    chunks = [(zone, variant, wind, seed, a, min(a + step, runs), tuple(dts)) for a in range(0, runs, step)]
    if procs > 1:
        from multiprocessing import get_context
        with get_context("fork").Pool(procs) as pool:
            parts = pool.map(_tail_chunk, chunks)
    else:
        parts = [_tail_chunk(c) for c in chunks]
    counts = {k: np.concatenate([p[k] for p in parts]) for k in parts[0]}
    np.savez_compressed(path, **counts)
    return counts


def rq1(zone: str, variant: str = "base", winds=(0.0, 5.0, 10.0), dts=(2.0, 10.0, 60.0, 300.0),
        runs: int = 40, tail_runs: int = 1000, tail_dts=(60.0, 300.0), seed: int = 21, procs: int = 1) -> dict:
    """How far are time-stepped results from the exact ones?"""
    out = {"zone": zone, "variant": variant, "runs": runs, "tail_runs": tail_runs, "rows": [], "tail": []}
    for wind in winds:
        sc, _ = load(zone, variant, wind)
        th = [rng.draw_thresholds(sc.n, seed, r, sc.ftp_mu, sc.ftp_sigma) for r in range(runs)]
        t0 = time.perf_counter()
        ref = [exact.exact(sc, f, e)[0] for f, e in th]
        t_exact = (time.perf_counter() - t0) / runs
        ref_burned = np.array([np.isfinite(t).sum() for t in ref])
        for dt in dts:
            for name, (bm, im) in STEPPED_VARIANTS.items():
                t0 = time.perf_counter()
                st = [exact.stepped(sc, f, e, dt, bm, seed, r, im) for r, (f, e) in enumerate(th)]
                t_step = (time.perf_counter() - t0) / runs
                err, rel, symd, burned = [], [], [], []
                for a_, b_ in zip(ref, st):
                    both = np.isfinite(a_) & np.isfinite(b_) & (a_ > 0)
                    err.append(np.mean(b_[both] - a_[both]) if both.any() else 0.0)
                    rel.append(np.mean((b_[both] - a_[both]) / a_[both]) if both.any() else 0.0)
                    symd.append(int((np.isfinite(a_) != np.isfinite(b_)).sum()))
                    burned.append(int(np.isfinite(b_).sum()))
                burned = np.array(burned)
                out["rows"].append({
                    "wind": wind, "dt": dt, "stepped": name,
                    "mean_arrival_error_s": float(np.mean(err)),
                    "mean_arrival_error_rel": float(np.mean(rel)),
                    "burned_exact_mean": float(ref_burned.mean()), "burned_stepped_mean": float(burned.mean()),
                    "burned_bias_rel": float((burned.mean() - ref_burned.mean()) / max(ref_burned.mean(), 1)),
                    "sym_diff_mean": float(np.mean(symd)),
                    "ms_per_run_exact": 1e3 * t_exact, "ms_per_run_stepped": 1e3 * t_step,
                })
        # Tail: distribution of burned counts, exact vs stepped with independent randomness.
        counts = tail_counts(zone, variant, wind, seed + 1, tail_runs, tail_dts, procs=procs)
        ex = counts["exact"]
        ks = sorted({int(np.percentile(ex, q)) for q in (90, 99, 99.9) if tail_runs * (1 - q / 100) >= 10})
        for dt in tail_dts:
            for name in ("end_hazard", "end_bernoulli"):
                sb = counts[f"dt{dt:g}_{name}"]
                for k in ks:
                    pe, ps = float((ex >= k).mean()), float((sb >= k).mean())
                    out["tail"].append({"wind": wind, "dt": dt, "stepped": name, "K": k,
                                        "p_exact": pe, "p_stepped": ps,
                                        "se_exact": float(np.sqrt(pe * (1 - pe) / tail_runs)),
                                        "ratio": ps / pe if pe > 0 else None})
    return _save(zone, f"rq1_{variant}", out)


# ------------------------------------------------------------------ RQ2 (CPU pilot)

def rq2(zone: str, variant: str = "base", wind: float = 5.0, runs: int = 30, seed: int = 31,
        dts=(1.0, 5.0, 30.0), target_err_s: float = 60.0) -> dict:
    """CPU cost of exact vs time-stepped runs, and the dt needed for a given accuracy."""
    sc, _ = load(zone, variant, wind)
    th = [rng.draw_thresholds(sc.n, seed, r, sc.ftp_mu, sc.ftp_sigma) for r in range(runs)]
    exact.exact(sc, *th[0])                                  # compile outside the timing
    rows = []
    for mode, name in ((exact.SEQUENTIAL, "sequential"), (exact.GLOBAL, "global"), (exact.LOCAL, "local")):
        t0 = time.perf_counter()
        res = [exact.exact(sc, f, e, mode) for f, e in th]
        dtm = (time.perf_counter() - t0) / runs
        rows.append({"solver": f"exact_{name}", "ms_per_run": 1e3 * dtm,
                     "iterations": float(np.mean([r[1] for r in res])),
                     "predictions": float(np.mean([r[2] for r in res])),
                     "events": float(np.mean([np.isfinite(r[0]).sum() for r in res])), "arrival_err_s": 0.0})
    ref = [exact.exact(sc, f, e)[0] for f, e in th]
    for dt in dts:
        exact.stepped(sc, *th[0], dt)
        t0 = time.perf_counter()
        st = [exact.stepped(sc, f, e, dt, exact.BRAND_HAZARD, seed, r, exact.IGNITE_INTERPOLATED)
              for r, (f, e) in enumerate(th)]
        dtm = (time.perf_counter() - t0) / runs
        errs = []
        for a_, b_ in zip(ref, st):
            both = np.isfinite(a_) & np.isfinite(b_)
            errs.append(np.abs(b_[both] - a_[both]).mean())
        rows.append({"solver": f"stepped_interp_dt{dt:g}", "ms_per_run": 1e3 * dtm,
                     "iterations": float(np.ceil(sc.t_end / dt)), "arrival_err_s": float(np.mean(errs))})
    return _save(zone, f"rq2_{variant}_U{wind:g}", {"zone": zone, "variant": variant, "wind": wind,
                                                     "n_nodes": sc.n, "n_edges": sc.n_edges,
                                                     "runs": runs, "target_err_s": target_err_s, "rows": rows})


# ------------------------------------------------------------------ RQ3 (CPU pilot)

def rq3(zone: str, variant: str = "critical", wind: float = 0.0, level: int | None = None,
        crude_runs: int = 20000, sus_repeats: int = 10, sus_n: int = 1000, seed: int = 41) -> dict:
    """Subset simulation vs crude Monte Carlo for P(burned >= level)."""
    sc, _ = load(zone, variant, wind)
    t0 = time.perf_counter()
    counts = np.empty(crude_runs, dtype=np.int64)
    for r in range(crude_runs):
        f, e = rng.draw_thresholds(sc.n, seed, r, sc.ftp_mu, sc.ftp_sigma)
        counts[r] = np.isfinite(exact.exact(sc, f, e)[0]).sum()
    t_crude = time.perf_counter() - t0
    if level is None:
        level = int(np.percentile(counts, 99.8))
    p_crude = float((counts >= level).mean())
    se_crude = float(np.sqrt(p_crude * (1 - p_crude) / crude_runs))
    ests, evals = [], []
    t0 = time.perf_counter()
    for k in range(sus_repeats):
        res = subset.subset_simulation(lambda z: subset.score(sc, z), 2 * sc.n, float(level), n=sus_n,
                                       rng=np.random.default_rng(seed + 1000 + k))
        ests.append(res.p)
        evals.append(res.n_evals)
    t_sus = time.perf_counter() - t0
    ests = np.array(ests)
    mean_evals = float(np.mean(evals))
    cov_sus = float(ests.std(ddof=1) / ests.mean()) if ests.mean() > 0 else None
    cov_crude_same_budget = float(np.sqrt((1 - p_crude) / (p_crude * mean_evals))) if p_crude > 0 else None
    gain = (cov_crude_same_budget / cov_sus) ** 2 if cov_sus and cov_crude_same_budget else None
    return _save(zone, f"rq3_{variant}_U{wind:g}", {
        "zone": zone, "variant": variant, "wind": wind, "level": level,
        "crude": {"runs": crude_runs, "p": p_crude, "se": se_crude, "seconds": t_crude,
                  "burned_percentiles": {q: float(np.percentile(counts, q)) for q in (50, 90, 99, 99.9)}},
        "subset": {"repeats": sus_repeats, "n_per_level": sus_n, "estimates": ests.tolist(),
                   "mean": float(ests.mean()), "cov": cov_sus, "mean_evals": mean_evals, "seconds": t_sus},
        "cov_crude_at_same_budget": cov_crude_same_budget, "efficiency_gain": gain,
    })


def _crude_chunk(args):
    zone, variant, wind, seed, r0, r1 = args
    sc, _ = load(zone, variant, wind)
    out = np.empty(r1 - r0, dtype=np.int32)
    for k, r in enumerate(range(r0, r1)):
        f, e = rng.draw_thresholds(sc.n, seed, r, sc.ftp_mu, sc.ftp_sigma)
        out[k] = np.isfinite(exact.exact(sc, f, e)[0]).sum()
    return out


def _sus_one(args):
    zone, variant, wind, level, n, beta, rep_seed = args
    sc, _ = load(zone, variant, wind)
    res = subset.subset_simulation(lambda z: subset.score(sc, z), 2 * sc.n, float(level), n=n, beta=beta,
                                   rng=np.random.default_rng(rep_seed))
    return res.p, res.n_evals, res.accept_rates


def rq3_deep(zone: str, variant: str = "critical", wind: float = 0.0, crude_runs: int = 1_000_000,
             targets=(1e-3, 1e-4, 1e-5), sus_repeats: int = 20, sus_n: int = 1000, betas=(0.3, 0.6),
             procs: int = 4, seed: int = 41) -> dict:
    """Crude Monte Carlo ground truth (multi-process) and subset simulation at several tail levels."""
    from multiprocessing import get_context
    counts_path = sim_dir(zone) / f"crude_{variant}_U{wind:g}_S{seed}_N{crude_runs}.npy"
    t0 = time.perf_counter()
    if counts_path.exists():
        counts = np.load(counts_path)
    else:
        step = -(-crude_runs // (procs * 8))
        chunks = [(zone, variant, wind, seed, a, min(a + step, crude_runs)) for a in range(0, crude_runs, step)]
        with get_context("fork").Pool(procs) as pool:
            counts = np.concatenate(pool.map(_crude_chunk, chunks))
        np.save(counts_path, counts)
    t_crude = time.perf_counter() - t0
    levels = []
    for p_target in targets:
        k = int(np.quantile(counts, 1 - p_target, method="higher"))
        p_hat = float((counts >= k).mean())
        levels.append({"target": p_target, "level": k, "p_crude": p_hat,
                       "se_crude": float(np.sqrt(p_hat * (1 - p_hat) / crude_runs)),
                       "crude_hits": int((counts >= k).sum())})
    rows = []
    for lv in levels:
        for beta in betas:
            jobs = [(zone, variant, wind, lv["level"], sus_n, beta, seed + 7919 * (i + 1)) for i in range(sus_repeats)]
            t1 = time.perf_counter()
            with get_context("fork").Pool(procs) as pool:
                out = pool.map(_sus_one, jobs)
            ests = np.array([o[0] for o in out])
            evals = float(np.mean([o[1] for o in out]))
            acc = float(np.mean([np.mean(o[2]) if o[2] else np.nan for o in out]))
            p_ref = lv["p_crude"]
            mse = float(np.mean((ests - p_ref) ** 2))
            var_crude_same = p_ref * (1 - p_ref) / evals
            rows.append({**lv, "beta": beta, "sus_mean": float(ests.mean()), "sus_median": float(np.median(ests)),
                         "sus_cov": float(ests.std(ddof=1) / ests.mean()) if ests.mean() > 0 else None,
                         "sus_rel_bias": float(ests.mean() / p_ref - 1) if p_ref > 0 else None,
                         "sus_evals": evals, "accept_rate": acc,
                         "efficiency_gain_mse": float(var_crude_same / mse) if mse > 0 else None,
                         "seconds": time.perf_counter() - t1, "estimates": ests.tolist()})
    return _save(zone, f"rq3deep_{variant}_U{wind:g}", {
        "zone": zone, "variant": variant, "wind": wind, "crude_runs": crude_runs, "crude_seconds": t_crude,
        "burned_percentiles": {str(q): float(np.percentile(counts, q)) for q in (50, 90, 99, 99.9, 99.99, 99.999)},
        "rows": rows})


# ------------------------------------------------------------------ report

def unity_paired(zone: str, run_dir: Path | None = None) -> dict:
    """RQ1 at GPU scale: pair Unity exact and stepped runs that share scenario, seed,
    replicas and batches (Benchmark with -savetimes 1), and compare them building by
    building (same thresholds, so the difference is pure discretisation error)."""
    run_dir = run_dir or (sim_dir(zone) / "unity_runs")
    metas = []
    for f in sorted(run_dir.glob("*.json")):
        v = json.loads(f.read_text())
        if v.get("times_saved"):
            metas.append((f.with_suffix(""), v))
    rows = []
    for stem_e, e in metas:
        if e.get("solver") != "exact":
            continue
        key = (e["scenario"], e["replicas_per_batch"], e["batches"], e["seed"])
        n = e["n_nodes"]
        ex = np.concatenate([np.fromfile(f"{stem_e}_times_b{b}.f32", "<f4") for b in range(e["batches"])]).reshape(-1, n)
        ex_b = np.isfinite(ex).sum(1)
        for stem_s, st in metas:
            if st.get("solver") != "stepped" or (st["scenario"], st["replicas_per_batch"], st["batches"], st["seed"]) != key:
                continue
            ts = np.concatenate([np.fromfile(f"{stem_s}_times_b{b}.f32", "<f4") for b in range(st["batches"])]).reshape(-1, n)
            both = np.isfinite(ex) & np.isfinite(ts) & (ex > 0)
            diff = np.where(both, ts, 0.0) - np.where(both, ex, 0.0)
            rel = (diff / np.where(both, ex, 1.0)).sum(1) / np.maximum(both.sum(1), 1)
            err = diff.sum(1) / np.maximum(both.sum(1), 1)
            st_b = np.isfinite(ts).sum(1)
            tails = []
            for q in (90, 99, 99.9):
                if len(ex_b) * (1 - q / 100) < 10:
                    continue
                k = int(np.percentile(ex_b, q))
                pe, ps = float((ex_b >= k).mean()), float((st_b >= k).mean())
                tails.append({"q": q, "K": k, "p_exact": pe, "p_stepped": ps, "ratio": ps / pe if pe else None})
            rows.append({"scenario": e["scenario"], "backend": e["backend"], "stepped": st["rule"], "dt_s": st["dt_s"],
                         "runs": int(len(ex_b)), "mean_arrival_error_s": float(err.mean()),
                         "mean_arrival_error_rel": float(rel.mean()),
                         "burned_exact_mean": float(ex_b.mean()), "burned_stepped_mean": float(st_b.mean()),
                         "exact_runs_per_s": e["runs_per_second"], "stepped_runs_per_s": st["runs_per_second"],
                         "tails": tails})
    return _save(zone, "unity_paired", {"zone": zone, "rows": rows})


def _unity_section(zones) -> list[str]:
    """Summaries of Unity Benchmark runs (FireGraphBatch.Benchmark JSON + CSV), if any exist."""
    files = []
    for zone in zones:
        files += sorted((sim_dir(zone) / "unity_runs").glob("*.json"))
    files += sorted((config.REPO_ROOT / "Documentation" / "Research" / "results" / "unity").glob("*.json"))
    paired = []
    for zone in zones:
        f = sim_dir(zone) / "unity_paired.json"
        if f.exists():
            paired += json.loads(f.read_text())["rows"]
    if not files and not paired:
        return []
    out = []
    if paired:
        out += ["## Unity paired runs: exact vs time-stepped with identical thresholds (local PC)", "",
                "| scenario | backend | stepped | runs | mean arrival error s (rel.) | burned exact → stepped | runs/s exact / stepped | tail ratio at p90 / p99 / p99.9 |",
                "|---|---|---|---|---|---|---|---|"]
        for r in paired:
            tails = " / ".join("n/a" if t["ratio"] is None else f"{t['ratio']:.2f}" for t in r["tails"])
            out.append(f"| {r['scenario']} | {r['backend']} | {r['stepped']} | {r['runs']} | {r['mean_arrival_error_s']:.0f} "
                       f"({100 * r['mean_arrival_error_rel']:.1f}%) | {r['burned_exact_mean']:.0f} → {r['burned_stepped_mean']:.0f} | "
                       f"{r['exact_runs_per_s']} / {r['stepped_runs_per_s']} | {tails} |")
        out.append("")
    if not files:
        return out
    lines = out + ["## Unity engine runs (local PC)", "",
             "| scenario | backend | rule | device | N | replicas × batches | runs/s | burned mean | P(burned ≥ K) at K = p99 / p99.9 |",
             "|---|---|---|---|---|---|---|---|---|"]
    for f in files:
        v = json.loads(f.read_text())
        csv = f.with_suffix(".csv")
        burned_txt, tail_txt = "n/a", "n/a"
        if csv.exists():
            b = np.loadtxt(csv, delimiter=",", skiprows=1, usecols=1, ndmin=1)
            if b.size:
                k99, k999 = np.percentile(b, 99), np.percentile(b, 99.9)
                burned_txt = f"{b.mean():.0f}"
                tail_txt = f"{(b >= k99).mean():.2e} / {(b >= k999).mean():.2e}"
        lines.append(f"| {v['scenario']} | {v['backend']} | {v['rule']} | {v['device']} | {v['n_nodes']} | "
                     f"{v['replicas_per_batch']} × {v['batches']} | {v['runs_per_second']} | {burned_txt} | {tail_txt} |")
    return lines + [""]


def report(zones, out_path: Path) -> Path:
    lines = ["# Exact fire spread: pilot results (CPU reference)", "",
             "Generated by `python -m ffe sim report`. Physics model M0 with literature-range defaults",
             "(not calibrated). Ignition: the building nearest the zone centre. Horizon 24 h.", ""]
    for zone in zones:
        d = sim_dir(zone)
        lines += [f"## {zone}", ""]
        f = d / "verify.json"
        if f.exists():
            v = json.loads(f.read_text())
            lines += ["### Verification: commit rules", "",
                      "| variant | wind m/s | bit-identical runs | iterations seq / global / local |",
                      "|---|---|---|---|"]
            for r in v["rows"]:
                lines.append(f"| {r['variant']} | {r['wind']:g} | {r['bit_identical_runs']}/{r['runs']} | "
                             f"{r['iters_sequential']:.0f} / {r['iters_global']:.0f} / {r['iters_local']:.0f} |")
            lines.append("")
        for f in sorted(d.glob("rq1_*.json")):
            v = json.loads(f.read_text())
            lines += [f"### RQ1 time-step bias ({v['variant']}, {v['runs']} paired runs)", "",
                      "| wind | dt s | stepped variant | mean arrival error s | relative | burned exact → stepped | sym. diff | ms/run exact / stepped |",
                      "|---|---|---|---|---|---|---|---|"]
            for r in v["rows"]:
                lines.append(f"| {r['wind']:g} | {r['dt']:g} | {r['stepped']} | {r['mean_arrival_error_s']:.0f} | "
                             f"{100 * r['mean_arrival_error_rel']:.1f}% | {r['burned_exact_mean']:.0f} → {r['burned_stepped_mean']:.0f} | "
                             f"{r['sym_diff_mean']:.1f} | {r['ms_per_run_exact']:.1f} / {r['ms_per_run_stepped']:.1f} |")
            lines += ["", f"Tail probabilities ({v['tail_runs']} independent runs each):", "",
                      "| wind | dt s | stepped variant | K | P exact (± se) | P stepped | ratio |", "|---|---|---|---|---|---|---|"]
            for r in v["tail"]:
                ratio = "n/a" if r["ratio"] is None else f"{r['ratio']:.2f}"
                se = f" ± {r['se_exact']:.1e}" if "se_exact" in r else ""
                lines.append(f"| {r['wind']:g} | {r['dt']:g} | {r['stepped']} | {r['K']} | {r['p_exact']:.2e}{se} | {r['p_stepped']:.2e} | {ratio} |")
            lines.append("")
        for f in sorted(d.glob("rq2_*.json")):
            v = json.loads(f.read_text())
            lines += [f"### RQ2 CPU cost ({v['variant']}, wind {v['wind']:g} m/s, {v['n_nodes']} buildings, {v['n_edges']} edges)", "",
                      "| solver | ms/run | iterations | mean arrival error s |", "|---|---|---|---|"]
            for r in v["rows"]:
                lines.append(f"| {r['solver']} | {r['ms_per_run']:.1f} | {r['iterations']:.0f} | {r['arrival_err_s']:.1f} |")
            lines.append("")
        for f in sorted(d.glob("rq3_*.json")):
            v = json.loads(f.read_text())
            c, s = v["crude"], v["subset"]
            gain = "n/a" if v["efficiency_gain"] is None else f"{v['efficiency_gain']:.1f}×"
            lines += [f"### RQ3 rare events ({v['variant']}, wind {v['wind']:g} m/s, P(burned ≥ {v['level']}))", "",
                      f"* Crude Monte Carlo: {c['runs']} runs → p = {c['p']:.2e} ± {c['se']:.1e} ({c['seconds']:.0f} s). "
                      f"Burned percentiles 50/90/99/99.9: {', '.join(f'{x:.0f}' for x in c['burned_percentiles'].values())}.",
                      f"* Subset simulation: {s['repeats']} repeats × ~{s['mean_evals']:.0f} runs → mean {s['mean']:.2e}, "
                      f"CoV {s['cov']:.2f} ({s['seconds']:.0f} s total).",
                      f"* Crude MC CoV with the same budget: {v['cov_crude_at_same_budget']:.2f}; efficiency gain {gain}.", ""]
        for f in sorted(d.glob("rq3deep_*.json")):
            v = json.loads(f.read_text())
            lines += [f"### RQ3 deep: subset simulation vs {v['crude_runs']:,} crude runs ({v['variant']}, wind {v['wind']:g} m/s)", "",
                      "Burned-count percentiles 50/90/99/99.9/99.99/99.999: "
                      + ", ".join(f"{x:.0f}" for x in v["burned_percentiles"].values()) + ".", "",
                      "| target p | level K | p crude (hits) | beta | SuS mean | rel. bias | CoV | runs/estimate | accept | efficiency vs crude (MSE) |",
                      "|---|---|---|---|---|---|---|---|---|---|"]
            for r in v["rows"]:
                eff = "n/a" if r["efficiency_gain_mse"] is None else f"{r['efficiency_gain_mse']:.1f}×"
                cov = "n/a" if r["sus_cov"] is None else f"{r['sus_cov']:.2f}"
                bias = "n/a" if r["sus_rel_bias"] is None else f"{100 * r['sus_rel_bias']:+.0f}%"
                lines.append(f"| {r['target']:.0e} | {r['level']} | {r['p_crude']:.2e} ({r['crude_hits']}) | {r['beta']} | "
                             f"{r['sus_mean']:.2e} | {bias} | {cov} | {r['sus_evals']:.0f} | {r['accept_rate']:.2f} | {eff} |")
            lines.append("")
    lines += _unity_section(zones)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text("\n".join(lines))
    return out_path

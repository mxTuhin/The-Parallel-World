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

# Named physics variants. "base" spreads readily; "critical" sits near the
# percolation threshold so that large fires are rare (the RQ3 regime).
VARIANTS = {
    "base": {},
    "critical": {"e_flame_kw_m2": 30.0},
}


def sim_dir(zone: str) -> Path:
    d = config.zone_dir(zone) / "sim"
    d.mkdir(parents=True, exist_ok=True)
    return d


def load(zone: str, variant: str = "base", wind: float = 0.0, wind_dir: float = 180.0,
         t_end_h: float = 24.0):
    graph_path = config.derived_dir(zone) / "graph.ffeg"
    if not graph_path.exists():
        raise FileNotFoundError(f"{graph_path} missing: run `ffe fetch {zone}` and `ffe build {zone}` first")
    _, arr = ffeg.read(graph_path)
    params = model.Params(**VARIANTS[variant])
    sc = model.compile_scenario(arr, params, wind_speed_ms=wind, wind_dir_from_deg=wind_dir,
                                ignitions=[model.central_building(arr)], t_end_s=t_end_h * 3600,
                                meta={"zone": zone, "variant": variant})
    return sc, arr


def _save(zone: str, name: str, data: dict) -> dict:
    (sim_dir(zone) / f"{name}.json").write_text(json.dumps(data, indent=1))
    return data


def compile_to_file(zone: str, variant: str, wind: float, wind_dir: float, seed: int = 1) -> Path:
    sc, arr = load(zone, variant, wind, wind_dir)
    ftp, e = rng.draw_thresholds(sc.n, seed, 0, sc.ftp_mu, sc.ftp_sigma)
    name = f"{variant}_U{wind:g}_D{wind_dir:g}.ffes"
    path = scenario.write(sim_dir(zone) / name, sc, arr, ref_thresholds=(ftp, e), ref_seed=seed, ref_replica=0)
    # Reference result of the float32 scenario for the Unity parity test.
    sc32, _, a32 = scenario.read(path)
    t_ign, iters, _ = exact.exact(sc32, a32["ftp"].astype(float), a32["eth"].astype(float), exact.LOCAL)
    # Raw little-endian float64 (inf = not ignited) so C# can read it without numpy.
    t_ign.astype("<f8").tofile(path.with_suffix(".ref_tign.f64"))
    (path.with_suffix(".ref.json")).write_text(json.dumps({
        "scenario": name, "seed": seed, "replica": 0, "burned": int(np.isfinite(t_ign).sum()),
        "iterations": int(iters), "t_last_s": float(t_ign[np.isfinite(t_ign)].max()),
        "t_ign_first20": [None if not np.isfinite(v) else float(v) for v in t_ign[:20]]}, indent=1))
    return path


def write_fixture(dest_dir: Path, n: int = 300, seed: int = 5, thr_seed: int = 9, replica: int = 3) -> Path:
    """Small synthetic scenario + Python reference result for the Unity EditMode tests."""
    sc, graph = model.synthetic_scenario(n=n, seed=seed, tau0_spread=0.3, extent_m=160.0)
    ftp, e = rng.draw_thresholds(sc.n, thr_seed, replica, sc.ftp_mu, sc.ftp_sigma)
    dest_dir.mkdir(parents=True, exist_ok=True)
    path = scenario.write(dest_dir / "small.ffes", sc, graph, ref_thresholds=(ftp, e),
                          ref_seed=thr_seed, ref_replica=replica)
    sc32, _, a32 = scenario.read(path)
    t_ign, _, _ = exact.exact(sc32, a32["ftp"].astype(float), a32["eth"].astype(float), exact.LOCAL)
    t_ign.astype("<f8").tofile(path.with_suffix(".ref_tign.f64"))
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


def rq1(zone: str, variant: str = "base", winds=(0.0, 5.0, 10.0), dts=(2.0, 10.0, 60.0, 300.0),
        runs: int = 40, tail_runs: int = 1000, tail_dts=(60.0, 300.0), seed: int = 21) -> dict:
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
        th = [rng.draw_thresholds(sc.n, seed + 1, r, sc.ftp_mu, sc.ftp_sigma) for r in range(tail_runs)]
        ex = np.array([np.isfinite(exact.exact(sc, f, e)[0]).sum() for f, e in th])
        ks = sorted({int(np.percentile(ex, q)) for q in (90, 99)})
        for dt in tail_dts:
            for name in ("end_hazard", "end_bernoulli"):
                bm, im = STEPPED_VARIANTS[name]
                sb = np.array([np.isfinite(exact.stepped(sc, f, e, dt, bm, seed + 1, r, im)).sum()
                               for r, (f, e) in enumerate(th)])
                for k in ks:
                    pe, ps = float((ex >= k).mean()), float((sb >= k).mean())
                    out["tail"].append({"wind": wind, "dt": dt, "stepped": name, "K": k,
                                        "p_exact": pe, "p_stepped": ps,
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


# ------------------------------------------------------------------ report

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
                      "| wind | dt s | stepped variant | K | P exact | P stepped | ratio |", "|---|---|---|---|---|---|---|"]
            for r in v["tail"]:
                ratio = "n/a" if r["ratio"] is None else f"{r['ratio']:.2f}"
                lines.append(f"| {r['wind']:g} | {r['dt']:g} | {r['stepped']} | {r['K']} | {r['p_exact']:.3f} | {r['p_stepped']:.3f} | {ratio} |")
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
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text("\n".join(lines))
    return out_path

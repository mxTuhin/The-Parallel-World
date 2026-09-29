"""E5: physics sanity for model M0.

1. `fit_fullscale(csv)` fits the radiation parameters (flame emissive power and
   FTP median, optionally the FTP spread) to full-scale building-to-building
   ignition tests: one burning source facing one target across a known gap, with
   or without wind. Each test is solved with the same exact node clock as the town
   simulations (a two-building scenario), so the fitted values plug straight into
   `--variant calibrated`.

   Input: a CSV with the columns of FULLSCALE_TEMPLATE (fill it from the test
   report, e.g. the 23 tests of the 2025 Fire Technology wind-driven
   building-to-building study). Firebrands are switched off in these tests.

   Likelihood per test:
     P(ignite) = Phi((ln D_total - ln FTP_median) / sigma)
     where D_total = total flux-time dose the target receives over the source's
     whole burning curve. If a time to ignition is reported, a log-normal time
     error term (sd `time_sd_log`) is added using the time at which the dose
     reaches the median FTP.

2. `compare_real_fire(zone, ...)` runs the town simulation from the real origin
   building with the real (or ERA5) wind and compares the per-building burn
   probability with the observed burned area: burned count, Brier score, ROC
   AUC, and F1/Jaccard of the P >= 0.5 set.
"""

from __future__ import annotations

import csv
import json
from dataclasses import replace
from pathlib import Path

import numpy as np
from scipy.optimize import minimize
from scipy.special import log_ndtr

from .. import config, ffeg
from . import exact, model, rng

FULLSCALE_TEMPLATE = config.REPO_ROOT / "Documentation" / "Research" / "data" / "fullscale_tests_template.csv"
FULLSCALE_COLUMNS = [
    "test_id",                 # label from the report
    "separation_m",            # gap between source facade and target facade
    "wind_ms",                 # wind speed (0 if none)
    "wind_toward_target",      # 1 if the wind blows from source to target, -1 opposite, 0 across or none
    "source_width_m",          # width of the burning facade facing the target
    "source_height_m",         # height of the burning facade (or flame height if reported)
    "source_full_duration_s",  # duration of full involvement; blank = model default for 100 m2
    "target_ignited",          # 1 or 0
    "time_to_ignition_s",      # blank if not ignited or not reported
    "notes",
]


def write_template(path: Path = FULLSCALE_TEMPLATE) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        with open(path, "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(FULLSCALE_COLUMNS)
            w.writerow(["EXAMPLE-delete-me", 3.0, 0, 0, 6.0, 5.0, "", 1, 780, "illustrative row only"])
    return path


def read_tests(path: Path) -> list[dict]:
    rows = []
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            if not r.get("test_id") or r["test_id"].startswith("EXAMPLE"):
                continue

            def num(k, default=None):
                v = (r.get(k) or "").strip()
                return float(v) if v else default
            rows.append({
                "test_id": r["test_id"], "gap": num("separation_m"), "wind": num("wind_ms", 0.0),
                "toward": num("wind_toward_target", 0.0), "width": num("source_width_m"),
                "height": num("source_height_m"), "td": num("source_full_duration_s"),
                "ignited": int(num("target_ignited", 0)), "t_ign": num("time_to_ignition_s"),
            })
    if not rows:
        raise ValueError(f"{path}: no test rows (fill in the template first)")
    return rows


def _two_building(p: model.Params, test: dict) -> model.Scenario:
    """Source 0 burning from t = 0 (no incubation), target 1 across the gap."""
    cos_t = float(np.sign(test["toward"])) if test["wind"] > 0 else 0.0
    a = float(model.radiation_coefficient(p, test["gap"], test["width"], test["height"] / p.flame_height_factor,
                                          test["wind"], cos_t))
    td = test["td"] if test["td"] else float(model.full_involvement_s(p, 100.0))
    return model.Scenario(
        n=2, in_offsets=np.array([0, 0, 1]), src=np.array([0]), a=np.array([a]), b=np.array([0.0]),
        tau0=np.array([0.0, p.tau0_s]), tg=np.array([p.tg_s, p.tg_s]), td=np.array([td, td]),
        tx=np.array([p.tx_s, p.tx_s]), q_cr=p.q_cr_kw_m2, ftp_n=p.ftp_n, ftp_mu=float(np.log(p.ftp_median)),
        ftp_sigma=p.ftp_sigma, ignitions=np.array([0]), t_end=1e7)


def predict_test(p: model.Params, test: dict) -> dict:
    sc = _two_building(p, test)
    # Total dose over the source's whole burning curve: the progress fraction against a
    # huge threshold, scaled back (exact, since both are computed in float64).
    big = 1e300
    dose = exact.progress(sc, np.array([0.0, np.inf]), np.array([big, big]), np.array([np.inf, np.inf])) * big
    t_med, _, _ = exact.exact(sc, np.array([p.ftp_median, p.ftp_median]), np.array([np.inf, np.inf]))
    if dose <= 0:
        log_p = -np.inf
    else:
        log_p = float(log_ndtr((np.log(dose) - np.log(p.ftp_median)) / p.ftp_sigma))
    return {"a_kw_m2": float(sc.a[0]), "dose_total": float(dose), "p_ignite": float(np.exp(log_p)),
            "log_p": log_p, "t_ignite_median_s": float(t_med[1])}


def _neg_loglik(theta, base: model.Params, tests, fit_sigma: bool, time_sd_log: float):
    e_flame, ftp_med = np.exp(theta[0]), np.exp(theta[1])
    sigma = np.exp(theta[2]) if fit_sigma else base.ftp_sigma
    p = replace(base, e_flame_kw_m2=float(e_flame), ftp_median=float(ftp_med), ftp_sigma=float(sigma))
    nll = 0.0
    for t in tests:
        pr = predict_test(p, t)
        if t["ignited"]:
            nll -= max(pr["log_p"], -50.0)
            if t["t_ign"] and np.isfinite(pr["t_ignite_median_s"]):
                nll += 0.5 * ((np.log(pr["t_ignite_median_s"]) - np.log(t["t_ign"])) / time_sd_log) ** 2
        else:
            nll -= float(np.log1p(-min(pr["p_ignite"], 1 - 1e-12)))
    return nll


def fit_fullscale(csv_path: Path, base: model.Params | None = None, fit_sigma: bool = False,
                  time_sd_log: float = 0.5, out_path: Path | None = None) -> dict:
    base = base or model.Params()
    tests = read_tests(csv_path)
    # Coarse grid, then Nelder-Mead from the best grid point.
    best = None
    for e in np.linspace(np.log(15), np.log(150), 10):
        for f in np.linspace(np.log(2e3), np.log(5e4), 10):
            th = [e, f] + ([np.log(base.ftp_sigma)] if fit_sigma else [])
            v = _neg_loglik(th, base, tests, fit_sigma, time_sd_log)
            if best is None or v < best[0]:
                best = (v, th)
    res = minimize(_neg_loglik, best[1], args=(base, tests, fit_sigma, time_sd_log), method="Nelder-Mead",
                   options={"xatol": 1e-4, "fatol": 1e-6, "maxiter": 2000})
    th = res.x
    fitted = replace(base, e_flame_kw_m2=float(np.exp(th[0])), ftp_median=float(np.exp(th[1])),
                     ftp_sigma=float(np.exp(th[2])) if fit_sigma else base.ftp_sigma)
    rows = []
    for t in tests:
        pr = predict_test(fitted, t)
        rows.append({**t, **{k: v for k, v in pr.items() if k != "log_p"}})
    ign = np.array([t["ignited"] for t in tests])
    pp = np.array([r["p_ignite"] for r in rows])
    out = {
        "source": str(csv_path), "n_tests": len(tests), "fit_sigma": fit_sigma,
        "neg_loglik": float(res.fun), "converged": bool(res.success),
        "params": fitted.to_dict(),
        "brier": float(np.mean((pp - ign) ** 2)),
        "accuracy_at_0.5": float(np.mean((pp >= 0.5) == (ign == 1))),
        "tests": rows,
    }
    out_path = out_path or (config.REPO_ROOT / "Documentation" / "Research" / "results" / "calibration.json")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(out, indent=1, default=float))
    return out


# ------------------------------------------------------------------ real fire comparison

def _auc(score: np.ndarray, label: np.ndarray) -> float | None:
    pos, neg = score[label == 1], score[label == 0]
    if len(pos) == 0 or len(neg) == 0:
        return None
    order = np.argsort(np.concatenate([pos, neg]), kind="mergesort")
    ranks = np.empty(len(order))
    ranks[order] = np.arange(1, len(order) + 1)
    # average ranks for ties
    allv = np.concatenate([pos, neg])
    for v in np.unique(allv):
        m = allv == v
        if m.sum() > 1:
            ranks[m] = ranks[m].mean()
    return float((ranks[:len(pos)].sum() - len(pos) * (len(pos) + 1) / 2) / (len(pos) * len(neg)))


def compare_real_fire(zone: str, variant: str = "base", wind="era5", wind_dir: float = 180.0,
                      ignition: str = "zone", runs: int = 200, t_end_h: float = 30.0, seed: int = 51) -> dict:
    """Simulated burn probability per building vs the observed burned area of a real fire."""
    from .experiments import _save, load
    sc, arr = load(zone, variant, wind, wind_dir, t_end_h=t_end_h, ignition=ignition)
    if "label" not in arr:
        raise ValueError("graph has no labels: add local_inputs/burned_area.geojson and rebuild")
    lab = arr["label"].astype(int)
    known = lab >= 0
    obs = (lab == 4).astype(int)
    if known.sum() == 0 or obs.sum() == 0:
        raise ValueError("no observed burned buildings in the labels")
    burned_counts, hits = [], np.zeros(sc.n)
    for r in range(runs):
        f, e = rng.draw_thresholds(sc.n, seed, r, sc.ftp_mu, sc.ftp_sigma)
        t = exact.exact(sc, f, e)[0]
        b = np.isfinite(t)
        hits += b
        burned_counts.append(int(b.sum()))
    prob = hits / runs
    pk, ok = prob[known], obs[known]
    pred = pk >= 0.5
    tp = int((pred & (ok == 1)).sum())
    fp = int((pred & (ok == 0)).sum())
    fn = int((~pred & (ok == 1)).sum())
    bc = np.array(burned_counts)
    out = {
        "zone": zone, "variant": variant, "wind_ms": sc.meta["wind_speed_ms"],
        "wind_dir_from_deg": sc.meta["wind_dir_from_deg"], "ignition": ignition,
        "ignition_nodes": sc.ignitions.tolist(), "runs": runs, "t_end_h": t_end_h,
        "observed_burned": int(obs.sum()), "labelled_buildings": int(known.sum()),
        "sim_burned_mean": float(bc.mean()), "sim_burned_p05_p50_p95": [float(np.percentile(bc, q)) for q in (5, 50, 95)],
        "brier": float(np.mean((pk - ok) ** 2)), "auc": _auc(pk, ok),
        "f1_p50": float(2 * tp / (2 * tp + fp + fn)) if tp + fp + fn else None,
        "jaccard_p50": float(tp / (tp + fp + fn)) if tp + fp + fn else None,
    }
    return _save(zone, f"realfire_{variant}", out)

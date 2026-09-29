"""Command line: `python -m ffe <command>` (or `ffe <command>` after pip install -e .)."""

from __future__ import annotations

import argparse
import os
import json
import sys

from pathlib import Path

from . import config, pipeline
from .zones import get_zone, load_zones


def cmd_zones(_args):
    for z in load_zones().values():
        print(f"{z.stage:4s} {z.id:18s} {z.kind:13s} {z.name}")


def cmd_plan(args):
    z = get_zone(args.zone)
    print(f"{z.id}: {z.name}\nstage {z.stage} | kind {z.kind} | bbox {z.bbox}\n")
    print(f"data dir: {config.zone_dir(z.id)}   (cloud session: {config.IN_CLOUD_SESSION}, "
          f"download limit: {'none' if config.MAX_DOWNLOAD_MB <= 0 else f'{config.MAX_DOWNLOAD_MB:.0f} MB'})\n")
    for r in pipeline.plan(z):
        mark = "x" if r["present"] else " "
        print(f"  [{mark}] {r['item']:24s} {r['where']:7s} ~{r['typical_mb']:>14s} MB  {r.get('what', '')}")
    print("\nwhere: cloud = fetchable anywhere | pc = host blocked in cloud session | manual = obtain by hand")
    if z.footprint_caveat:
        print(f"\nfootprint caveat: {z.footprint_caveat}")


def cmd_fetch(args):
    z = get_zone(args.zone)
    only = args.only.split(",") if args.only else None
    res = pipeline.fetch(z, only=only, allow_large=args.allow_large)
    blocked = [k for k, v in res.items() if v["status"] != "ok"]
    if blocked:
        print(f"\nNot fetched here: {', '.join(blocked)}. Re-run on the local PC:  "
              f"python -m ffe fetch {z.id} --only {','.join(blocked)}")


def cmd_build(args):
    z = get_zone(args.zone)
    rep = pipeline.build(z, radius_m=args.radius, grid_cell_m=args.grid_cell)
    print(json.dumps(rep, indent=2))


def cmd_usgs_lookup(args):
    from .sources import shakemap
    for e in shakemap.lookup_events(args.time, minmag=args.minmag):
        print(e)


def cmd_fetch_arcgis(args):
    from .sources import arcgis
    z = get_zone(args.zone)
    dest = config.local_inputs_dir(z.id) / (z.labels.get("file") or "labels.geojson")
    out = arcgis.fetch_layer(args.layer_url, dest, bbox=z.bbox)
    print(f"wrote {out} ({out.stat().st_size / 1e6:.1f} MB)")


def cmd_sim(args):
    from .sim import experiments as ex
    zones = args.zones.split(",")
    if args.sim_cmd == "compile":
        for z in zones:
            print(ex.compile_to_file(z, args.variant, args.wind, args.wind_dir, seed=args.seed,
                                     ignition=args.ignition, t_end_h=args.hours))
    elif args.sim_cmd == "verify":
        for z in zones:
            print(json.dumps(ex.verify(z, runs=args.runs), indent=1))
    elif args.sim_cmd == "rq1":
        for z in zones:
            ex.rq1(z, args.variant, runs=args.runs, tail_runs=args.tail_runs, procs=args.procs)
            print(f"rq1 done: {z}")
    elif args.sim_cmd == "rq2":
        for z in zones:
            print(json.dumps(ex.rq2(z, args.variant, wind=args.wind, runs=args.runs), indent=1))
    elif args.sim_cmd == "rq3":
        for z in zones:
            print(json.dumps(ex.rq3(z, args.variant, wind=args.wind, level=args.level,
                                    crude_runs=args.crude_runs, sus_repeats=args.repeats), indent=1))
    elif args.sim_cmd == "rq3deep":
        for z in zones:
            ex.rq3_deep(z, args.variant, wind=args.wind, crude_runs=args.crude_runs, sus_repeats=args.repeats,
                        procs=args.procs)
            print(f"rq3deep done: {z}")
    elif args.sim_cmd == "calibrate":
        from .sim import calibrate
        if not args.tests:
            print(f"template: {calibrate.write_template()}  (fill it, then pass --tests <csv>)")
            return
        res = calibrate.fit_fullscale(Path(args.tests), fit_sigma=args.fit_sigma)
        print(json.dumps({k: res[k] for k in ("n_tests", "params", "brier", "accuracy_at_0.5", "converged")}, indent=1))
    elif args.sim_cmd == "realfire":
        from .sim import calibrate
        for z in zones:
            print(json.dumps(calibrate.compare_real_fire(z, args.variant, wind=args.wind, wind_dir=args.wind_dir,
                                                         ignition=args.ignition if args.ignition != "center" else "zone",
                                                         runs=args.runs, t_end_h=args.hours), indent=1))
    elif args.sim_cmd == "unitypaired":
        for z in zones:
            res = ex.unity_paired(z)
            print(f"{z}: {len(res['rows'])} exact/stepped pairs -> {ex.sim_dir(z) / 'unity_paired.json'}")
    elif args.sim_cmd == "fixture":
        print(ex.write_fixture(config.REPO_ROOT / "Assets" / "Tests" / "FireGraph" / "Fixtures"))
    elif args.sim_cmd == "report":
        out = config.REPO_ROOT / "Documentation" / "Research" / "results" / "ExactFire_pilot.md"
        print(ex.report(zones, out))


def main(argv=None):
    p = argparse.ArgumentParser(prog="ffe", description="Open building data -> fire graph -> exact fire-spread scenarios for Unity")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("zones", help="list study zones").set_defaults(fn=cmd_zones)
    s = sub.add_parser("plan", help="what a zone needs, where each item can be fetched, what is present")
    s.add_argument("zone"); s.set_defaults(fn=cmd_plan)
    s = sub.add_parser("fetch", help="download open data for a zone")
    s.add_argument("zone"); s.add_argument("--only", help="comma list: buildings,roads,fire_stations,weather,dem,gem,shakemap")
    s.add_argument("--allow-large", action="store_true", help="ignore the cloud-session size limit")
    s.set_defaults(fn=cmd_fetch)
    s = sub.add_parser("build", help="build the building fire graph and export graph.ffeg")
    s.add_argument("zone"); s.add_argument("--radius", type=float, default=40.0, help="edge radius, m")
    s.add_argument("--grid-cell", type=float, default=25.0, help="firebrand grid cell, m")
    s.set_defaults(fn=cmd_build)
    s = sub.add_parser("usgs-lookup", help="find USGS event ids near a time (PC: needs earthquake.usgs.gov)")
    s.add_argument("--time", required=True); s.add_argument("--minmag", type=float, default=6.0)
    s.set_defaults(fn=cmd_usgs_lookup)
    s = sub.add_parser("fetch-arcgis", help="download a damage-inspection layer (e.g. CAL FIRE DINS) as zone labels")
    s.add_argument("zone"); s.add_argument("layer_url", help=".../FeatureServer/<layer>")
    s.set_defaults(fn=cmd_fetch_arcgis)
    s = sub.add_parser("sim", help="exact fire-spread solver: scenarios, verification, RQ1-RQ3 pilots")
    s.add_argument("sim_cmd", choices=["compile", "verify", "rq1", "rq2", "rq3", "rq3deep", "calibrate", "realfire", "unitypaired", "report", "fixture"])
    s.add_argument("zones", nargs="?", default="", help="zone id or comma list (graph.ffeg must exist)")
    s.add_argument("--variant", default=None, help="physics variant: base | critical")
    s.add_argument("--wind", default=None, help="wind speed m/s, or 'era5' (mean of the first 6 h after ignition)")
    s.add_argument("--wind-dir", type=float, default=180.0, help="direction the wind blows FROM, deg")
    s.add_argument("--runs", type=int, default=30)
    s.add_argument("--tail-runs", type=int, default=1000)
    s.add_argument("--crude-runs", type=int, default=20000)
    s.add_argument("--repeats", type=int, default=10)
    s.add_argument("--level", type=int, default=None)
    s.add_argument("--seed", type=int, default=1)
    s.add_argument("--ignition", default="center", help="center | zone (zones.yaml ignition_lonlat) | random:K | LON,LAT")
    s.add_argument("--hours", type=float, default=24.0, help="simulation horizon, h")
    s.add_argument("--procs", type=int, default=os.cpu_count() or 1, help="worker processes for Monte Carlo runs")
    s.add_argument("--tests", default=None, help="calibrate: full-scale test CSV")
    s.add_argument("--fit-sigma", action="store_true", help="calibrate: also fit the FTP spread")
    s.set_defaults(fn=cmd_sim)
    args = p.parse_args(argv)
    if args.cmd == "sim":
        defaults = {"rq3": ("critical", 0.0), "rq3deep": ("critical", 0.0), "compile": ("base", 5.0),
                    "realfire": ("base", "era5")}.get(args.sim_cmd, ("base", 5.0))
        args.variant = args.variant or defaults[0]
        args.wind = defaults[1] if args.wind is None else args.wind
        if args.wind != "era5":
            args.wind = float(args.wind)
    args.fn(args)


if __name__ == "__main__":
    sys.exit(main())

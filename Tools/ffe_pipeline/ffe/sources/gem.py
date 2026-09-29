"""GEM Global Exposure Model (raw GitHub, CC BY-NC-SA 4.0).

National taxonomy summaries (~40 KB) are reachable from the cloud session and
give a construction-mix prior (e.g. Japan: ~82% of buildings are wooden).
The 1 km disaggregated exposure (csv.gz) sits in the same repository; clone it
on a local PC (`git clone --depth 1 https://github.com/gem/global_exposure_model`,
several GB) and point `ffe build --gem-exposure <file>` at the country file.
"""

from __future__ import annotations

import csv
from collections import defaultdict
from pathlib import Path

from .. import config, net

# GEM macro-taxonomy prefixes treated as combustible structural systems.
COMBUSTIBLE_MACRO = {"W"}


def summary_url(region: str, country: str) -> str:
    return f"{config.GEM_EXPOSURE_RAW}/{region}/{country}/summaries/Exposure_Summary_Taxonomy.csv"


def fetch_taxonomy_summary(region: str, country: str, dest: Path) -> Path:
    return net.download(summary_url(region, country), dest)


def macro_shares(path: Path, occupancy: str = "RES") -> dict[str, float]:
    """Share of buildings by macro taxonomy (W, CR+, S, MUR, ...) for one occupancy class."""
    counts: dict[str, float] = defaultdict(float)
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            if occupancy and row.get("OCCUPANCY") != occupancy:
                continue
            counts[row["MACRO_TAXONOMY"]] += float(row.get("BUILDINGS") or 0)
    total = sum(counts.values())
    return {k: v / total for k, v in sorted(counts.items(), key=lambda kv: -kv[1])} if total else {}


def combustible_share(shares: dict[str, float]) -> float:
    return sum(v for k, v in shares.items() if k.split("+")[0].split("|")[0] in COMBUSTIBLE_MACRO)

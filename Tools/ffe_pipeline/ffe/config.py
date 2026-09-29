"""Paths and global settings. Everything is overridable with environment variables
so the same code runs in the cloud session and on a local PC."""

from __future__ import annotations

import os
from pathlib import Path

# Repository root = three levels above this file (Tools/ffe_pipeline/ffe/config.py).
REPO_ROOT = Path(__file__).resolve().parents[3]

# Where downloaded and derived data live. Git-ignored. Override with FFE_DATA_DIR.
DATA_DIR = Path(os.environ.get("FFE_DATA_DIR", REPO_ROOT / "FFEData")).resolve()

# The download-size guard exists only for the cloud research session (sandboxed,
# proxied network). On a local PC there is no limit: the chosen study zone decides
# how much is downloaded. FFE_MAX_DOWNLOAD_MB overrides either default (0 = no limit).
IN_CLOUD_SESSION = bool(os.environ.get("CCR_AGENT_PROXY_ENABLED")) or Path("/root/.ccr/ca-bundle.crt").exists()
MAX_DOWNLOAD_MB = float(os.environ.get("FFE_MAX_DOWNLOAD_MB", "90" if IN_CLOUD_SESSION else "0"))

# Pinned Overture release so results are reproducible.
OVERTURE_RELEASE = os.environ.get("FFE_OVERTURE_RELEASE", "2026-09-23.1")
OVERTURE_BUCKET = "overturemaps-us-west-2"
OVERTURE_REGION = "us-west-2"

ERA5_BASE = "https://nsf-ncar-era5.s3.amazonaws.com"
COPDEM_BASE = "https://copernicus-dem-30m.s3.amazonaws.com"
GEM_EXPOSURE_RAW = "https://raw.githubusercontent.com/gem/global_exposure_model/main"
USGS_EVENT_API = "https://earthquake.usgs.gov/fdsnws/event/1/query"


def zone_dir(zone_id: str) -> Path:
    return DATA_DIR / "zones" / zone_id


def raw_dir(zone_id: str) -> Path:
    d = zone_dir(zone_id) / "raw"
    d.mkdir(parents=True, exist_ok=True)
    return d


def derived_dir(zone_id: str) -> Path:
    d = zone_dir(zone_id) / "derived"
    d.mkdir(parents=True, exist_ok=True)
    return d


def local_inputs_dir(zone_id: str) -> Path:
    """Files the user downloads manually (blocked hosts, >90 MB, or hand-digitized labels)."""
    d = zone_dir(zone_id) / "local_inputs"
    d.mkdir(parents=True, exist_ok=True)
    return d

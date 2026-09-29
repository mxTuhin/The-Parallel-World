"""Scenario files (.ffes): the exact solver's input, read by Unity.

Same container as FFEG (ffe.ffeg.write_container) with magic b"FFES":

  node   tau0_s, tg_s, td_s, tx_s   burning profile per building (float32)
         x_m, y_m, area_m2, height_m (float32, for the viewer)
         ftp, eth                   OPTIONAL thresholds of one reference run
                                    (float32, for Python<->Unity parity tests)
  csr    in_offsets                 uint32, n+1 (incoming edges grouped by target)
  edge   src (uint32), a_kw_m2 (float32), b_per_s (float32)
  list   ignitions                  uint32 node indices ignited at t = 0

Manifest scalars: q_cr, ftp_n, ftp_mu, ftp_sigma, t_end_s, lookahead_s, and the
reference run's seed/replica when thresholds are included. The C# reader is
Assets/Script/FireGraph/FfesReader.cs.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np

from .. import ffeg
from .model import Scenario

MAGIC = b"FFES"


def write(path: Path, sc: Scenario, graph_arrays: dict, *, ref_thresholds=None,
          ref_seed: int | None = None, ref_replica: int | None = None, extra: dict | None = None) -> Path:
    f32 = np.float32
    arrays = [
        ("tau0_s", "node", sc.tau0.astype(f32)), ("tg_s", "node", sc.tg.astype(f32)),
        ("td_s", "node", sc.td.astype(f32)), ("tx_s", "node", sc.tx.astype(f32)),
        ("x_m", "node", np.asarray(graph_arrays["x_m"], f32)), ("y_m", "node", np.asarray(graph_arrays["y_m"], f32)),
        ("area_m2", "node", np.asarray(graph_arrays["area_m2"], f32)),
        ("height_m", "node", np.asarray(graph_arrays["height_m"], f32)),
    ]
    if ref_thresholds is not None:
        ftp, eth = ref_thresholds
        arrays += [("ftp", "node", ftp.astype(f32)), ("eth", "node", eth.astype(f32))]
    arrays += [
        ("in_offsets", "csr", sc.in_offsets.astype(np.uint32)),
        ("src", "edge", sc.src.astype(np.uint32)),
        ("a_kw_m2", "edge", sc.a.astype(f32)),
        ("b_per_s", "edge", sc.b.astype(f32)),
        ("ignitions", "list", sc.ignitions.astype(np.uint32)),
    ]
    manifest = {
        "format": "FFES", "version": ffeg.VERSION,
        "n_nodes": sc.n, "n_edges": sc.n_edges,
        "q_cr": sc.q_cr, "ftp_n": sc.ftp_n, "ftp_mu": sc.ftp_mu, "ftp_sigma": sc.ftp_sigma,
        "t_end_s": sc.t_end, "lookahead_s": sc.lookahead(),
        "ref_seed": -1 if ref_seed is None else int(ref_seed),        # -1 = no reference thresholds
        "ref_replica": -1 if ref_replica is None else int(ref_replica),
        "meta": sc.meta, **(extra or {}),
    }
    return ffeg.write_container(path, MAGIC, manifest, arrays)


def read(path: Path) -> tuple[Scenario, dict, dict]:
    """Scenario (float32 values widened to float64), manifest, raw arrays."""
    man, arr = ffeg.read(path, magic=MAGIC)
    f64 = np.float64
    sc = Scenario(
        n=man["n_nodes"], in_offsets=arr["in_offsets"].astype(np.int64), src=arr["src"].astype(np.int64),
        a=arr["a_kw_m2"].astype(f64), b=arr["b_per_s"].astype(f64),
        tau0=arr["tau0_s"].astype(f64), tg=arr["tg_s"].astype(f64), td=arr["td_s"].astype(f64),
        tx=arr["tx_s"].astype(f64), q_cr=man["q_cr"], ftp_n=man["ftp_n"], ftp_mu=man["ftp_mu"],
        ftp_sigma=man["ftp_sigma"], ignitions=arr["ignitions"].astype(np.int64), t_end=man["t_end_s"],
        meta=man.get("meta", {}),
    )
    return sc, man, arr

"""FFEG v1: the binary graph file handed to the Unity engine.

Layout (little-endian):
    bytes 0-3   magic b"FFEG"
    bytes 4-7   uint32 format version (1)
    bytes 8-15  uint64 manifest length M
    bytes 16..  UTF-8 JSON manifest (M bytes), then zero padding to a 16-byte boundary
    data        raw arrays; each starts on a 16-byte boundary. The manifest lists
                every array as {name, group, dtype, count, offset, nbytes} with
                offset relative to the start of the data section.

Groups: "node" (length n_nodes), "csr" (in_offsets, length n_nodes+1),
"edge" (length n_edges), "grid" (cell_offsets length nx*ny+1, cell_items length n_nodes).
dtypes use numpy names: float32, uint32, int8, uint8.

C# can read this with one FileStream: parse the header and JSON, then copy each
array into a NativeArray<T> (or GraphicsBuffer) at the listed offset.
"""

from __future__ import annotations

import json
import struct
from pathlib import Path

import numpy as np

MAGIC = b"FFEG"
VERSION = 1
ALIGN = 16
ALLOWED = {"float32", "uint32", "int32", "int8", "uint8"}


def _pad(n: int) -> int:
    return (-n) % ALIGN


def write_container(path: Path, magic: bytes, manifest: dict,
                    arrays: list[tuple[str, str, np.ndarray]]) -> Path:
    """Write any manifest + named arrays in the FFEG container layout under `magic`.

    Used for graphs (b"FFEG") and simulation scenarios (b"FFES", see ffe.sim.scenario).
    The "arrays" entry of the manifest is filled in here.
    """
    if len(magic) != 4:
        raise ValueError("magic must be 4 bytes")
    entries, offset = [], 0
    for name, group, a in arrays:
        a = np.ascontiguousarray(a)
        if a.dtype.name not in ALLOWED:
            raise TypeError(f"{name}: dtype {a.dtype} not allowed in FFEG")
        if a.dtype.byteorder == ">":
            raise TypeError(f"{name}: big-endian arrays are not allowed")
        entries.append({"name": name, "group": group, "dtype": a.dtype.name, "count": int(a.size),
                        "offset": offset, "nbytes": int(a.nbytes)})
        offset += a.nbytes + _pad(a.nbytes)
    manifest = {**manifest, "arrays": entries}
    blob = json.dumps(manifest, separators=(",", ":")).encode("utf-8")
    header_len = 16 + len(blob)
    with open(path, "wb") as f:
        f.write(magic + struct.pack("<IQ", VERSION, len(blob)))
        f.write(blob + b"\0" * _pad(header_len))
        for (_, _, a), e in zip(arrays, entries):
            f.write(np.ascontiguousarray(a).astype(a.dtype.newbyteorder("<"), copy=False).tobytes())
            f.write(b"\0" * _pad(e["nbytes"]))
    return Path(path)


def write(path: Path, graph, extra_node: dict[str, np.ndarray] | None = None, manifest_extra: dict | None = None):
    arrays: list[tuple[str, str, np.ndarray]] = []
    for name, a in {**graph.node, **(extra_node or {})}.items():
        arrays.append((name, "node", a))
    arrays.append(("in_offsets", "csr", graph.in_offsets))
    for name, a in graph.edge.items():
        arrays.append((name, "edge", a))
    arrays.append(("cell_offsets", "grid", graph.grid["cell_offsets"]))
    arrays.append(("cell_items", "grid", graph.grid["cell_items"]))
    manifest = {
        "format": "FFEG", "version": VERSION,
        "n_nodes": graph.n_nodes, "n_edges": graph.n_edges,
        "crs_proj4": graph.crs_proj4, "origin_lonlat": list(graph.origin_lonlat),
        "grid": {k: graph.grid[k] for k in ("cell_m", "x0_m", "y0_m", "nx", "ny")},
        "units": {"x_m": "m", "y_m": "m", "area_m2": "m2", "height_m": "m", "gap_m": "m",
                  "bearing_rad": "rad CCW from east", "facing_m": "m"},
        **(manifest_extra or {}),
    }
    return write_container(path, MAGIC, manifest, arrays)


def read(path: Path, magic: bytes = MAGIC) -> tuple[dict, dict[str, np.ndarray]]:
    raw = Path(path).read_bytes()
    if raw[:4] != magic:
        raise ValueError(f"{path}: not a {magic.decode()} file")
    version, mlen = struct.unpack_from("<IQ", raw, 4)
    if version != VERSION:
        raise ValueError(f"{path}: unsupported version {version}")
    manifest = json.loads(raw[16:16 + mlen])
    data_start = 16 + mlen + _pad(16 + mlen)
    arrays = {}
    for e in manifest["arrays"]:
        start = data_start + e["offset"]
        arrays[e["name"]] = np.frombuffer(raw, dtype=np.dtype(e["dtype"]).newbyteorder("<"),
                                          count=e["count"], offset=start)
    return manifest, arrays

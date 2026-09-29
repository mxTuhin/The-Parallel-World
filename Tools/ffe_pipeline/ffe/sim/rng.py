"""Counter-based random numbers shared by Python, C# (Burst) and HLSL.

Every random input of a run is a pure function of (seed, replica, node, stream),
so results do not depend on thread count, update order or backend.

Philox4x32-10 (Salmon et al. 2011, Random123). Counter = (node, stream, replica, extra),
key = (seed low 32 bits, seed high 32 bits).

Streams:
  STREAM_FTP    -> standard normal z1, FTP threshold = exp(mu + sigma * z1)
  STREAM_BRAND  -> uniform u2, firebrand threshold E = -ln(u2) ~ Exp(1)
  STREAM_STEP   -> per-step Bernoulli draws of the time-stepped baseline (extra = step)

The C# port is Assets/Script/FireGraph/Philox.cs; keep the two in sync.
"""

from __future__ import annotations

import numpy as np
from numba import njit

M0, M1 = 0xD2511F53, 0xCD9E8D57
W0, W1 = 0x9E3779B9, 0xBB67AE85
MASK = 0xFFFFFFFF

STREAM_FTP, STREAM_BRAND, STREAM_STEP = 0, 1, 2
TWO_M53 = 1.0 / 9007199254740992.0


@njit(cache=True)
def philox4x32(c0, c1, c2, c3, k0, k1):
    """Philox4x32-10 on uint32 values held in int64 (numba-friendly). Returns 4 ints."""
    for r in range(10):
        p0 = np.uint64(M0) * np.uint64(c0)
        p1 = np.uint64(M1) * np.uint64(c2)
        hi0, lo0 = np.int64(p0 >> np.uint64(32)), np.int64(p0 & np.uint64(MASK))
        hi1, lo1 = np.int64(p1 >> np.uint64(32)), np.int64(p1 & np.uint64(MASK))
        c0, c1, c2, c3 = hi1 ^ c1 ^ k0, lo1, hi0 ^ c3 ^ k1, lo0
        if r < 9:
            k0 = (k0 + W0) & MASK
            k1 = (k1 + W1) & MASK
    return c0, c1, c2, c3


@njit(cache=True)
def uniform01(x0, x1):
    """53-bit uniform in the open interval (0, 1) from two uint32 words."""
    return ((x0 >> 5) * 67108864.0 + (x1 >> 6) + 0.5) * TWO_M53


@njit(cache=True)
def norm_ppf(p):
    """Inverse standard normal CDF (Acklam, relative error < 1.15e-9). Only log and sqrt."""
    a1, a2, a3 = -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02
    a4, a5, a6 = 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00
    b1, b2, b3 = -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02
    b4, b5 = 6.680131188771972e+01, -1.328068155288572e+01
    c1, c2, c3 = -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00
    c4, c5, c6 = -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00
    d1, d2, d3, d4 = 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00
    plow = 0.02425
    if p < plow:
        q = np.sqrt(-2.0 * np.log(p))
        return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0)
    if p > 1.0 - plow:
        q = np.sqrt(-2.0 * np.log(1.0 - p))
        return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0)
    q = p - 0.5
    r = q * q
    return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q / (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0)


@njit(cache=True)
def uniform_at(seed, replica, node, stream, extra):
    k0, k1 = seed & MASK, (seed >> 32) & MASK
    x0, x1, _, _ = philox4x32(node & MASK, stream, replica & MASK, extra & MASK, k0, k1)
    return uniform01(x0, x1)


@njit(cache=True)
def _draw(n, seed, replica, ftp_mu, ftp_sigma, ftp_out, e_out):
    for i in range(n):
        z = norm_ppf(uniform_at(seed, replica, i, STREAM_FTP, 0))
        ftp_out[i] = np.exp(ftp_mu + ftp_sigma * z)
        e_out[i] = -np.log(uniform_at(seed, replica, i, STREAM_BRAND, 0))


def draw_thresholds(n: int, seed: int, replica: int, ftp_mu: float, ftp_sigma: float):
    """Per-building ignition thresholds (FTP, firebrand Exp(1)) for one run."""
    ftp, e = np.empty(n), np.empty(n)
    _draw(n, int(seed), int(replica), float(ftp_mu), float(ftp_sigma), ftp, e)
    return ftp, e

"""Exact event-driven solver and the conventional time-stepped baseline.

Exact solver
  For every unburned building the next ignition time is a closed-form root of
  its heat-dose and firebrand-hazard clocks (see model.py), computed from the
  set of buildings already ignited. The solver repeatedly commits ignitions in
  time order. Three commit rules, all giving bit-identical results:

    SEQUENTIAL  commit only the earliest predicted ignition (classic next-event
                simulation; the reference).
    GLOBAL      commit every prediction earlier than t_min + L, L = min tau0.
                Safe because a building ignited at s >= t_min emits nothing
                before s + tau0 >= t_min + L.
    LOCAL       commit prediction p_i if p_i < t_min + min tau0_k over i's still
                unburned in-neighbours k (k can only change i's clocks through
                its own emission). Larger windows, still exact.

  Predictions are recomputed from scratch with a fixed summation order (CSR
  order), so the result does not depend on the commit rule, batch size or
  thread count. This is the property the Unity Burst/GPU ports reproduce.

Time-stepped baseline
  The update used by most cellular-automaton and graph fire models: every dt,
  evaluate the flux, add (q - q_cr)_+^n * dt to the dose (explicit Euler),
  ignite at the end of the step when a threshold is passed. Firebrands either
  share the exact solver's Exp(1) thresholds (BRAND_HAZARD, common random
  numbers: isolates the discretisation error) or use one Bernoulli draw per
  step with p = 1 - exp(-lambda dt) (BRAND_BERNOULLI, the usual practice).
"""

from __future__ import annotations

import numpy as np
from numba import njit

from .rng import STREAM_STEP, uniform_at

INF = np.inf
SEQUENTIAL, GLOBAL, LOCAL = 0, 1, 2
BRAND_HAZARD, BRAND_BERNOULLI = 0, 1
IGNITE_END_OF_STEP, IGNITE_INTERPOLATED = 0, 1


@njit(cache=True, inline="always")
def phi(tau, t0, tg, td, tx):
    """Burning intensity in [0, 1] at time tau after ignition."""
    if tau <= t0:
        return 0.0
    s = tau - t0
    if s < tg:
        return s / tg
    s -= tg
    if s < td:
        return 1.0
    s -= td
    if s < tx:
        return 1.0 - s / tx
    return 0.0


@njit(cache=True, inline="always")
def lin_cross(r0, m, rem):
    """tau >= 0 with r0*tau + m*tau^2/2 = rem (r0 >= 0; a root is known to exist)."""
    disc = r0 * r0 + 2.0 * m * rem
    if disc < 0.0:
        disc = 0.0
    den = r0 + np.sqrt(disc)
    if den <= 0.0:
        return INF
    return 2.0 * rem / den


@njit(cache=True, inline="always")
def dphi(tau_mid, t0, tg, td, tx):
    """Slope of phi inside a segment, identified from the segment midpoint.

    Using the midpoint (not the float breakpoint itself) makes the phase robust to
    rounding, and the slope is exactly 1/tg, 0 or -1/tx.
    """
    if tau_mid <= t0:
        return 0.0
    s = tau_mid - t0
    if s < tg:
        return 1.0 / tg
    s -= tg
    if s < td:
        return 0.0
    s -= td
    if s < tx:
        return -1.0 / tx
    return 0.0


@njit(cache=True, inline="always")
def pos_gain(rs, m, T, n):
    """Integral over [0, T] of r(t)^n with r = rs + m t >= 0 on the interval."""
    if n == 1.0:
        return (rs + 0.5 * m * T) * T
    if m == 0.0:
        return rs ** n * T
    re = rs + m * T
    if re < 0.0:
        re = 0.0
    return (re ** (n + 1.0) - rs ** (n + 1.0)) / ((n + 1.0) * m)


@njit(cache=True, inline="always")
def pos_cross(rs, m, n, rem):
    """Time at which the integral of (rs + m t)^n reaches rem (a root is known to exist)."""
    if n == 1.0:
        return lin_cross(rs, m, rem)
    if m == 0.0:
        return rem / rs ** n
    val = rs ** (n + 1.0) + rem * m * (n + 1.0)
    if val < 0.0:
        val = 0.0
    return (val ** (1.0 / (n + 1.0)) - rs) / m


@njit(cache=True)
def node_clock(i, off, src, a, b, t_ign, tau0, tg, td, tx, q_cr, n_exp, ftp_i, e_i, horizon, buf):
    """Exact next ignition time of building i given the ignited set, and its progress.

    Returns (t_cross, progress). t_cross = inf if no crossing before `horizon`;
    progress = max(dose/FTP, hazard/E) reached by `horizon` (1.0 when it ignites).

    Each segment between source breakpoints is described only by its start value
    and slope, so inserting a later source's breakpoint (which happens when that
    source is committed after i's prediction) cannot change i's crossing time by
    even one ulp. That is what makes all commit rules bit-identical.
    """
    k = 0
    for e in range(off[i], off[i + 1]):
        j = src[e]
        tj = t_ign[j]
        if tj < INF:
            s0 = tj + tau0[j]
            s1 = s0 + tg[j]
            s2 = s1 + td[j]
            buf[k] = s0
            buf[k + 1] = s1
            buf[k + 2] = s2
            buf[k + 3] = s2 + tx[j]
            k += 4
    if k == 0:
        return INF, 0.0
    bp = np.sort(buf[:k])
    D = 0.0
    H = 0.0
    for s in range(k - 1):
        t0 = bp[s]
        t1 = bp[s + 1]
        if t0 >= horizon:
            break
        if t1 > horizon:
            t1 = horizon
        if t1 <= t0:
            continue
        tm = 0.5 * (t0 + t1)
        q0 = 0.0
        sq = 0.0
        l0 = 0.0
        sl = 0.0
        for e in range(off[i], off[i + 1]):
            j = src[e]
            tj = t_ign[j]
            if tj < INF:
                f0 = phi(t0 - tj, tau0[j], tg[j], td[j], tx[j])
                g = dphi(tm - tj, tau0[j], tg[j], td[j], tx[j])
                q0 += a[e] * f0
                sq += a[e] * g
                l0 += b[e] * f0
                sl += b[e] * g
        T = t1 - t0
        tc = INF
        gH = (l0 + 0.5 * sl * T) * T
        if gH > 0.0 and H + gH >= e_i:
            tc = t0 + lin_cross(l0, sl, e_i - H)
        r0 = q0 - q_cr
        r1 = r0 + sq * T
        if r0 > 0.0 or r1 > 0.0:
            ts = t0
            rs = r0
            te = t1
            if r0 <= 0.0:
                ts = t0 + (-r0) / sq          # sq > 0 here
                rs = 0.0
            elif r1 < 0.0:
                te = t0 + r0 / (-sq)          # sq < 0 here
            if te > ts:
                gD = pos_gain(rs, sq, te - ts, n_exp)
                if gD > 0.0 and D + gD >= ftp_i:
                    tr = ts + pos_cross(rs, sq, n_exp, ftp_i - D)
                    if tr < tc:
                        tc = tr
                D += gD
        H += gH
        if tc < INF:
            if tc < t0:
                tc = t0
            if tc > t1:
                tc = t1
            if tc >= horizon:
                return INF, 1.0
            return tc, 1.0
    prog = D / ftp_i
    if H / e_i > prog:
        prog = H / e_i
    return INF, prog


@njit(cache=True)
def run_exact(off, src, a, b, out_off, out_dst, tau0, tg, td, tx, q_cr, n_exp,
              ftp, eth, ign, t_end, mode):
    """Exact event-driven run. Returns (t_ign, iterations, predictions computed)."""
    n = len(off) - 1
    t_ign = np.full(n, INF)
    pred = np.full(n, INF)
    dirty = np.zeros(n, dtype=np.bool_)
    maxdeg = 1
    for i in range(n):
        d = off[i + 1] - off[i]
        if d > maxdeg:
            maxdeg = d
    buf = np.empty(4 * maxdeg)
    commit = np.empty(n, dtype=np.int64)
    L = INF
    for i in range(n):
        if tau0[i] < L:
            L = tau0[i]
    for c in range(len(ign)):
        t_ign[ign[c]] = 0.0
    for c in range(len(ign)):
        i = ign[c]
        for e in range(out_off[i], out_off[i + 1]):
            dirty[out_dst[e]] = True
    iters = 0
    n_pred = 0
    while True:
        for i in range(n):
            if dirty[i]:
                dirty[i] = False
                if t_ign[i] == INF:
                    pred[i], _ = node_clock(i, off, src, a, b, t_ign, tau0, tg, td, tx,
                                            q_cr, n_exp, ftp[i], eth[i], t_end, buf)
                    n_pred += 1
        tmin = INF
        for i in range(n):
            if t_ign[i] == INF and pred[i] < tmin:
                tmin = pred[i]
        if tmin == INF:
            break
        nc = 0
        for i in range(n):
            p = pred[i]
            if t_ign[i] < INF or p == INF:
                continue
            if p == tmin:
                ok = True
            elif mode == SEQUENTIAL:
                ok = False
            elif mode == GLOBAL:
                ok = p < tmin + L
            else:
                safe = INF
                for e in range(off[i], off[i + 1]):
                    kk = src[e]
                    if t_ign[kk] == INF:
                        s_ = tmin + tau0[kk]
                        if s_ < safe:
                            safe = s_
                ok = p < safe
            if ok:
                commit[nc] = i
                nc += 1
        for c in range(nc):
            i = commit[c]
            t_ign[i] = pred[i]
            pred[i] = INF
            for e in range(out_off[i], out_off[i + 1]):
                dirty[out_dst[e]] = True
        iters += 1
    return t_ign, iters, n_pred


@njit(cache=True)
def progress_all(off, src, a, b, t_ign, tau0, tg, td, tx, q_cr, n_exp, ftp, eth, t_end):
    """Max over unburned buildings of threshold progress at t_end (tie-breaker score)."""
    n = len(off) - 1
    maxdeg = 1
    for i in range(n):
        d = off[i + 1] - off[i]
        if d > maxdeg:
            maxdeg = d
    buf = np.empty(4 * maxdeg)
    best = 0.0
    for i in range(n):
        if t_ign[i] < t_end:
            continue
        _, pr = node_clock(i, off, src, a, b, t_ign, tau0, tg, td, tx, q_cr, n_exp, ftp[i], eth[i], t_end, buf)
        if pr > best:
            best = pr
    return best


@njit(cache=True)
def run_stepped(off, src, a, b, out_off, out_dst, tau0, tg, td, tx, q_cr, n_exp,
                ftp, eth, ign, t_end, dt, brand_mode, seed, replica, ignite_mode):
    """Conventional synchronous time-stepped run (explicit Euler dose). Returns t_ign.

    ignite_mode END_OF_STEP: a building that passes its threshold during step s is
    ignited at t_s + dt (standard CA update). INTERPOLATED: the ignition time is
    linearly interpolated inside the step (the strongest cheap fix a time-stepped
    model can make); the building still only starts heating others from the next step.
    """
    n = len(off) - 1
    t_ign = np.full(n, INF)
    D = np.zeros(n)
    H = np.zeros(n)
    active = np.zeros(n, dtype=np.bool_)
    act = np.empty(n, dtype=np.int64)
    na = 0
    newly = np.empty(n, dtype=np.int64)
    frac = np.empty(n)
    last_end = 0.0
    for c in range(len(ign)):
        i = ign[c]
        t_ign[i] = 0.0
        end = tau0[i] + tg[i] + td[i] + tx[i]
        if end > last_end:
            last_end = end
    for c in range(len(ign)):
        i = ign[c]
        for e in range(out_off[i], out_off[i + 1]):
            d = out_dst[e]
            if t_ign[d] == INF and not active[d]:
                active[d] = True
                act[na] = d
                na += 1
    nsteps = int(np.ceil(t_end / dt))
    for s in range(nsteps):
        t = s * dt
        if t >= last_end:
            break
        nn = 0
        for c in range(na):
            i = act[c]
            if t_ign[i] < INF:
                continue
            q = 0.0
            lam = 0.0
            for e in range(off[i], off[i + 1]):
                j = src[e]
                tj = t_ign[j]
                if tj < INF and tj <= t:
                    f = phi(t - tj, tau0[j], tg[j], td[j], tx[j])
                    q += a[e] * f
                    lam += b[e] * f
            r = q - q_cr
            f_i = 1.0
            fire = False
            if r > 0.0:
                gain = (r if n_exp == 1.0 else r ** n_exp) * dt
                if D[i] + gain >= ftp[i]:
                    fire = True
                    f_i = (ftp[i] - D[i]) / gain
                D[i] += gain
            if brand_mode == BRAND_HAZARD:
                gh = lam * dt
                if gh > 0.0 and H[i] + gh >= eth[i]:
                    fh = (eth[i] - H[i]) / gh
                    if not fire or fh < f_i:
                        f_i = fh
                    fire = True
                H[i] += gh
            elif lam > 0.0:
                u = uniform_at(seed, replica, i, STREAM_STEP, s)
                if u < 1.0 - np.exp(-lam * dt):
                    if not fire:
                        f_i = u / (1.0 - np.exp(-lam * dt))
                    fire = True
            if fire:
                newly[nn] = i
                frac[nn] = f_i
                nn += 1
        for c in range(nn):
            i = newly[c]
            t_ign[i] = t + dt if ignite_mode == IGNITE_END_OF_STEP else t + frac[c] * dt
            end = t_ign[i] + tau0[i] + tg[i] + td[i] + tx[i]
            if end > last_end:
                last_end = end
            for e in range(out_off[i], out_off[i + 1]):
                d = out_dst[e]
                if t_ign[d] == INF and not active[d]:
                    active[d] = True
                    act[na] = d
                    na += 1
        # compact the active list
        m = 0
        for c in range(na):
            if t_ign[act[c]] == INF:
                act[m] = act[c]
                m += 1
        na = m
    return t_ign


# ---------------------------------------------------------------- thin wrappers

def _args(sc):
    """Solver arguments, cached on the scenario object."""
    cached = getattr(sc, "_solver_args", None)
    if cached is None:
        out_off, out_dst = sc.out_adjacency()
        cached = (sc.in_offsets, sc.src, sc.a, sc.b, out_off, out_dst, sc.tau0, sc.tg, sc.td, sc.tx,
                  float(sc.q_cr), float(sc.ftp_n))
        sc._solver_args = cached
    return cached


def exact(sc, ftp, eth, mode=LOCAL):
    """(t_ign, iterations, predictions) for one run."""
    return run_exact(*_args(sc), ftp, eth, sc.ignitions, float(sc.t_end), mode)


def stepped(sc, ftp, eth, dt, brand_mode=BRAND_HAZARD, seed=0, replica=0, ignite_mode=IGNITE_END_OF_STEP):
    return run_stepped(*_args(sc), ftp, eth, sc.ignitions, float(sc.t_end), float(dt),
                       brand_mode, int(seed), int(replica), ignite_mode)


def progress(sc, t_ign, ftp, eth):
    return progress_all(sc.in_offsets, sc.src, sc.a, sc.b, t_ign, sc.tau0, sc.tg, sc.td, sc.tx,
                        float(sc.q_cr), float(sc.ftp_n), ftp, eth, float(sc.t_end))

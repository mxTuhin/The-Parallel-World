"""Subset simulation for rare conflagration probabilities.

Every random input of a run lives in one vector z of independent standard
normals (two per building):
    FTP_i = exp(mu + sigma * z_i)                 radiation threshold
    E_i   = -ln(Phi(z_{n+i}))                     firebrand threshold, Exp(1)
so P(score(z) >= level) can be estimated with subset simulation (Au & Beck
2001): a product of conditional probabilities p0, each estimated with MCMC
restricted to the previous intermediate level. Moves use the preconditioned
Crank-Nicolson proposal z' = sqrt(1 - beta^2) z + beta xi, which leaves the
standard normal invariant in any dimension, so only the level constraint is
checked in the accept step.

The score is the number of buildings burned by t_end plus a tie-breaker in
[0, 1): the largest threshold progress of any unburned building. The integer
count alone has large plateaus that stall the intermediate levels.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from scipy.special import log_ndtr

from . import exact


def thresholds_from_z(sc, z: np.ndarray):
    n = sc.n
    ftp = np.exp(sc.ftp_mu + sc.ftp_sigma * z[:n])
    e = -log_ndtr(z[n:])
    return ftp, e


def score(sc, z: np.ndarray, mode=exact.LOCAL) -> float:
    ftp, e = thresholds_from_z(sc, z)
    t_ign, _, _ = exact.exact(sc, ftp, e, mode)
    burned = int((t_ign < sc.t_end).sum())
    tie = exact.progress(sc, t_ign, ftp, e)
    return burned + min(tie, 0.999999)


@dataclass
class SubsetResult:
    p: float
    levels: list
    n_evals: int
    accept_rates: list


def subset_simulation(score_fn, dim: int, level: float, *, n: int = 1000, p0: float = 0.1,
                      beta: float = 0.5, max_levels: int = 12, rng: np.random.Generator) -> SubsetResult:
    """Estimate P(score_fn(z) >= level) for z ~ N(0, I_dim)."""
    z = rng.standard_normal((n, dim))
    g = np.array([score_fn(zi) for zi in z])
    evals = n
    n_seed = int(round(p0 * n))
    chain_len = n // n_seed
    p, levels, acc = 1.0, [], []
    for _ in range(max_levels):
        order = np.argsort(-g, kind="stable")
        thr = g[order[n_seed - 1]]
        if thr >= level:
            p *= float(np.mean(g >= level))
            levels.append(level)
            return SubsetResult(p, levels, evals, acc)
        # guard: a threshold equal to the minimum would not shrink the domain
        p *= float(np.mean(g >= thr))
        levels.append(float(thr))
        seeds = order[:n_seed]
        new_z = np.empty_like(z)
        new_g = np.empty_like(g)
        k, accepted, proposed = 0, 0, 0
        c = np.sqrt(1.0 - beta * beta)
        for s in seeds:
            cur_z, cur_g = z[s].copy(), g[s]
            for _ in range(chain_len):
                cand = c * cur_z + beta * rng.standard_normal(dim)
                cg = score_fn(cand)
                evals += 1
                proposed += 1
                if cg >= thr:
                    cur_z, cur_g = cand, cg
                    accepted += 1
                new_z[k], new_g[k] = cur_z, cur_g
                k += 1
        z, g = new_z[:k], new_g[:k]
        n = k
        acc.append(accepted / max(proposed, 1))
    p *= float(np.mean(g >= level))
    levels.append(level)
    return SubsetResult(p, levels, evals, acc)

"""Reference fire-spread model M0: geometry + weather -> per-edge coefficients.

The paper's claims are about *how* the model is solved (exactly, in parallel,
reproducibly, with rare-event estimation), not about a new physics model. M0
therefore uses standard, well-documented components with literature-range
defaults. Calibration against full-scale tests is a later step (see the plan).

Building j, ignited at t_j, has a burning intensity phi_j(t) in [0, 1]:

    phi = 0                      for t - t_j <= tau0_j   (incubation: fire is inside,
                                                          no external flames yet)
    rises linearly to 1          over tg_j               (growth to full involvement)
    stays at 1                   for td_j                (fully developed)
    falls linearly to 0          over tx_j               (decay)

Unburned building i receives
    radiant flux   q_i(t)      = sum_j a_ji * phi_j(t)             [kW/m2]
    firebrand rate lambda_i(t) = sum_j b_ji * phi_j(t)             [1/s]
and ignites at the first time either clock crosses its random threshold:
    radiation (flux-time product):  integral (q_i - q_cr)_+^n dt >= FTP_i
    firebrands (Poisson process):   integral lambda_i dt          >= E_i ~ Exp(1)

Both clocks are piecewise polynomial between breakpoints of the phi_j, so the
crossing times have closed forms (ffe.sim.exact). tau0 > 0 gives the solver a
physical lookahead: a newly ignited building cannot heat anyone for tau0.

Coefficients
  a_ji = E_fl * F(w, h_f, d_eff)   view factor from a point on i's facade to a
         parallel emitting rectangle (width = facing width, height = flame
         height), distance reduced downwind by flame tilt:
         d_eff = max(gap - h_f * tan(alpha) * cos(theta), d_min),
         tan(alpha) = min(U / u_tilt, tan_max),
         theta = angle between the wind (to-direction) and the j->i bearing.
  b_ji = beta * (A_j / 100 m2) * (A_i / 100 m2)^0.5 * exp(-d_c / ell(U)) * ((1 + cos theta) / 2)^2
         with ell(U) = ell0 * (1 + U / u_ell), d_c = centre distance.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field

import numpy as np


@dataclass
class Params:
    # Radiation (flux-time product ignition)
    e_flame_kw_m2: float = 50.0     # facade-averaged emissive power at full involvement
    flame_height_factor: float = 1.0   # emitting height = factor * building height
    q_cr_kw_m2: float = 10.0        # critical flux of timber cladding (piloted ~10-13)
    ftp_n: float = 1.0              # FTP exponent n (1 gives the simplest closed form)
    ftp_median: float = 1.0e4       # median FTP [(kW/m2)^n s]; ~15 min at 20 kW/m2 excess
    ftp_sigma: float = 0.4          # log-normal spread between buildings
    d_min_m: float = 0.5
    # Wind tilt
    u_tilt_ms: float = 6.0
    tan_max: float = 2.0
    # Firebrands
    brand_beta_per_s: float = 2.0e-4
    brand_ell0_m: float = 8.0
    brand_u_ell_ms: float = 5.0
    # Burning profile [s]
    tau0_s: float = 300.0           # incubation (no external flames); = solver lookahead
    tg_s: float = 300.0
    td_ref_s: float = 1800.0        # full-involvement time of a 100 m2 building
    td_min_s: float = 600.0
    td_max_s: float = 3600.0
    tx_s: float = 1200.0
    # Pruning
    a_min_kw_m2: float = 0.05
    b_min_per_s: float = 1e-8

    def to_dict(self):
        return asdict(self)


@dataclass
class Scenario:
    """Everything a solver needs. Edge arrays are aligned with in_offsets (CSR by target)."""
    n: int
    in_offsets: np.ndarray        # int64, n+1
    src: np.ndarray               # int64
    a: np.ndarray                 # float64 kW/m2 at phi = 1
    b: np.ndarray                 # float64 1/s at phi = 1
    tau0: np.ndarray              # float64 per node
    tg: np.ndarray
    td: np.ndarray
    tx: np.ndarray
    q_cr: float
    ftp_n: float
    ftp_mu: float                 # ln(median)
    ftp_sigma: float
    ignitions: np.ndarray         # int64 node indices ignited at t = 0
    t_end: float
    meta: dict = field(default_factory=dict)

    @property
    def n_edges(self):
        return int(self.in_offsets[-1])

    def out_adjacency(self):
        """CSR over outgoing edges (for dirty marking in the CPU reference)."""
        dst = np.repeat(np.arange(self.n), np.diff(self.in_offsets))
        order = np.argsort(self.src, kind="stable")
        out_off = np.zeros(self.n + 1, dtype=np.int64)
        np.cumsum(np.bincount(self.src, minlength=self.n), out=out_off[1:])
        return out_off, dst[order].astype(np.int64)

    def lookahead(self):
        return float(self.tau0.min()) if self.n else 0.0


def view_factor_parallel(w, h, d):
    """Point on a target facing the centre of a parallel w x h rectangle at distance d."""
    w, h, d = np.broadcast_arrays(np.asarray(w, float), np.asarray(h, float), np.asarray(d, float))
    x = (0.5 * w) / d
    y = (0.5 * h) / d
    sx, sy = np.sqrt(1 + x * x), np.sqrt(1 + y * y)
    corner = (x / sx * np.arctan(y / sx) + y / sy * np.arctan(x / sy)) / (2 * np.pi)
    return 4.0 * corner


def wind_to_vector(speed_ms: float, dir_from_deg: float):
    """Meteorological 'from' direction (0 = north, clockwise) -> unit 'to' vector (east, north)."""
    th = np.deg2rad(dir_from_deg)
    return -np.sin(th), -np.cos(th)


def compile_scenario(arrays: dict, params: Params, *, wind_speed_ms: float, wind_dir_from_deg: float,
                     ignitions, t_end_s: float = 24 * 3600.0, meta: dict | None = None) -> Scenario:
    """Turn an FFEG graph (ffe.ffeg.read arrays) into solver coefficients."""
    p = params
    off = arrays["in_offsets"].astype(np.int64)
    n = len(off) - 1
    src = arrays["src"].astype(np.int64)
    dst = np.repeat(np.arange(n), np.diff(off))
    gap = arrays["gap_m"].astype(float)
    facing = arrays["facing_m"].astype(float)
    bearing = arrays["bearing_rad"].astype(float)        # src -> dst, CCW from east
    h = arrays["height_m"].astype(float)
    area = arrays["area_m2"].astype(float)
    x, y = arrays["x_m"].astype(float), arrays["y_m"].astype(float)

    wx, wy = wind_to_vector(wind_speed_ms, wind_dir_from_deg)
    cos_t = np.cos(bearing) * wx + np.sin(bearing) * wy if wind_speed_ms > 0 else np.zeros_like(bearing)
    tan_a = min(wind_speed_ms / p.u_tilt_ms, p.tan_max)
    hf = p.flame_height_factor * h[src]
    d_eff = np.maximum(gap - hf * tan_a * cos_t, p.d_min_m)
    w = np.maximum(facing, 1.0)
    a = p.e_flame_kw_m2 * view_factor_parallel(w, hf, d_eff)

    d_c = np.hypot(x[dst] - x[src], y[dst] - y[src])
    ell = p.brand_ell0_m * (1 + wind_speed_ms / p.brand_u_ell_ms)
    b = (p.brand_beta_per_s * (area[src] / 100.0) * np.sqrt(area[dst] / 100.0)
         * np.exp(-d_c / ell) * ((1 + cos_t) / 2) ** 2)

    a = np.where(a >= p.a_min_kw_m2, a, 0.0)
    b = np.where(b >= p.b_min_per_s, b, 0.0)
    keep = (a > 0) | (b > 0)
    new_off = np.zeros(n + 1, dtype=np.int64)
    np.cumsum(np.bincount(dst[keep], minlength=n), out=new_off[1:])

    td = np.clip(p.td_ref_s * np.sqrt(area / 100.0), p.td_min_s, p.td_max_s)
    ones = np.ones(n)
    return Scenario(
        n=n, in_offsets=new_off, src=src[keep], a=a[keep], b=b[keep],
        tau0=p.tau0_s * ones, tg=p.tg_s * ones, td=td, tx=p.tx_s * ones,
        q_cr=p.q_cr_kw_m2, ftp_n=p.ftp_n, ftp_mu=float(np.log(p.ftp_median)), ftp_sigma=p.ftp_sigma,
        ignitions=np.asarray(ignitions, dtype=np.int64), t_end=float(t_end_s),
        meta={"params": p.to_dict(), "wind_speed_ms": wind_speed_ms, "wind_dir_from_deg": wind_dir_from_deg,
              **(meta or {})},
    )


def central_building(arrays: dict) -> int:
    """Index of the building nearest the zone origin (default ignition)."""
    x, y = arrays["x_m"].astype(float), arrays["y_m"].astype(float)
    return int(np.argmin(x * x + y * y))


def synthetic_scenario(n: int = 400, seed: int = 0, tau0_spread: float = 0.0, extent_m: float = 300.0) -> tuple[Scenario, dict]:
    """Random town for tests and engine fixtures: buildings uniform in a square,
    edges within 25 m with exponentially decaying coefficients. Returns the scenario
    and minimal graph arrays (positions, areas, heights) for the viewer."""
    r = np.random.default_rng(seed)
    x, y = r.uniform(0, extent_m, n), r.uniform(0, extent_m, n)
    d = np.hypot(x[:, None] - x[None], y[:, None] - y[None])
    dst, src = np.nonzero((d < 25) & (d > 0))
    order = np.lexsort((src, dst))
    dst, src = dst[order], src[order]
    off = np.zeros(n + 1, dtype=np.int64)
    np.cumsum(np.bincount(dst, minlength=n), out=off[1:])
    a = 60.0 * np.exp(-d[dst, src] / 6.0)
    b = 2e-4 * np.exp(-d[dst, src] / 8.0)
    tau0 = 300.0 * np.exp(tau0_spread * r.standard_normal(n))
    sc = Scenario(n=n, in_offsets=off, src=src.astype(np.int64), a=a, b=b,
                  tau0=tau0, tg=np.full(n, 300.0), td=r.uniform(900, 2400, n), tx=np.full(n, 1200.0),
                  q_cr=10.0, ftp_n=1.0, ftp_mu=float(np.log(1e4)), ftp_sigma=0.4,
                  ignitions=np.array([0, 1]), t_end=24 * 3600.0, meta={"synthetic_seed": seed})
    graph = {"x_m": x - extent_m / 2, "y_m": y - extent_m / 2, "area_m2": r.uniform(60, 200, n),
             "height_m": np.full(n, 6.0)}
    return sc, graph

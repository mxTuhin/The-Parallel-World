"""Faithful NumPy port (reproduces ResearchDirections.md §2) of FireSpreadJob.Execute / FireSpreadKernel for auditing.

States: 1=Empty, 2=Burning, 3=BurntOut. Wood.asset parameters, scene-level knobs
from FireSim.unity (GPU) / FireSimCPU.unity (CPU).
"""
import numpy as np

WOOD = dict(ign=5.0, absorb=2.0, spread=1.0, emit=5.0, cool=0.02, fuel=3.0)


def neighbour_sum(T, spread):
    """Sum of 8 neighbours' temperature*spreadMultiplier, zero outside the grid."""
    p = np.pad(T * spread, 1)
    h, w = T.shape
    s = np.zeros_like(T)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            if dx == 0 and dy == 0:
                continue
            s += p[1 + dy:1 + dy + h, 1 + dx:1 + dx + w]
    return s


def step(state, T, fuel, dt, diffusion, burn_rate, max_burn=20.0, m=WOOD):
    T = T + neighbour_sum(T, m["spread"]) * 0.125 * diffusion * m["absorb"] * dt
    burning = state == 2
    T = np.where(burning & (T < max_burn), T + m["emit"] * dt, T)
    fuel = np.where(burning, fuel - burn_rate * dt, fuel)
    out = burning & (fuel <= 0)
    state = np.where(out, 3, state)
    T = T - m["cool"] * T * dt
    T = np.maximum(T, 0.0)
    state = np.where((state == 1) & (T >= m["ign"]), 2, state)
    return state, T, fuel


def init(n):
    return (np.ones((n, n), np.int32), np.zeros((n, n)), np.full((n, n), WOOD["fuel"]))


def run(n, dt, t_end, diffusion, burn_rate, ignite=None, warm=None):
    state, T, fuel = init(n)
    c = n // 2
    if ignite:
        state[c, c] = 2
    if warm is not None:
        T[c, c] = warm
    total_heat = []
    for i in range(int(round(t_end / dt))):
        state, T, fuel = step(state, T, fuel, dt, diffusion, burn_rate)
        total_heat.append(T.sum())
    return state, T, np.array(total_heat)


if __name__ == "__main__":
    np.seterr(over="ignore", invalid="ignore")
    N = 151

    print("== 1. Conservation: one warm (NOT burning) cell, T=1 < ignition=5 ==")
    for diff in (0.75, 1.5, 5.0):
        st, T, H = run(N, 1 / 60, 20.0, diff, 0.5, ignite=False, warm=1.0)
        ignited = (st != 1).mean()
        print(f"  diffusionRate={diff:4}: total heat t=0 ->1.0, t=20s -> {H[-1]:.3g}; "
              f"fraction of grid ignited from a sub-ignition cell: {ignited:.2%}")
    # Analytic: uniform field obeys dT/dt = (diff*absorb - cool) T  -> exponential growth
    for diff in (0.75, 1.5, 5.0):
        print(f"  uniform-field growth rate lambda = {diff*WOOD['absorb'] - WOOD['cool']:.2f} 1/s "
              f"(pure Laplacian diffusion would give lambda = -cool = -0.02)")

    print("\n== 2. Frame-rate dependence (same sim-time, t=6 s, GPU scene diffusion=5) ==")
    for fps in (30, 60, 144):
        st, T, _ = run(N, 1 / fps, 6.0, 5.0, 0.5, ignite=True)
        print(f"  {fps:3d} fps: affected cells={int((st != 1).sum()):6d}  burning={int((st == 2).sum()):6d}"
              f"  maxT={np.nanmax(T):.3g}")

    print("\n== 3. Anisotropy of the front (diffusion=1.5, 60 fps, t=8 s) ==")
    st, T, _ = run(N, 1 / 60, 8.0, 1.5, 0.5, ignite=True)
    c = N // 2
    burnt = st != 1
    axis = max(k for k in range(c) if burnt[c, c + k])
    diag = max(k for k in range(c) if burnt[c + k, c + k])
    print(f"  axis reach={axis} cells, diagonal reach={diag} cells "
          f"-> diagonal/axis Euclidean ratio = {diag*np.sqrt(2)/max(axis,1):.3f} (isotropic = 1.0)")

    print("\n== 4. float32 (as in Unity): time until temperatures overflow to NaN ==")
    for diff, name in ((5.0, "GPU scene"), (1.5, "CPU scene")):
        st, T, f = init(N)
        T, f = T.astype(np.float32), f.astype(np.float32)
        st[N // 2, N // 2] = 2
        dt, first_nan = np.float32(1 / 60), None
        for i in range(60 * 40):
            st, T, f = step(st, T, f, dt, np.float32(diff), np.float32(0.5))
            T = T.astype(np.float32)
            if first_nan is None and np.isnan(T).any():
                first_nan = (i + 1) * float(dt)
        print(f"  {name} (diffusionRate={diff}): first NaN at t={first_nan:.1f} s simulated; "
              f"NaN cells at t=40 s: {int(np.isnan(T).sum())}/{N*N}")

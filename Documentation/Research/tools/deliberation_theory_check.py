"""Verifies the closed-form optimal deliberation time used in ResearchDirections.md §6.2.

Model: decision quality q(tau) = 1 - (1 - q0) * exp(-tau / kappa).
Hazard grows as H ~ exp(r t). A correct action contains it at H(t0 + tau); a wrong
action costs an extra D seconds of growth. Expected loss (normalised by H(t0)):
    L(tau) = e^{r tau} + (1 - q0) (e^{r D} - 1) e^{tau (r - 1/kappa)}
Closed form (Pi = kappa * r):
    tau* = kappa * ln[(1 - q0)(e^{rD} - 1)(1/Pi - 1)]   if Pi < 1 and the log argument > 1
    tau* = 0                                            otherwise (reflex regime)
"""
import numpy as np


def log_loss(tau, kappa, q0, r, D):
    # log-space form avoids 1 - q underflowing to 0 in float64
    log_g = r * D + np.log1p(-np.exp(-r * D))
    return np.logaddexp(r * tau, np.log(1 - q0) + log_g + tau * (r - 1 / kappa))


def tau_star(kappa, q0, r, D):
    pi = kappa * r
    if pi >= 1:
        return 0.0
    arg = (1 - q0) * np.expm1(r * D) * (1 / pi - 1)
    return kappa * np.log(arg) if arg > 1 else 0.0


if __name__ == "__main__":
    taus = np.linspace(0, 200, 400001)
    worst = 0.0
    for kappa in (1, 3, 10):
        for q0 in (0.2, 0.5, 0.8):
            for T in (0.5, 2, 5, 20, 60):
                for D in (5, 20, 60):
                    r = 1 / T
                    numeric = taus[np.argmin(log_loss(taus, kappa, q0, r, D))]
                    worst = max(worst, abs(numeric - tau_star(kappa, q0, r, D)))
    print(f"max |numeric argmin - closed form| over 135 cases: {worst:.5f} s (grid step 0.0005 s)")

    print("\nNFPA 72 t-squared fires, Q = alpha t^2, local growth rate r = 2/t:")
    for name, alpha in (("slow", 0.00293), ("medium", 0.01172), ("fast", 0.04689), ("ultrafast", 0.1876)):
        for q_det in (50, 100):
            t0 = np.sqrt(q_det / alpha)
            print(f"  {name:9s} detected at {q_det:3d} kW: fire age {t0:6.1f} s, "
                  f"e-folding time 1/r = {t0 / 2:5.1f} s -> reflex regime when kappa >= {t0 / 2:5.1f} s")

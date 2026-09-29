# Research Plan: Exact, Parallel and Reproducible Simulation of Building-to-Building Fire Spread

*Current direction (September 2026). It replaces the fire-following-earthquake plan (`ResearchPlan_FFE.md`, parked because it depends on ignition and pre-event building data that is hard to obtain). The data pipeline built for that plan is reused.*

**Working title:** *Fire without time steps: exact, parallel and reproducible simulation of building-to-building fire spread, and what time stepping hides in the tail.*

**In one paragraph.** Almost every building-to-building fire simulator (research models, WUI tools and game engines) advances in fixed time steps: every `dt` it adds `flux × dt` to each building's heat dose and makes a random draw. We reformulate the standard fire-spread physics (flux-time-product ignition from radiation, plus a Poisson process for firebrand ignition) so that all randomness is a vector of thresholds drawn once per building. The next ignition time of every building then has a closed form. On top of this we build:
1. an **exact event-driven solver**;
2. a **parallel commit rule** that stays exact because a newly ignited building cannot heat anyone during its incubation time `tau0` (a lookahead that comes from the physics);
3. **bit-identical results** for any thread count, batch size or commit rule, in Python, Burst CPU and GPU compute;
4. **rare-event estimation** of city-scale fire probabilities with subset simulation over the threshold vector.

Pilot results on real towns (Itoigawa, Wajima) already show that time stepping delays arrival times in proportion to `dt`, and underestimates the probability of large fires by more than it biases the average.

---

## 0. Status at a glance

| Piece | Where | Status |
|---|---|---|
| Building graphs of real towns (Wajima 4,313 / Itoigawa 3,011 / Eaton 18,942 / Tokyo-Nakano 136,560 buildings) | cloud, `ffe build` | ✅ built |
| Physics model M0 → scenario files (`.ffes`) | `ffe/sim/model.py`, `scenario.py` | ✅ |
| Exact solver (3 commit rules) + time-stepped baselines | `ffe/sim/exact.py` (numba) | ✅, 30 Python tests pass |
| Counter-based RNG (Philox4x32-10) identical in Python / C# / HLSL | `rng.py`, `Philox.cs` | ✅ passes the published known-answer vectors |
| Subset simulation | `ffe/sim/subset.py` | ✅ tested on a known tail |
| Unity: container reader, scenario, Burst CPU solver, GPU compute solver, viewer, batch/parity runner, EditMode tests | `Assets/Script/FireGraph/`, `Assets/Resources/FireGraph/ExactFire.compute`, `Assets/Tests/FireGraph/` | ✅ written. CPU path compiled and **run under Mono in the cloud (8/8 tests pass)**. GPU path and Burst: **local** |
| Python ↔ C# parity on real towns | `Tools/unity_check/check.sh parity …` | ✅ max relative time difference 2.6e-7, 0 outcome mismatches (3 towns) |
| Pilot E0–E4 (CPU) | `ffe sim verify/rq1/rq2/rq3` → `results/ExactFire_pilot.md` | ✅ (numbers in §9) |
| GPU parity, GPU scaling, GPU crude Monte Carlo ground truth, viewer | Unity on the local PC | ⏳ see `LOCAL_ENGINE_PLAN.md` |
| Physics sanity against full-scale tests | local / reading | ⏳ optional (E5) |

---

## 1. Why this direction

* **Data-light.** Only building footprints (Overture, open, already fetched) and literature physics ranges are needed. Real burn records are optional sanity checks, not the evidence.
* **Engine-level novelty.** The contribution is how the simulation is solved, which is what a game or simulation engine owns.
* **Who is helped:**
  * fire modellers (results that do not depend on `dt`, plus a measured bias to correct older studies);
  * risk and insurance analysts (cheap tail probabilities);
  * digital-twin and emergency-training developers (a fire that is reproducible and independent of frame rate);
  * game developers (a deterministic fire that multiplayer lockstep can replay from a seed).

---

## 2. Novelty triangulation: what was cancelled, what survives

Three independent search angles were used:
* **A:** the fire-spread literature itself;
* **B:** neighbouring fields that solved similar numerical problems (epidemics on networks, spiking neurons, parallel discrete-event simulation);
* **C:** risk and rare-event methods.

A claim survives only if no angle finds it already done.

| # | Candidate claim | A: fire literature | B: neighbouring fields | C: risk/rare events | Verdict |
|---|---|---|---|---|---|
| X1 | "Event-driven fire simulation is new" | DEVS / front-tracking wildfire simulators exist (Filippi, Innocenti et al.) | Next-reaction and exact network epidemics (NEXT-Net 2025, FastGEMF) | | ❌ **cancelled** |
| X2 | "Time-step artefacts in fire models are new" | FSJ 2026 firebrand paper notes that one draw per step is sensitive to `dt`. The CA literature notes `dt` effects | | | ❌ **cancelled** as a discovery. Survives only as a **quantification** for building-level radiation plus firebrand models, focused on tails (see C3) |
| X3 | "Lookahead-window parallel exact simulation is new" | | NEST uses the minimum synaptic delay as its communication window. Conservative PDES (Chandy–Misra, YAWNS). GPU DES kernels | | ❌ **cancelled** as a generic algorithm |
| X4 | "Counter-based RNG reproducibility is new" | | Random123 / Philox is standard | | ❌ **cancelled** (used as a tool, not claimed) |
| X5 | "GPU fire simulation is new" | GPU wildfire CA (Denham & Laneri), PyTorchFire | | | ❌ **cancelled** |
| C1 | **Building fire spread (flux-time-product radiation + Poisson firebrands + piecewise-linear burning curves) reformulated as a random-threshold, closed-form next-event system** | Not found. Urban and WUI models (Himoto–Tanaka, SWUIFT at 5-min steps, GIS models) are all time-stepped | Analogous to exact integrate-and-fire neuron simulation (Brette 2006/2007): acknowledged as inspiration | | ✅ **holds**, with the neuroscience analogy cited |
| C2 | **Physics-derived lookahead (incubation before external flaming) gives an exact, bit-reproducible GPU commit rule for fire; a start-value/slope segment form keeps it bit-identical** | Not found | The window idea is known (X3); the physics source of the lookahead and the fire-specific bit-identity argument are new | | ✅ **holds (narrowed)**: an application-specific algorithmic contribution |
| C3 | **Measured bias of time-stepped building fire models in arrival times and especially tail (conflagration) probabilities, against an exact reference** | Sensitivity analyses exist; no exact reference and no tail focus found | | | ✅ **holds** (pilot: §9) |
| C4 | **Subset simulation of city-scale fire probabilities over the threshold vector** | Monte Carlo burn probabilities are standard | | Splitting / large deviations used for spotting distance (FSJ 2022), not for conflagration probability. No urban-fire use found | ✅ **holds** (pilot pending in §9) |
| C5 | Game-engine deterministic, frame-rate-independent fire | Frostbite networked fire propagation thesis (time-stepped); game-engine determinism papers | | | ⚠️ **supporting only**: demo and engineering contribution, not a headline claim |

**Contribution statement after cancellation:** C1 + C2 are the method, C3 is the finding that motivates it, C4 is the payoff, and C5 is the engine demonstration.

---

## 3. Research questions and hypotheses

| RQ | Question | Hypotheses (pass/fail) | Pilot evidence |
|---|---|---|---|
| **RQ1** | How far do time-stepped building fire simulators drift from the exact solution at the step sizes used in practice? | **H1a:** mean arrival-time error grows about linearly in `dt`; ≥ 5% at `dt` = 60 s and ≥ 25% at 300 s (SWUIFT uses 5 min). **H1b:** the error in P(burned ≥ K) at the 1% tail is larger (relative) than the error in mean burned count. **H1c:** interpolating the ignition time inside the step halves the arrival error but does not remove the tail bias | Itoigawa/Wajima: 7–10% at 60 s, 32–114% at 300 s. Tail ratio 0.08–0.87 at 60 s, 0–0.27 at 300 s, while mean burned count changes −0.7% to −29%. Interpolation halves arrival error. **H1a–c supported in the pilot**; tail estimates need more runs (GPU) |
| **RQ2** | Can the exact solver run in parallel on CPU and GPU with bit-identical results, and is it faster than time stepping at equal accuracy? | **H2a:** Sequential, Global and Local commit rules and any replica batching give bit-identical ignition times within a backend. **H2b:** CPU (Burst) and GPU agree to float32 tolerance, and the burned set differs only at float32 near-ties. **H2c:** at ≥ 10^5 buildings with ≥ 64 replicas, the GPU is ≥ 10× faster than Burst CPU per run. **H2d:** exact is faster than any stepped configuration whose arrival error is ≤ 60 s | H2a: ✅ 180/180 Python runs + C# on 3 towns. Python↔C#: max rel diff 2.6e-7. H2d: ✅ CPU pilot (Itoigawa: exact 49 ms vs stepped 105 ms at 75 s error, 453 ms at 15 s error). H2b/H2c: **local GPU** |
| **RQ3** | Does the threshold formulation make city-scale fire probabilities of 10^-3 to 10^-5 affordable? | **H3a:** subset simulation matches crude Monte Carlo within its confidence interval. **H3b:** it needs ≥ 10× fewer runs for the same coefficient of variation at p ≈ 10^-3, with the gain growing as p shrinks | CPU pilot running (§9). Ground truth with 10^6–10^7 GPU runs: **local** |

---

## 4. Method

### 4.1 Physics model M0 (standard components, literature ranges)

Building `j` ignited at `t_j` has intensity `phi_j(t)`:
* 0 during incubation `tau0` (fire inside, no external flames);
* rises linearly to 1 over `tg`;
* stays at 1 for `td` (which scales with sqrt(floor area));
* decays to 0 over `tx`.

Building `i` receives:
* radiation `q_i = Σ a_ji phi_j`, where `a_ji` = emissive power × the view factor from a point to a parallel rectangle, with flame tilt from wind reducing the distance downwind;
* a firebrand ignition rate `λ_i = Σ b_ji phi_j`, from an exponential distance kernel stretched downwind.

Ignition happens at the first crossing of either:
* **radiation:** `∫ (q_i − q_cr)_+^n dt ≥ FTP_i` (flux-time product; log-normal `FTP_i`);
* **firebrands:** `∫ λ_i dt ≥ E_i ~ Exp(1)` (exact Poisson-process sampling by time change).

Defaults are in `ffe/sim/model.py`. Calibration against full-scale tests is E5. **The exactness claims hold for any parameter values.**

### 4.2 Exact node clock

Between breakpoints of the burning neighbours' `phi`, `q_i` and `λ_i` are linear, so:
* the dose is piecewise polynomial;
* the crossing time is a closed-form root: a stable quadratic for `n = 1`, and a power law for general `n`.

Segments are visited by a minimum scan, with no sort and no scratch memory, so the same code runs on GPUs. **Each segment uses only its start value and slope.** That is what keeps results bit-identical when a later neighbour's breakpoint splits a segment. The pilot found and fixed exactly this: before the fix, 1 run in 5 differed by a few ulps.

### 4.3 Commit rules and the lookahead lemma

Let `t_min` be the earliest prediction among unburned buildings.
* Any future ignition happens at or after `t_min`.
* A building ignited at `s` emits nothing before `s + tau0`.

Therefore building `i`'s clocks up to `t_min + min_{k ∈ unburned in-neighbours(i)} tau0_k` depend only on buildings already ignited. Any prediction below that bound is final.

Three rules:
* **Local** uses exactly that bound.
* **Global** uses `t_min + min_all tau0`.
* **Sequential** commits only `t_min`.

All three produce identical ignition times, and the windowed rules cut the number of synchronisation steps by ~10× in the pilot.

A known edge case: a new breakpoint landing within float rounding of a crossing could in principle change the last bit. It was not observed in any run. The paper should state it and report the observed mismatch rate.

### 4.4 Determinism and parity
* Every building's thresholds depend only on `(seed, replica, building)` via Philox.
* Sums run in fixed CSR order.
* Reductions are float `min`, which is order-independent.

So the result depends on nothing else. CPU vs GPU differ only by float32 hardware sqrt/division rounding; HLSL uses `precise` and Burst uses `FloatMode.Strict`.

### 4.5 GPU mapping

Dispatch `(ceil(N/64), R)`: x = building, y = replica. Each iteration is **Predict** (dirty buildings only), then **Reduce** (per-replica min with groupshared memory), then **Decide**, then **Apply**. Many iterations are recorded into one CommandBuffer, and finished replicas skip work. Replicas give the parallelism that a single fire front lacks.

### 4.6 Rare events

`z ∈ R^{2N}` holds standard normals, with `FTP = exp(mu + sigma z_1)` and `E = −ln Φ(z_2)`. Subset simulation (Au & Beck) uses preconditioned Crank–Nicolson moves (valid in any dimension). The score is buildings burned plus a [0, 1) tie-breaker (maximum threshold progress of any unburned building).

---

## 5. Experiment map

| ID | Question | Data | Runs where | Status | Output |
|---|---|---|---|---|---|
| E0 | Closed forms, commit-rule identity, Python↔C# parity | synthetic + 3 towns | cloud (Python, Mono) | ✅ | `tests/test_sim.py`, `Assets/Tests/FireGraph`, `*.parity.txt` |
| E0-GPU | GPU vs CPU parity, GPU rule identity | same | **local** Unity | ⏳ | `Tools > Fire Graph > Parity`, batch `Parity` |
| E1 | RQ1: arrival, mean and tail bias vs `dt` (2–300 s), 3 baseline variants, 3 winds | Itoigawa, Wajima | cloud pilot ✅; final tails with ≥ 10^5 GPU runs **local** | pilot ✅ | `rq1_*.json` |
| E2 | RQ2: CPU cost, exact vs stepped at matched accuracy | 3 towns | cloud ✅ | ✅ | `rq2_*.json` |
| E3 | RQ2: GPU scaling in N (3k → 136k → ~500k) and R (1 → 1024) | + Tokyo-Nakano, Tokyo-west | **local** | ⏳ | `unity_runs/*.json` |
| E4 | RQ3: subset simulation vs crude MC | Itoigawa "critical" regime | cloud pilot; GPU crude MC 10^6–10^7 **local** | pilot running | `rq3_*.json` |
| E5 | Physics sanity: calibrate `e_flame`, FTP against published full-scale separation tests; Itoigawa burned-area overlap | published tables; digitised burned area | local / manual | optional | new `ffe sim calibrate` (to write) |
| E6 | Engine demo: frame-rate independence (30/60/144 fps give identical state at time t), scrubbing | any scenario | **local** Unity viewer | ⏳ | video / screenshots |

---

## 6. Data we have (enough for the whole paper)

| Item | Source | Size | Status |
|---|---|---|---|
| Footprints and graphs: Wajima, Itoigawa, Eaton, Tokyo-Nakano | Overture (open) | 2–15 MB each | ✅ in `FFEData/` (git-ignored) |
| Tokyo-west large graph | Overture | > 90 MB, so fetch on the PC | ⏳ local |
| Physics parameters | SFPE handbook ranges; FTP/critical-flux literature; SWUIFT thresholds | tables | defaults in code; E5 refines |
| Wind | fixed scenarios (0 / 5 / 10 m/s); ERA5 is already fetched for real-event runs | — | ✅ |
| Full-scale test table (optional, E5) | Fire Technology 2025 wind-driven building-to-building tests (23 tests) | small | reading |

No ignition records, damage labels or ShakeMaps are needed.

---

## 7. Code map

```
Overture/ERA5/DEM ──ffe fetch/build──► graph.ffeg ──ffe sim compile──► scenario .ffes (+ reference thresholds, Python result)
                                                                         │
     Python (numba): ffe/sim/exact.py  ◄── same algorithm ──►  Unity: ExactFireCore.cs (Burst jobs, ExactFireCpu.cs)
                     ffe/sim/subset.py                                 ExactFire.compute (GPU, ExactFireGpu.cs)
                     ffe/sim/experiments.py                            FireGraphViewer + BuildingsInstanced.shader
                                                                       Editor/FireGraphBatch.cs (Parity, Benchmark)
     Tools/unity_check/check.sh: compiles the Unity C# on Mono with stubs and runs the EditMode tests (cloud)
```

---

## 8. Paper outline and target venues

1. Introduction: time stepping everywhere, and what it costs in the tails.
2. Model M0 (standard components).
3. Threshold reformulation and exact clock (C1).
4. Parallel exact commit rules, determinism, GPU mapping (C2).
5. Verification (E0).
6. How wrong is time stepping? (E1, C3)
7. Performance (E2, E3).
8. Rare conflagration probabilities (E4, C4).
9. Engine demonstration (E6, C5).
10. Limitations (model M0 not calibrated in this paper; near-tie edge case; constant wind per run).

Venues:
* **Primary:** Fire Safety Journal or Fire Technology, framed as a numerical method plus bias finding.
* **Alternatives:** Environmental Modelling & Software; ACM TOMACS / SIGSIM-PADS for a GPU-algorithm-only version.
* **Short demo paper:** ACM MIG / I3D for the engine track.

---

## 9. Pilot results (CPU, cloud)

Full tables: `results/ExactFire_pilot.md` (regenerate with `python -m ffe sim report <zones>`).

* **E0:** all three commit rules bit-identical in 180/180 runs (2 towns × 3 winds × 30 seeds); windows need 7–14× fewer iterations than sequential. Unity C# CPU (float32) vs Python (float64): max relative time difference 1.2–2.6e-7 on 3 towns, 0 outcome mismatches.
* **E1 (Itoigawa and Wajima, 40 paired runs, identical thresholds):**

  | Step | Mean arrival-time error |
  |---|---|
  | 2 s | 0.3% |
  | 10 s | 1.2–1.4% |
  | 60 s | 7–10% |
  | 300 s | 32–114% |

  Interpolating the ignition time inside the step roughly halves these errors. The 1% tail probability at 60 s comes out 0.08–0.87 of the exact value, and at 300 s 0–0.27, while the mean burned count moves less.
* **E2:**
  * Itoigawa: exact 49 ms/run on one core, vs time-stepped 105 ms at 75 s mean error or 453 ms at 15 s.
  * Wajima and Eaton: see the results file.
* **E4:** see the results file once the run completes.

---

## 10. Risks and reviewer questions

| Risk / question | Answer or mitigation |
|---|---|
| "The physics model is simplistic / uncalibrated." | The claims are about solution accuracy and cost for a model class that includes published urban/WUI models. E5 adds calibration against full-scale tests. The bias results are shown across parameter variants |
| "Event-driven simulation is old." | Yes (§2 X1). Our claims are C1–C4, with neuroscience and epidemic analogues cited |
| "Time-stepped models with small `dt` are fine." | E1/E2: small `dt` costs more than exact, and practical tools use 1–5 min steps |
| Tail estimates from 1,000 runs are noisy | Final E1/E4 use GPU crude Monte Carlo with 10^5–10^7 runs |
| Near-tie bit mismatches | Report the measured rate. Parity tolerance is float32 |
| Subset simulation can get stuck on multimodal tails | Report acceptance rates and repeat runs. Crude GPU MC gives the ground truth |
| Constant wind per run | Hourly wind epochs are only extra breakpoints in the same algorithm (future work) |

---

## 11. Sources (checked September 2026)

* Firebrand ignition in landscape-scale fire spread models (FSJ 2026; time-resolution sensitivity): https://www.sciencedirect.com/science/article/pii/S0379711226001608
* Firebrand-driven fire spread in WUI and urban conflagration models (FSJ 2026): https://www.sciencedirect.com/science/article/pii/S0379711226000548
* Discrete-event front-tracking simulation of a physical fire-spread model: https://www.researchgate.net/publication/228620655_Discrete_Event_Front-tracking_Simulation_of_a_Physical_Fire-spread_Model
* SWUIFT / Masoudvaziri et al. (5-min steps, 14 kW/m² critical flux): https://www.frames.gov/catalog/63759
* Himoto & Tanaka physics-based urban fire spread model: https://www.sciencedirect.com/science/article/abs/pii/S0379711207001257
* Flux-time product ignition model: https://link.springer.com/article/10.1007/s10694-023-01399-3
* Exact simulation of integrate-and-fire models (Brette 2006): https://direct.mit.edu/neco/article/18/8/2004/7067/Exact-Simulation-of-Integrate-and-Fire-Models-with
* NEST precise spike times and the minimum-delay communication interval: https://www.frontiersin.org/journals/neuroinformatics/articles/10.3389/fninf.2010.00113/full
* NEXT-Net exact epidemics on networks (PLOS CB 2025): https://journals.plos.org/ploscompbiol/article?id=10.1371%2Fjournal.pcbi.1013490
* FastGEMF: https://arxiv.org/pdf/2410.16625
* GPU discrete-event simulation kernel: https://journals.sagepub.com/doi/abs/10.1177/0037549713508839
* GPU stochastic fire propagation (Denham & Laneri): https://arxiv.org/abs/1701.03549
* PyTorchFire: https://www.sciencedirect.com/science/article/pii/S1364815225000854
* Subset simulation introduction (Au & Beck; Zuev): https://arxiv.org/pdf/1505.03506
* Splitting for rare-event simulation: https://www.informs-sim.org/wsc06papers/014.pdf
* Rare events in spotting (large deviations, importance sampling): https://www.sciencedirect.com/science/article/abs/pii/S0379711222001084
* Wind-driven building-to-building fire spread tests (Fire Technology 2025): https://link.springer.com/article/10.1007/s10694-025-01854-3
* Random123 / Philox: https://numpy.org/doc/stable/reference/random/bit_generators/philox.html
* Networked real-time fire propagation in Frostbite: https://www.diva-portal.org/smash/get/diva2:1694444/FULLTEXT01.pdf
* Determinism of game engines for simulation: https://arxiv.org/abs/2104.06262

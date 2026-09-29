# The Parallel World: Research Directions Study

> **Superseded:** the LLM/VLM direction below was set aside in favour of `Direction_FireFollowingEarthquake.md`. The physics audit in §2 remains valid and applies to both.

*Codebase audit, literature triangulation (state of the art as of late September 2026), scoring matrix, and one recommended research program.*

---

## 0. TL;DR

1. **What exists today** is a well-engineered real-time **2D thermal cellular automaton (CA)** for fire, implemented twice (Burst `IJobParallelFor` on CPU and an HLSL compute kernel on GPU), plus a shooter layer and a profiling harness. The strongest asset is **speed**: 25 M cells in about 137 ms on 8 cores, and faster on GPU.
2. **The physics is not publishable as it stands.** The "diffusion" term only adds heat and never subtracts it, so energy is created from nothing. The simulation also depends on frame rate, and in float32 it overflows to NaN within about 10 s of simulated time on the GPU-scene parameters (§2, all reproducible with `tools/ca_audit.py`).
3. **Most of the obvious "add an LLM/VLM" ideas are already taken.** Open-loop VLM fire question answering is covered by *FireWorldBench* (arXiv, Sep 2026). Closed-loop LLM agents facing fire are covered by *HAZARD* (ICLR 2024). Fire world models are covered by *PhysFire-WM* (Dec 2025). Latency-coupled agent benchmarks are covered by *Real-Time Reasoning Agents* (ICLR 2026), *Win Fast or Lose Slow* (NeurIPS 2025), GPTNT and OmniGameArena. Differentiable fire CAs are covered by *PyTorchFire* and Neural-Parameterized CA (2026). LLM evacuation agents already have several 2025–26 papers.
4. **The gap that survived triangulation:** no work links **how long an AI agent should think** to **the growth law of the physical hazard it faces**. I derived a closed form (checked numerically) that gives a **phase boundary**: when an agent's reasoning time constant κ exceeds the hazard's e-folding time 1/r (dimensionless **Π = κ·r ≥ 1**), extra deliberation always hurts. Applied to the NFPA 72 fire-growth classes, an ultrafast fire detected at 50 kW has an e-folding time of only **≈8 s**, which is shorter than a typical reasoning-model response time. **Recommended paper:** *"Thinking While It Burns: a growth-law theory of deliberation for AI agents facing spreading hazards"*. It combines theory, a controlled real-time simulator (this repo, fixed), a hazard-aware compute governor, and human baselines from the playable layer. Target: NeurIPS / ICLR main track.
5. Secondary tracks: **(B)** pre-fire photo to predicted heat-release-rate curve with VLMs, validated on the NIST Fire Calorimetry Database; **(C)** LLM-guided adversarial search for design-fire scenarios with a multi-fidelity simulator, targeting fire-engineering journals.

---

## 1. What the project is (honest assessment)

| Component | Files | What it does | Research value |
|---|---|---|---|
| Fire CA core | `Assets/Script/FireSpreadJob.cs`, `Assets/Shader/FireHeatmap.compute` (`FireSpreadKernel`) | 3-state CA (Empty / Burning / BurntOut), 8-neighbour heat gathering, per-material ignition / absorption / emission / cooling / fuel | Fast, but the physics is ad hoc (§2) |
| CPU backend | `FireSimulationController.cs` | Burst job, runtime worker-thread control, CPU heatmap | Good systems engineering |
| GPU backend | `FireSimulationControllerGPUCompute.cs` | Double-buffered compute; `AppendStructuredBuffer` compaction of burning cells; `AsyncGPUReadback` shadow grid (16-frame staleness) | Good systems engineering; the readback delay is effectively *observation latency* (useful later, §6) |
| Shared contract | `IFireSimulation.cs` | Backend-agnostic query/ignite API | A good seam for a Python/agent bridge |
| Benchmarking | `SimulationBenchmark.cs`, `Documentation/JobProfilingComparison.md` | Warmup/record state machine, CSV export, core-scaling (1 core → 8 cores: 818 → 137 ms at 25 M cells, ~75% efficiency) | Solid, but core-scaling of a stencil code is not novel research |
| Game layer | `Player*`, `Enemy*`, `Bullet*` | Third-person shooter; fire damages the player | Useful later as a **human-subject interface** (§6.5) |
| Materials | `Assets/Data/{Wood,Plastic,Misc}.asset` | All three share ignition=5, emission=5, cooling=0.02, fuel=3; only `heatAbsorption` differs (2 / 1 / 1.5) | Materials are effectively identical and in arbitrary units |

**Bottom line:** the repository is an engineering prototype: a fast stencil engine with a demo. A publishable contribution has to come from a scientific question that this fast, controllable, interactive engine is well placed to answer. Doing the same thing faster is not enough.

---

## 2. Physics audit (reproducible)

I ported `FireSpreadJob.Execute` line by line to NumPy (`tools/ca_audit.py`). The port uses Wood parameters and scene values read from `FireSim.unity` (GPU, `diffusionRate: 5`) and `FireSimCPU.unity` (CPU, `diffusionRate: 1.5`). It is a port and was not run inside Unity.

| # | Finding | Evidence | Why it matters |
|---|---|---|---|
| 1 | **Non-conservative "diffusion".** `FireSpreadJob.cs:40` / `FireHeatmap.compute:216` add `mean(neighbour T) × rate × absorption × dt` but never subtract the cell's own temperature, so this is *heat injection*, not a Laplacian. A uniform field grows as `exp((diffusionRate·absorption − cooling)·t)`, i.e. at +1.48, +2.98 or +9.98 s⁻¹ for the three scene values. A pure diffusion would decay at −0.02 s⁻¹. | A single **non-burning** cell at T=1 (below ignition=5) ignites 17% / 71% / **100%** of the grid within 20 s. Total heat goes from 1 to 1.6×10⁸⁰. | Fire spread is driven by numerical blow-up, not by fuel or material properties. No physical claim can rest on this. |
| 2 | **Frame-rate dependence.** The CPU path uses raw `Time.deltaTime` (`FireSimulationController.cs:234`). The GPU path clamps it but still steps once per frame with a variable dt. | Same simulated time (6 s): 10,149 / 12,997 / 15,205 cells affected at 30 / 60 / 144 fps (+50%). | Results are not reproducible across machines. Experiments need fixed, deterministic steps. |
| 3 | **float32 overflow.** Temperatures grow exponentially, pass 3.4×10³⁸ (float32 maximum), become ∞, and `∞ − ∞` produces NaN. | First NaN at **10.1 s** (GPU-scene parameters) and **31.7 s** (CPU-scene parameters). The whole grid is NaN by 40 s. | What happens next depends on how `max()` treats NaN. That behaviour is not guaranteed to match between Burst and HLSL (D3D follows IEEE maxNum and returns the non-NaN operand), so the CPU and GPU runs can silently diverge. |
| 4 | **CPU and GPU scenes run different physics.** `diffusionRate` is 1.5 (CPU scene), 5 (GPU scene) and 0.75 (`GPU-Compute.prefab`). `maxBurningTemperature` is hard-coded to `20f` on CPU and serialized on GPU. | Scene YAML. | The CPU-vs-GPU comparison mixes a physics change with a hardware change. |
| 5 | Burnt-out cells keep absorbing heat without bound, so the "cooling ember" visual state depends on runaway temperatures. | Follows from #1. | Visual states are not meaningful. |
| ✓ | **Front isotropy is fine.** The 8-neighbour equal-weight stencil gives a diagonal-to-axis reach ratio of 1.018. | `ca_audit.py` §3. | I checked this and it is *not* a problem. |

**Fix (Phase 0, §9):** use a conservative stencil (Σ wₖ(Tₖ − T)), a fixed timestep with sub-stepping under a CFL/stability bound, Arrhenius-type or threshold ignition with explicit heat release per unit area (HRRPUA), SI units, materials calibrated from published tables (FSRI Materials & Products DB, SFPE handbook), and a CPU/GPU parity test.

---

## 3. Search plan (topics × angles)

The goal was to cancel out ideas that are already done by checking each candidate from three independent angles:

* **Angle 1, prior-art saturation:** has this been published or preprinted, especially in 2025–26?
* **Angle 2, repo leverage:** does this repo's specific strength (real-time GPU CA, interactivity, game layer) give an advantage?
* **Angle 3, validation path:** can the result be checked against real data, a theory, or humans, so that it is more than "our sim says so"?

Topics searched: embodied hazard benchmarks; VLM fire QA and forecasting; LLM evacuation agents; video and world-model physics benchmarks; fire-specific world models; differentiable and neural CA for fire; FDS surrogates and fire digital twins; robot-simulator fire augmentation; UAV/VLM wildfire agents; latency-aware and real-time agent benchmarks; speed–accuracy trade-offs in LLM agents; physical-property inference from images (NeRF2Physics lineage); heat-release-rate prediction from images; fire-load recognition; LLMs in performance-based fire design; embodied safety benchmarks; NFPA t² fire growth.

---

## 4. Prior-art map (what is already taken)

| Area | Closest work (2024–2026) | What it covers | Verdict for us |
|---|---|---|---|
| VLM fire QA / forecasting (open-loop) | **FireWorldBench** (arXiv 2609.23064, Sep 2026): 520 fire worlds, 47 archetypes, 9,074 QA; coupled fields (temperature, soot, visibility, CO, velocity); counterfactual intervention questions | Physical-state inference, mechanisms, forecasting, intervention *reasoning* from partial observations | **Killed.** Do not build "VLM answers questions about fire". |
| Closed-loop LLM agents in hazards | **HAZARD** (ICLR 2024, ThreeDWorld): fire/flood/wind, object rescue, LLM decision pipeline | Turn-based, object-level temperature model | **Mostly killed.** Gap: turn-based and not physically calibrated. |
| Latency-coupled agents | **Real-Time Reasoning Agents / AgileThinker** (ICLR 2026); **Win Fast or Lose Slow** (NeurIPS 2025); **GPTNT** (wall-clock bomb timer); **OmniGameArena** (latency-injected real time) | The environment does not pause while the model thinks; games and trading | **Killed as a generic benchmark.** Gap: none of them tie latency to a *physical growth law* or give a predictive theory. |
| Fire world models | **PhysFire-WM** (Tsinghua, Dec 2025): physics-informed world model on infrared + mask video | Wildfire video forecasting | **Killed.** |
| Physics of video world models | Physics-IQ (ICCV 2025), CRONOS, PhyGround, Principia (2026) | General physics probing | Crowded. |
| Differentiable / neural CA for fire | **PyTorchFire** (EMS 2025); **Neural-Parameterized CA** (2026, JAX, IoU > 0.6 at 72 h) | Wildfire parameter calibration | **Killed.** |
| Robot simulators with fire | **Fire as a Service** (2026): Fire-X GPU solver validated against FDS, co-simulated with ROS | Firefighting-robot training | **Killed** (and higher fidelity than this repo). |
| VLM + RL wildfire UAV | **FIRE-VLM** (2026) | UAV tracking in a wildfire digital twin | Taken. |
| LLM evacuation agents | Safety Science 2025 (LLM agents + CA mall fire); *LLM-Driven Personalities* (2026, **Unity** + ZeroMQ); *Guide Me Out* (2026) | Human-like evacuee behaviour | Crowded. |
| Building fire digital twin / FDS surrogates | PolyU (Huang) AIoT digital twin (AEI 2025); NIST ML forecasting; GenAI fire scenario images (FSJ 2025) | Real-time temperature/smoke forecast | Crowded, and the domain community has an FDS data advantage. |
| Physical properties from images | NeRF2Physics (CVPR 2024), PUGS, PhysVGGT (2026) | Mass, stiffness, friction, thermal conductivity; **not flammability or HRR** | **Partially open** (→ Track B). |
| HRR from images | NIST IR 8521 (HRR from *fire video*); CNN THR from item images (FSJ 2025); fire-load recognition (Mask R-CNN) | Mostly during-fire video; one pre-fire total-heat-release CNN | **Partially open:** time-resolved, uncertainty-aware, VLM + retrieval, room-level composition. |
| Embodied safety | SafeAgentBench, IS-Bench, AGENTSAFE (CVPR 2026) | Refusal and risk-mitigation ordering; static hazards | Not about dynamic physical growth. |

---

## 5. Triangulation and scoring matrix

Scores run from 1 (bad) to 5 (good). "Scoop safety" means 5 = low risk of being scooped. "Effort" means 5 = least effort.

| # | Candidate direction | Novelty after 2026 prior art | Scientific depth | Repo leverage | Validation path | Venue ceiling | Scoop safety | Effort | **Total /35** | Verdict |
|---|---|---|---|---|---|---|---|---|---|---|
| **A** | **Growth-law theory of deliberation + hazard-aware compute governor** (VLM agents, real-time hazard) | 5 | 4 | 5 | 4 (theory + controlled sim + humans + NFPA anchoring) | 5 (NeurIPS/ICLR) | 4 | 3 | **30** | **PRIMARY** |
| B | Pre-fire photo → HRR curve with VLM + retrieval (NIST FCD, FSRI DB); also the scene-to-sim compiler | 3 | 3 | 2 | 5 (real calorimetry) | 4 (CVPR/ICCV or FSJ) | 3 | 3 | 23 | Secondary |
| C | LLM-guided adversarial design-fire discovery with multi-fidelity sim (fast CA → FDS confirm) | 4 | 3 | 3 | 4 (FDS) | 3 (FSJ / Fire Technology; top of that domain) | 4 | 2 | 23 | Tertiary |
| D | VLM fire QA benchmark | 1 | 2 | 3 | 3 | 3 | 1 | 4 | 17 | Killed (FireWorldBench) |
| E | Closed-loop hazard rescue benchmark | 1 | 2 | 4 | 2 | 3 | 2 | 3 | 17 | Killed (HAZARD) |
| F | Generic latency-coupled agent benchmark | 1 | 2 | 4 | 2 | 3 | 1 | 4 | 17 | Killed (ICLR'26, NeurIPS'25, GPTNT, OmniGameArena) |
| G | Differentiable / neural CA for wildfire | 1 | 3 | 2 | 4 | 3 | 1 | 3 | 17 | Killed |
| H | Fire-video world model | 1 | 3 | 1 | 3 | 4 | 1 | 1 | 14 | Killed (PhysFire-WM) |
| I | LLM evacuation agents | 2 | 2 | 4 | 2 | 2 | 2 | 4 | 18 | Crowded |
| J | Real-time FDS surrogate / digital twin | 2 | 3 | 2 | 4 | 3 | 2 | 2 | 18 | Crowded; no data advantage |

**Three-angle check for A:**
* **Angle 1:** no hit for deliberation time tied to a hazard growth law. Latency-coupled works are empirical and game-specific. The classical *value of computation* and metareasoning literature (Russell & Wefald; Horvitz; anytime algorithms; Lieder & Griffiths' resource-rationality) supplies the concept but not this instantiation, its predictions for modern reasoning models, or the benchmark-bias consequence.
* **Angle 2:** A needs a hazard whose growth law you can dial precisely and cheaply, running in real time and in thousands of counterfactual rollouts. That is exactly this repo's strength, once fixed.
* **Angle 3:** A has three independent checks: closed-form predictions (falsifiable), NFPA α classes (real-world anchoring), and humans playing through the same interface.

---

## 6. Primary proposal: "Thinking While It Burns"

### 6.1 Question

Reasoning models get better when they think longer, while physical hazards keep growing during that thinking time. **When is extra thinking worth it, and can we predict the answer from the hazard's physics and the agent's measured reasoning curve?** A corollary: **do turn-based benchmarks, where the world pauses while the model thinks, systematically over-rank slow thinking models?**

### 6.2 Theory (derived and numerically verified in `tools/deliberation_theory_check.py`)

* The hazard size is H(t) and its instantaneous log-growth rate is r = d ln H/dt.
* Decision quality after deliberating for τ is q(τ) = 1 − (1 − q₀)·e^(−τ/κ). Here κ is the agent's **reasoning time constant** and is measurable per model and per thinking budget.
* A wrong action costs an extra D seconds of uncontained growth (the error-recovery time).
* Expected loss relative to H(t₀): L(τ) = e^(rτ) + (1 − q₀)(e^(rD) − 1)·e^(τ(r − 1/κ)).

This loss is convex in τ. Defining the **deliberation number Π = κ·r**:

> **τ\* = κ · ln[ (1 − q₀)(e^(rD) − 1)(1/Π − 1) ]** when Π < 1 and the log argument > 1; **τ\* = 0 (reflex regime)** otherwise.

The numerical argmin matches the closed form to within 0.00025 s (the grid step) across 135 parameter combinations.

**Real-world anchor.** A t² design fire Q = αt² has r = 2/t. If it is detected at Q_det, its age is t₀ = √(Q_det/α), so **Π = 2κ·√(α/Q_det)**. Using NFPA 72 α values:

| Growth class (α, kW/s²) | Detected at 50 kW: e-folding time 1/r | Detected at 100 kW: e-folding time 1/r |
|---|---|---|
| Slow (0.00293) | 65 s | 92 s |
| Medium (0.01172) | 33 s | 46 s |
| Fast (0.04689) | 16 s | 23 s |
| **Ultrafast (0.1876)** | **8 s** | **12 s** |

A reasoning VLM whose useful deliberation constant is 10–30 s is already in the **reflex regime (Π ≥ 1)** for fast and ultrafast fires. For those fires, "thinking harder" is predicted to be counterproductive. This is a headline-worthy, falsifiable claim.

**Extensions for the paper:**
* Sequential decisions (a dynamic program over repeated think/act cycles).
* Observation delay δ. The GPU shadow grid's 16-frame readback is one example; δ adds to the effective deliberation time.
* Action execution time.
* Stochastic q.
* Power-law versus exponential versus saturating (fuel-limited) regimes. For t² growth, Π *falls* as the fire ages, which predicts "reflex early, deliberate later".

### 6.3 Hypotheses

* **H1 (data collapse).** Empirical optimal budgets from many models, hazard regimes and growth rates collapse onto one master curve τ\*/κ = f(Π, q₀, rD).
* **H2 (phase boundary).** Above Π ≈ 1, the best policy is the fastest model or the minimum budget, regardless of hazard type.
* **H3 (benchmark bias).** The rank correlation (Kendall τ) between turn-based and real-time leaderboards decreases monotonically with Π, and the leaderboard inverts above Π ≈ 1.
* **H4 (governor).** A controller that estimates r online from observations (burning-area or thermal growth) and picks the budget or model from the closed form approaches oracle regret. It beats fixed budgets and the dual-thread AgileThinker baseline.
* **H5 (humans).** Humans in the playable layer adapt deliberation to Π, measured as decision latency against growth rate. Whether they do so better or worse than models is interesting either way.

### 6.4 System (built on this repo)

1. **Phase 0 physics fix (§9).** Conservative, deterministic, fixed-step, SI-unit CA with a **growth-regime controller** so that r(t) can be dialled exactly: constant-speed front (t²), exponential, and fuel-limited.
2. **Three time-coupling modes** behind `IFireSimulation`:
   * *turn-based* (the world pauses);
   * *latency-injected* (the world pauses, then the measured latency is replayed; reproducible);
   * *asynchronous wall-clock*.

   Also report latency in tokens, as a hardware-agnostic unit, as ICLR'26 does.
3. **Agent bridge.** Python over gRPC/ZeroMQ, or ML-Agents, running headless and in batch.
   * Observations: RGB, thermal, top-down map, and a text state.
   * Actions: move, close door, deploy extinguisher or firebreak, evacuate an NPC, call for help.
4. **Task families.** Containment (choose where to cut a firebreak), egress (route a group out), and triage (which room first). Each has a ground-truth oracle computed by exhaustive counterfactual rollouts. This is only feasible because the engine is fast, so the existing Burst/GPU work becomes an enabling contribution.
5. **Optional module: simulation as imagination.** The agent can call a *misspecified* fast CA to roll out candidate actions (in the lineage of Mind's Eye, ICLR 2023). The research question is when a fast, wrong simulator beats slow, right-ish reasoning, and Π predicts that as well.

### 6.5 Experiments

| ID | Purpose | Protocol | Key figure |
|---|---|---|---|
| E1 | Measure q(τ) per model | Frozen-world mode, 5–7 thinking budgets × tasks; fit q₀ and κ (seconds and tokens) | Reasoning curves |
| E2 | Measure hazard r | Sweep regime × growth parameter; record H(t) | Growth-law validation |
| E3 | Test H1 and H2 | Real-time mode, sweep budgets, locate the empirical τ\* | **Master-curve collapse vs Π** |
| E4 | Test H3 | Same agents, turn-based vs real-time | Kendall τ vs Π; leaderboard inversion |
| E5 | Test H4 | Governor vs fixed budgets, always-fast, always-slow, AgileThinker-style dual thread, oracle | Regret vs Π |
| E6 | Test H5 | 20–40 participants via the playable layer (needs ethics/IRB approval) | Human latency vs Π, compared with models |
| E7 | Robustness | Smoke occlusion, observation delay δ, simulator misspecification | Shift of the phase boundary |

**Models:** 2–3 open-weight VLMs with controllable thinking budgets, plus 2–3 frontier API models, plus a small fast model for routing experiments. Report cost.

### 6.6 Risks and mitigations

| Risk | Mitigation |
|---|---|
| "The theory is obvious (value of computation)." | Frame it as instantiation plus empirical law. The novelty is the **collapse**, the **NFPA-anchored regime map**, and the **benchmark-bias result**, all measured on modern reasoning models. |
| "Toy physics." | Phase 0 calibration. Show that the growth law, not the renderer, drives results. Optionally reproduce one E3 cell with FDS-precomputed fields. |
| Scoop by the real-time-agents community | Move fast. Release a short theory preprint plus E3 early. |
| API variance and cost | Latency-injected mode and token-time make runs reproducible. Cache observations. |
| Human study logistics | Keep E6 optional; the paper stands without it. |

**Venues:** NeurIPS / ICLR (main track, agents and reasoning). A strong E6 could also make a CogSci companion paper. The NFPA-regime analysis could make a short Fire Safety Journal note.

---

## 7. Secondary track B: "Burn Before It Burns" (pre-fire images → fire behaviour)

* **Task.** From photos of an item or room before any fire, predict the **time-resolved HRR curve** (peak, time to peak, total heat) with uncertainty. The pipeline combines VLM material reasoning, retrieval over fire-property tables (FSRI Materials & Products DB, SFPE), and composition across a room.
* **Data.** The **NIST Fire Calorimetry Database** (public; photos, videos and HRR CSVs from single items up to furnished rooms), plus the FSRI DB.
* **Novelty versus prior art.** NeRF2Physics and PhysVGGT do not cover combustion properties. NIST IR 8521 predicts HRR from *fire video*. The FSJ 2025 CNN predicts only *total* heat release. Track B adds a curve, uncertainty, VLM + retrieval, and zero-shot generalisation.
* **Synergy.** Track B is the **scene-to-simulator compiler** that turns a photo into calibrated CA material parameters, feeding Track A with realistic hazard growth rates.
* **Venue:** CVPR/ICCV (physical scene understanding) if the results are strong, otherwise Fire Safety Journal.

## 8. Tertiary track C: adversarial design-fire discovery

In performance-based fire design, engineers hand-pick "credible but conservative" design fires. Track C uses an LLM proposer (ignition location, fuel arrangement, door states), a fast fixed CA as the screening surrogate, and FDS to confirm the top-k. The target is to find worst-credible scenarios that engineers miss, measured as margin to untenability. **Venue:** Fire Safety Journal / Fire Technology. This is lower ML glamour but has real practical value.

---

## 9. Phase 0: concrete engineering changes to this repo

| Change | Where | Why |
|---|---|---|
| Conservative stencil: `T += dt·D·Σ wₖ(Tₖ − T)` | `FireSpreadJob.cs:38-45,85-93`; `FireHeatmap.compute:204-216` | Fixes the energy blow-up (§2 #1, #3) |
| Separate *heat release* (HRRPUA × burning) from *transport*; cap temperature physically, not with `maxBurningTemperature` | Same | Materials start to matter |
| Fixed dt + sub-steps with a stability bound; accumulator loop | `FireSimulationController.cs:220-240`; GPU `Update` | Determinism (§2 #2) |
| A single `SimulationParams` ScriptableObject shared by both backends | Scenes and prefab | Removes the 0.75 / 1.5 / 5 mismatch (§2 #4) |
| CPU↔GPU parity test (N steps, max-abs-diff threshold) and NaN guard | `Assets/Tests` (Unity Test Framework is already in the manifest) | Correctness |
| Calibrated material table (SI units) | `Assets/Data/*.asset` | Credibility |
| Growth-regime controller and H(t) logger | New | Track A's independent variable |
| Headless batch mode and Python bridge; three time-coupling modes | New, behind `IFireSimulation` | Agent experiments |
| Validate against analytic heat-kernel spreading and front speed; optionally one FDS case | `tools/` | Reviewer defence |

## 10. Roadmap (relative)

| Months | Work |
|---|---|
| 0–1 | Phase 0 fixes, parity tests, growth-regime controller, Python bridge |
| 1–2 | E1 and E2; freeze task families and oracles |
| 2–3 | E3 and E4 (the core results); write the theory section; preprint |
| 3–4 | E5 governor, E7 robustness; optionally E6 humans |
| 4–5 | Submit A. Start B using NIST FCD as the scene compiler. |

## 11. Reading list (in priority order)

1. HAZARD Challenge (ICLR 2024). The closest closed-loop ancestor.
2. Real-Time Reasoning Agents in Evolving Environments (ICLR 2026). *Win Fast or Lose Slow* (NeurIPS 2025).
3. FireWorldBench (arXiv 2609.23064). Know exactly what it covers.
4. GPTNT; OmniGameArena (latency-injected protocol).
5. Russell & Wefald, *Do the Right Thing* (value of computation); Lieder & Griffiths, resource-rational analysis.
6. Mind's Eye (ICLR 2023). Simulator as a reasoning tool.
7. Fire as a Service / Fire-X (2026). The fidelity bar for fire in robot simulators.
8. NFPA 72 t² fire classes; SFPE Handbook chapters on design fires and HRR.
9. NIST FCD, NIST IR 8521, FSRI Materials & Products DB (Track B).
10. PyTorchFire; Neural-Parameterized CA (differentiable-CA baselines, if needed).

## 12. Sources

* HAZARD: https://arxiv.org/abs/2401.12975 · https://vis-www.cs.umass.edu/hazard/
* FireWorldBench: https://arxiv.org/abs/2609.23064 · https://fireworldbench.github.io/
* PhysFire-WM: https://arxiv.org/abs/2512.17152
* Real-Time Reasoning Agents (ICLR 2026): https://arxiv.org/abs/2511.04898
* Win Fast or Lose Slow (NeurIPS 2025): https://arxiv.org/pdf/2505.19481
* GPTNT: https://arxiv.org/html/2606.28514v2
* OmniGameArena: https://arxiv.org/pdf/2606.09826
* Fire as a Service: https://arxiv.org/abs/2603.19063
* FIRE-VLM: https://ui.adsabs.harvard.edu/abs/2026arXiv260103449W/abstract
* PyTorchFire: https://arxiv.org/pdf/2502.18738
* Neural-Parameterized CA for Wildfire: https://arxiv.org/abs/2606.11676
* LLM agents for fire evacuation in CA (Safety Science 2025): https://www.sciencedirect.com/science/article/abs/pii/S0925753525001602
* LLM-Driven Personalities in Emergency Simulations: https://arxiv.org/html/2606.31038v1
* Guide Me Out: https://arxiv.org/abs/2606.09428
* Physics-IQ: https://insait.ai/new-benchmark-physics-iq-challenges-ai-video-models-understanding-of-the-physical-world/
* NeRF2Physics (CVPR 2024): https://ajzhai.github.io/NeRF2Physics/
* PhysVGGT: https://arxiv.org/pdf/2609.18920
* NIST FCD: https://www.nist.gov/el/fcd · NIST IR 8521: https://nvlpubs.nist.gov/nistpubs/ir/2024/NIST.IR.8521.pdf
* FSRI Materials & Products DB: https://doi.org/10.1177/07349041241235566
* CNN THR prediction from item images (FSJ 2025): https://www.sciencedirect.com/science/article/abs/pii/S0379711225001808
* Indoor fire load recognition: https://www.researchgate.net/publication/355864197_Deep_Learning-Based_Instance_Segmentation_for_Indoor_Fire_Load_Recognition
* PolyU AIoT building fire digital twin (AEI 2025): https://www.sciencedirect.com/science/article/pii/S1474034625000102
* GenAI fire scenario analysis (FSJ 2025): https://www.sciencedirect.com/science/article/abs/pii/S0379711225000918
* SafeAgentBench: https://arxiv.org/abs/2412.13178 · AGENTSAFE (CVPR 2026): https://openaccess.thecvf.com/content/CVPR2026/papers/Ying_AGENTSAFE_Benchmarking_the_Safety_of_Embodied_Agents_on_Hazardous_Instructions_CVPR_2026_paper.pdf
* Mind's Eye: https://arxiv.org/abs/2210.05359
* NFPA 72 t² α values: https://www.researchgate.net/figure/Values-for-a-and-tg-for-different-classifications-of-fire-growth-in-NFPA-72_tbl1_360843287

*Caveat: arXiv pages could not be fetched directly from this environment, so details of the 2026 preprints come from search-result abstracts. Read FireWorldBench and the ICLR 2026 real-time-agents paper in full before committing to Track A.*

# Research Plan: Open Global Fire-Following-Earthquake (FFE) Model

> **Parked (September 2026).** The FFE study needs ignition records and pre-event building stock that are hard to obtain (see the Paper 2 feasibility discussion). The current direction is `ResearchPlan_ExactFire.md`, which reuses this plan's data pipeline and building graphs. This file is kept as the long-term application (the earthquake scenario = many simultaneous ignitions) once the exact solver is published.

*Validation, research questions, data, Unity/GPU engine and closed-loop system design.*
*Builds on `Direction_FireFollowingEarthquake.md`; where the two differ, this plan wins. Literature and data checked through late September 2026.*

---

## 0. "Fire after earthquake" already has many papers. Why this stays novel

Searching "fire after earthquake" returns hundreds of papers. They fall into five families, and none of them does what this plan does:

| Family | Typical work | What it answers | Why ours is different |
|---|---|---|---|
| **1. Structural fire after earthquake** (the largest family in search results) | Fire resistance of earthquake-damaged steel frames and RC columns; full-scale fire tests on damaged frames; FFE fragility of braced frames | Does *one damaged building* survive a fire? | Different scale and question. We model fire *spreading between thousands of buildings*. |
| **2. Ignition statistics** | Davidson GLMMs (US), Anderson (Tohoku), Nishino hierarchical Bayes (Japan), HAZUS ignition curves | How many fires start for a given shaking? | We *use* these. Our step is testing whether one ignition model **transfers across countries** with open covariates (RQ1a). |
| **3. City spread models, one city each** | Himoto–Tanaka physics model (Kobe, Itoigawa), GisFFE, building-height FFE model (Kobe/Wajima, 2025), Pohang cluster method (2024), Tokyo fire-spread clusters | How did or would fire spread in *this* city, using *this* city's local building survey? | Each is built and checked on local inventories in one country. We test **one model on open global data across countries**, against these as baselines (RQ1b–c). |
| **4. Regional probabilistic cascade** | Nishino (Japan), Buffalo end-to-end framework (Noto), HAZUS FFE (US), commercial catastrophe models (closed) | Expected FFE losses for one region | Single-country or closed. We make it **open, global, and calibrated on building-level outcomes**, and quantify the **tail-risk** effect worldwide (RQ3). |
| **5. Reviews and case reports** | 2026 *Natural Hazards* review; Noto/Kobe reports | State of the field | They list our targets as open gaps: probabilistic modelling, transferability, slope, vegetation, suppression. |

**In one sentence:** existing work answers "how does fire spread in *this* city with *its* data?"; this plan answers "**does one physics-based model, fed only open global data, predict fire after earthquakes across countries, what controls conflagration, and how much does it add to global earthquake risk?**"

Each individual ingredient exists (ignition regressions, physics-based spread, cluster analysis). The contribution is the transfer test, open-data inputs, statistical calibration on building-level labels, satellite updating, and the global tail-risk result.

The method also starts small: one zone at a time, validated before scaling (zone ladder Z1 → Z3 in `Tools/ffe_pipeline/ffe/zones.yaml`).

---

## 1. Verdict of the cross-check

**The research holds, with two claims narrowed.** Each claim was checked against (a) direct prior art, (b) adjacent groups that could reach it first, and (c) whether data exist to test it.

| # | Claim | Evidence | Closest threat | Status |
|---|---|---|---|---|
| C1 | There is no open, global FFE model | GEM Global Risk Model v2026 excludes post-earthquake fire. USGS PAGER excludes secondary hazards from loss estimates. The 2026 *Natural Hazards* FFE review lists probabilistic modelling as a gap. | Commercial models (Moody's RMS, Verisk; GEM + Aon for Canada) are closed | **Holds** |
| C2 | No FFE model has been validated across countries | All published validations are within one country: Japan (Kobe, Wajima, Itoigawa: Himoto, Nishino), Korea (Pohang, Sci Rep 2024), US (HAZUS). Nishino found ignition rates vary strongly even between Japanese earthquakes. | Nishino (Kyoto), Elhami-Khorasani (Buffalo) | **Holds** (and becomes RQ1) |
| C3 | Open global data can replace local building inventories | Untested anywhere. Enabled by GlobalBuildingAtlas (2.75 B footprints + heights, 2025) and GEM-taxonomy footprints (2.7 B, 2025). | — | **Holds** (RQ1) |
| C4 | Conflagration is a percolation-type threshold | **Partly known.** Tokyo's regional risk survey has used wind-dependent "fire-spread clusters" since 1975. Korea's cluster-based rapid method appeared in 2024. Berkeley (2026) showed phase transitions in *structural damage*, not fire. | Tokyo practice, Korean method, Berkeley | **Narrowed**: the new part is a *cross-city test* of the threshold with physics-defined wind edges, measured jointly against ignition density and suppression loss (RQ2) |
| C5 | GPU engine as a novelty | A GPU voxel urban fire framework (UNSW, 2026) exists, uncalibrated and wildland-urban. No GPU *ensemble* FFE engine found. | UNSW voxel work | **Narrowed**: the engine is an *enabling* contribution, not the headline |
| C6 | Statistical calibration (simulation-based inference) of an urban fire model on building-level outcomes | Not found; urban fire models are hand-tuned per event | — | **Holds** |
| C7 | Updating urban post-earthquake fire forecasts with satellite fire detections | Done for *wildfire* (WRF-SFIRE and related); not found for urban post-earthquake fire | — | **Holds** |
| C8 | Global estimate of how much FFE adds to *tail* seismic risk | Not found | GEM (plans secondary perils) | **Holds** (RQ3) |

**Signs it can go somewhere:**
* PNAS (Jun 2026) published urban-conflagration intervention work (Vanderbilt, wildland-urban interface).
* ISO TC92/WG14 opened a work item (2026) to evaluate large-outdoor-fire models, including post-earthquake fires. It needs benchmarks.
* Both open global earthquake systems (GEM for risk, PAGER for rapid impact) have the same fire gap.

**Groups to position against or collaborate with:**
* Nishino (Kyoto/DPRI): Japanese ignition records and a physics-based spread model with spot fires. The best collaborator.
* Elhami-Khorasani (Buffalo): regional FFE frameworks.
* Mahmoud (Vanderbilt): graph-based wildland-urban fire, the PNAS 2026 paper.
* Z. Wang (Berkeley): phase transitions in damage.
* GEM: the natural home for the output.

---

## 2. Research questions and hypotheses

Every hypothesis has a **pre-registered pass/fail target**. The targets are proposals to fix before running experiments, not predicted results.

### RQ1: Can open global data carry a physics-based FFE model across countries?
*Can an FFE model driven only by globally available open data reproduce observed ignitions and building-level fire outcomes in held-out events and countries, with skill comparable to models built on local inventories?*

* **H1a (ignition transfer).** A hierarchical Bayesian ignition model with open covariates cuts between-event dispersion relative to a model using shaking intensity (PGV) alone.
  * Covariates: PGV, building damage share, construction mix, local time and season, and power-outage signal from NASA Black Marble for events after 2012.
  * Pass: σ_event falls by ≥30%, and leave-one-event-out predicted counts fall within a factor of 2 for ≥70% of events.
* **H1b (spread transfer).** A spread model calibrated on some conflagrations predicts building-level burned/not-burned on held-out conflagrations.
  * Pass: AUC ≥ 0.85, and burned count within ±30% of observed.
* **H1c (open vs local inputs).** Replacing local inventories with GlobalBuildingAtlas + GEM taxonomy costs little.
  * Pass: AUC drops by ≤ 0.05 and burned count shifts by ≤ 20% (tested where both exist: Kobe, Wajima, Eaton).
* **Required baselines** (the model must beat these, or the added complexity is unjustified):
  * HAZUS empirical FFE;
  * static fire-spread clusters (Tokyo/Pohang style);
  * a Hamada-type empirical spread model;
  * distance-only percolation.

### RQ2: What controls whether an earthquake becomes a city fire?
*How is variance in FFE outcome split between ignition density, wind, fuel-network connectivity and suppression loss, and is the move from isolated fires to conflagration threshold-like in a physics-defined connectivity metric?*

* **H2a (attribution).** In a global sensitivity analysis (Sobol indices from GPU ensembles over cities × events × wind):
  * connectivity × wind interaction dominates (total-order index > 0.5) in dense cities with combustible construction;
  * ignition density dominates in low-connectivity cities.
* **H2b (threshold and collapse).** Plotted against the connectivity metric, burned fraction shows a sharp transition, and curves from different cities collapse onto one master curve. Plotted against PGV, they do not collapse.
  * Connectivity metric: the fraction of buildings in the largest wind-conditioned fire cluster. Edges come from the spread physics at the event's wind, not from fixed distances.
  * Pass: a two-regime (breakpoint) model beats a smooth fit by ΔAIC > 10, and the between-city spread of the breakpoint is ≤ 25%.
* **H2c (tests on real events).** On real events, the RQ2 descriptors (connectivity relative to threshold, ignitions per cluster, suppression ratio) separate conflagrations (Kobe, Wajima, Kanto 1923) from low-spread events (Northridge, Kumamoto, Christchurch) better than PGV or damage counts alone.

### RQ3: How much does fire add to earthquake risk worldwide, and where?
*When FFE is added to probabilistic seismic risk for seismic cities worldwide, how do average annual loss (AAL) and tail losses change, and how much does leaving FFE out mis-rank cities?*

* **H3a (tail, not mean).** For a non-trivial subset of cities, FFE raises the 1-in-250/500-year loss by ≥ 20% while raising AAL by < 5%.
* **H3b (mis-ranking).** Ranking cities by shaking-only tail loss versus shaking-plus-fire tail loss gives Kendall τ < 0.8.

---

## 3. The system as closed loops

```
                ┌──────────────────────────── DATA LAYER (open) ────────────────────────────┐
                │ ShakeMap/Atlas · GEM hazard+exposure · GlobalBuildingAtlas · GEM-taxonomy   │
                │ footprints · ERA5/MERRA-2 wind · NASADEM · WorldCover · OSM roads/stations  │
                └──────────────┬──────────────────────────────────────────────▲──────────────┘
                               ▼                                              │ (L4) value of
                 City Fire-Graph Builder (Python)                             │ information:
                 footprints→nodes, geometry→edges (CSR), attributes          │ which data would
                               ▼                                              │ cut uncertainty most
   ┌──────────── ENGINE (Unity 6.3, C#/Burst CPU reference + HLSL GPU ensemble) ────────────┐
   │ damage states → ignitions → building fire growth → spread (radiation, plume, brands)    │
   │ → suppression on road graph → burned buildings, time series, losses                     │
   └───────┬───────────────────────────────┬───────────────────────────────┬────────────────┘
           ▼ (L1) CALIBRATE                 ▼ (L2) HINDCAST                  ▼ (L3) FORECAST
   simulation-based inference      held-out events, open-vs-local   OpenQuake event sets → risk
   on building-level labels        inputs, vs baselines (RQ1, RQ2)  curves (RQ3); new real quake →
   (DINS, Itoigawa, Kobe…)         → skill scores                   rapid forecast → FIRMS/Black
           │ posterior θ                     │ failures → model revision  Marble/ARIA observations →
           └──────────────► back into ENGINE ◄┘                         update → scored → posterior θ
```

* **L1 (calibrate).** The GPU ensemble feeds simulation-based inference: neural posterior estimation over roughly 10–15 physics parameters. It is trained on events with building-level labels, and the posterior is fed back into the engine.
* **L2 (hindcast).** Leave-one-event-out tests and the open-vs-local ablation answer RQ1. Failures send the physics back for revision; hindcast outcomes feed the RQ2 tests.
* **L3 (forecast).**
  * *Offline:* GEM stochastic event sets give RQ3 risk curves.
  * *Online:* for each new damaging earthquake, a pre-registered rapid forecast is issued. It is updated as NASA FIRMS fire detections, Black Marble outages and ARIA damage maps arrive, then scored. Every new earthquake becomes an out-of-sample test and a calibration update.
* **L4 (learn what to measure).** Uncertainty from L1–L3 ranks which data (for example, per-country ignition records or construction material) would most reduce tail-risk uncertainty. That ranking steers the next data collection.

---

## 4. Data

### 4.1 Inputs (all open)

| Dataset | Role | Coverage | Licence / access |
|---|---|---|---|
| USGS ShakeMap + **ShakeMap Atlas** (~14,100 events, 1900–2019) | Shaking (PGA/PGV/MMI grids) for hindcasts | Global | Public domain, ComCat API |
| GEM Global Hazard Mosaic + OpenQuake event-based calculator | Stochastic event sets (RQ3) | Global (30 models) | CC BY-NC-SA 4.0 |
| GEM Global Exposure + Vulnerability models | Replacement costs, fragility | Global | CC BY-NC-SA 4.0 |
| **GlobalBuildingAtlas** (TUM, 2025) | Footprints + heights (LoD1), 2.75 B buildings | Global | Open (GitHub/TUM); GEE catalogue |
| GEM-taxonomy conflated footprints (Sci Data 2025); GHS-OBAT | Construction type, age, use | Global | Open |
| ERA5 hourly (1940 onward); **NASA MERRA-2** (1980 onward); 20CRv3 (1906/1923) | Wind and humidity at event time | Global | Free |
| **NASADEM** / Copernicus DEM | Slope (a stated gap in the review) | Global | Open |
| ESA WorldCover / Sentinel-2 | Urban vegetation fuel (a stated gap) | Global | Open |
| OSM roads, fire stations | Suppression model | Global (uneven) | ODbL |
| **NASA Black Marble VNP46** | Outage/restoration (ignition covariate, L3) | Global, 2012 onward | Open |
| **NASA JPL ARIA Damage Proxy Maps** | Damage-state validation, L3 update | Event-based | Open |
| **NASA FIRMS** VIIRS/MODIS | Fire timing, L3 update (Wajima 2024 was detected) | Global, 2000 onward (VIIRS 2012 onward) | Open |

### 4.2 Calibration and validation sets (tier A = building-level outcomes, B = counts or areas, C = maps only)

| Event | Type | Outcome data | Tier | Use |
|---|---|---|---|---|
| Eaton & Palisades 2025 (LA) | Wind-driven conflagration | **CAL FIRE DINS**, building-level, open (~34k structures) | A | L1 spread (urban-interior subset only, to avoid wildland fuel confounding) |
| Lahaina 2023 | Wind-driven urban conflagration | FEMA/PDC building damage (~2,200) | A | L1/L2 spread |
| Marshall 2021 | Wind-driven | NIST damage data | A | L2 spread |
| Itoigawa 2016 (Japan) | Wind 9 m/s, 147 buildings, 10+ spot fires | Burned buildings, spot fires (published) | A/B | L1 spread + firebrands |
| Kobe 1995 | FFE conflagration | BRI GIS damage/burned polygons; 148 fires; 6,513 buildings | A (collaboration) | L2 end-to-end |
| Noto/Wajima 2024 | FFE conflagration | GSI area 48,000 m²; ~240 buildings; ignition records (Nishino); FIRMS detection | A/B | L2 end-to-end + L3 dry run |
| Kanto 1923 | FFE conflagration | Digitized hourly burned areas, ignition points | C | L2 timing/shape |
| SF 1906 | FFE conflagration | Historic burn maps | C | L2 shape |
| Northridge 1994, Loma Prieta 1989, San Fernando 1971, Napa 2014 | Many ignitions, limited spread | Ignition counts (Davidson 2009 GLMM dataset) | B | L1 ignition, L2 low-spread events |
| Japanese events 1995–2024 (Chuetsu, Tohoku shaking fires, Kumamoto, Osaka, Hokkaido, Fukushima-oki, Noto) | Mixed | Ignition records (FDMA/JAFSE via Nishino) | B | L1 ignition (collaboration) |
| Christchurch 2011 | Low spread | Ignition counts (Fire & Emergency NZ reports) | B | L2 low-spread event |
| Turkey 2023, Myanmar 2025 | Large damage | **No published ignition counts found** | — | Only as L3 test cases if data emerge |

**Data risk.** Ignition data outside Japan, the US and NZ are thin. RQ1 is therefore framed as "Japan ↔ US ↔ NZ transfer + open-vs-local inputs". Claims of truly global validation wait until more events are recorded; the L3/L4 loops exist to collect them.

### 4.3 Outputs to expect

* **Per building × ensemble member:** ignition time, burn state, heat dose. Stored as summaries: burn probability, P10/P50/P90 ignition time.
* **Per event:** distribution of burned buildings and burned area, burning-count time series, number of large fires, loss.
* **Per city:** loss-exceedance curves with and without FFE; AAL; 250/500-year losses; Sobol indices; connectivity metrics.
* **Benchmark release:** event inputs + observations + metrics as a Scientific Data / ESSD dataset for ISO TC92/WG14-style comparison.
* **Volumes:** about 10⁵–10⁶ buildings per city; 10²–10³ ensemble members per event; kept as summaries, not full trajectories. Tens of GB per global run.

---

## 5. Physics and statistics inside the engine

| Component | Model | Parameters θ (calibrated by L1) |
|---|---|---|
| Seismic damage | GEM fragility curves by taxonomy × ShakeMap intensity; sampled damage state per member | (fixed from GEM) |
| Ignition | Poisson regression with random effects: log λ = β₀ + β₁ log PGV + β₂·damage share + β₃·wood share + β₄·time/season + β₅·outage + u_event + u_country | β, σ_event, σ_country |
| Building fire | Staged growth (t²) → fully developed → decay. Duration from floor area × fuel load; seismic damage adds openings (Himoto post-earthquake variant). | Growth α, fuel-load scale |
| Spread: radiation | q = ε σ T⁴ F_view (façade-to-façade view factor from geometry); ignition when the integrated flux-time exceeds a threshold | Flame temperature, flux-time threshold |
| Spread: plume/wind | Wind-tilted flame reach and plume heating downwind (ERA5/MERRA-2 wind) | Tilt/reach coefficients |
| Spread: firebrands | Generation ∝ HRR; log-normal downwind landing distance; ignition probability per landing (Itoigawa-calibrated) | Generation rate, μ/σ of landing distance, p_ign |
| Suppression | Engines from stations, travel on the OSM road graph with collapse-blocked links; engines can control fires below a size threshold | Engine effectiveness, blockage probability |

**Calibration.** Simulation-based inference (neural posterior estimation):
* Summary statistics: building-level burn map, burned count, burning-count time series, spot-fire count.
* Held-out checks: calibration (PIT) histograms; proper scores (CRPS for counts, log score and AUC for building-level outcomes).

---

## 6. Unity 6.3 engine design (C#, Burst, HLSL compute)

**What the repo already provides:**
* Double-buffered compute.
* `AppendStructuredBuffer` compaction + `CopyCount`: exactly the primitive for event-driven active lists.
* `AsyncGPUReadback`.
* Burst `IJobParallelFor`.
* Runtime worker-count control.
* A profiling/CSV harness.
* The Unity Test Framework (already in the manifest).

**Target version:** Unity 6000.3.9f1 (Unity 6.3 LTS).

| Design choice | Why |
|---|---|
| **Building graph in CSR, in-edges (gather form).** Precomputed per edge: distance, bearing, façade overlap, height difference (float16). Wind applied at runtime. | Gather avoids float atomics, which HLSL SM5 does not provide portably, and gives deterministic sums. |
| **Ensemble-major batching:** thread = (member, building); per-member frontier lists concatenated with offsets | Hundreds to thousands of members run in one dispatch. |
| **Event-driven frontier:** append active and candidate buildings → `CopyCount` → `DispatchIndirect` | Cost scales with *burning* buildings, not city size. Reuses the repo's pattern. |
| **Counter-based RNG** (Philox/PCG hash keyed by event, member, building, step), identical in C# (Unity.Mathematics) and HLSL | Reproducible runs and **bitwise-comparable CPU/GPU parity tests**. |
| **Integer atomics only:** `InterlockedCompareExchange` for ignition claims, `InterlockedAdd` for engine counts and fixed-point loss sums | Portable across DX12 / Vulkan / Metal. |
| **Fixed simulated timestep** (e.g. 5–10 s) with sub-steps where needed | Removes the frame-rate dependence found in the audit. |
| **Burst CPU reference** of the same kernels, plus a small NumPy reference for reviewers | Correctness and parity; reviewer trust. |
| **Headless standalone player** (`-batchmode` with GPU, not `-nographics`), driven by a JSON experiment manifest, writing binary/CSV summaries | Runs on Linux Vulkan GPU nodes; a built player does not need the Editor. |
| **Visualization:** Cesium for Unity (3D Tiles terrain/imagery) + GPU-instanced LoD1 extrusions coloured by burn probability; FIRMS overlays; time slider | A stakeholder-facing digital twin. This is where Unity is uniquely strong. |

**Hard limits to design around:**
* Individual GraphicsBuffers are practically capped at about 2 GB, so chunk cities.
* Dispatch is limited to 65,535 thread groups per dimension, so use 2D/3D indexing.
* No portable double precision on the GPU, so aggregate losses on the CPU or in fixed-point.
* HLSL on Metal/Vulkan differs slightly in NaN/max semantics, so add NaN guards and parity tests.

**Order-of-magnitude cost (to be benchmarked, not a claim):**
* Assume a city graph of about 4×10⁵ buildings with about 20 in-edges each (about 8×10⁶ edges).
* 1,024 members at a 2–5% active frontier means about 10⁸–10⁹ edge evaluations per step.
* On a modern GPU that is milliseconds to tens of milliseconds per step.
* 12 simulated hours at 10 s steps is about 4,300 steps, i.e. seconds to minutes per event ensemble.
* This is what makes L1 simulation-based inference and L3 event sets feasible.

**Honest caveat.** Unity is not a standard scientific HPC platform. Mitigations: pure, documented HLSL kernels; the Burst and NumPy references; a published parity suite; and a headless runner. That keeps results reproducible without the Editor.

---

## 7. Plan with go/no-go gates

| Stage | Work | Gate (stop or pivot if failed) |
|---|---|---|
| **0: data audit** (first) | Obtain DINS (Eaton/Palisades), Lahaina and Itoigawa data; request Kobe BRI GIS and Japanese ignition records (Nishino); compile the Davidson US ignition data | ≥ 3 tier-A conflagrations and ≥ 8 events with ignition counts. Otherwise narrow to spread-only RQ1b/RQ2. |
| **1: engine core** | Graph builder (Python); Burst reference; GPU ensemble kernels; RNG parity suite; headless runner | CPU/GPU parity within tolerance; throughput measured |
| **2: spread pilot** | L1 on Eaton urban interior + Itoigawa; hold out Lahaina | **Must beat the static-cluster and distance-percolation baselines** on held-out AUC. Otherwise the physics adds nothing: rethink. |
| **3: end-to-end hindcasts** | Kobe, Wajima (conflagrations) vs Northridge, Christchurch (low spread); ignition hierarchy; open-vs-local ablation | RQ1 targets (H1a–c) reported, pass or fail |
| **4: mechanism** | Sobol analysis + threshold/collapse across 20–50 cities (RQ2) | H2a–c reported |
| **5: global** | OpenQuake event sets → tail maps (RQ3); L3 dry run replaying Wajima as if live | H3a–b; benchmark release |

**Papers:**
1. Engine + calibrated spread model (Fire Safety Journal).
2. Cross-country transfer and open-vs-local inputs (Earthquake Spectra / NHESS).
3. Benchmark dataset (Scientific Data / ESSD).
4. Global tail-risk result + mechanism (Nature Communications / PNAS).

---

## 8. Sources

* GEM Global Risk Model (no post-earthquake fire): https://docs.openquake.org/global_risk_model/world/index.html
* USGS PAGER FAQ (secondary effects excluded): https://earthquake.usgs.gov/data/pager/faq.php
* FFE comprehensive review (Natural Hazards, 2026): https://link.springer.com/article/10.1007/s11069-025-07794-z
* Nishino, probabilistic shaking–fire cascade (Natural Hazards 2022): https://link.springer.com/article/10.1007/s11069-022-05802-0 · ignition uncertainty (IJDRR 2023): https://www.sciencedirect.com/science/article/pii/S2212420923006040 · Noto 2024 fires: https://journals.sagepub.com/doi/10.1177/87552930251335207 · tsunami fire ignition (EESD 2026): https://onlinelibrary.wiley.com/doi/full/10.1002/eqe.70128
* Physics-based urban fire spread with spot fires (Itoigawa validation): https://link.springer.com/article/10.1007/s00477-019-01649-3
* Itoigawa 2016 fire facts: https://en.wikinews.org/wiki/Fire_engulfs_140_buildings_in_Itoigawa,_Japan · firebrands: https://link.springer.com/article/10.1007/s10694-018-0751-x
* Noto regional end-to-end FFE framework: https://www.sciencedirect.com/science/article/abs/pii/S2212420924006216
* Static cluster-based rapid FFE method (Pohang, Sci Rep 2024): https://www.nature.com/articles/s41598-024-72363-6
* Tokyo regional earthquake risk survey (fire-spread clusters): https://note.com/tokyo_tech/n/n77dbea2da5fc?hl=en · IAFSS dense urban fire risk: https://publications.iafss.org/publications/fss/9/267/view/fss_9-267.pdf
* Phase transitions in collective structural damage (Berkeley, 2026): https://arxiv.org/abs/2602.16195
* Optimal interventions to curb urban conflagration (PNAS 2026): https://www.pnas.org/doi/10.1073/pnas.2612835123
* Graph-based WUI fire model (Mahmoud/Chulahwat): https://www.nature.com/articles/s41598-022-19875-1
* GPU voxel urban fire (IJGI 2026): https://www.mdpi.com/2220-9964/15/9/423
* Post-earthquake ignition GLMMs (Davidson 2009): https://doi.org/10.1061/(ASCE)1076-0342(2009)15:4(351) · MCEER report: https://www.eng.buffalo.edu/mceer-reports/09/09-0004.pdf
* Kobe fire spread (IAFSS): https://publications.iafss.org/publications/fss/5/959/view/fss_5-959.pdf · NIST Kobe: https://www.nist.gov/el/earthquake-kobe-japan-1995
* Kanto 1923 46-hour spread reconstruction: https://asia.nikkei.com/static/vdata/infographics/kanto-earthquake-46hours/
* Wajima 2024 fire (MODIS/VIIRS detection): https://wtlab.iis.u-tokyo.ac.jp/wataru/RapidResponse/202401WajimaFire/
* USGS ShakeMap Atlas: https://earthquake.usgs.gov/data/shakemap/atlas/
* GEM hazard mosaic: https://hazard.openquake.org/gem/models/mosaic/
* GlobalBuildingAtlas (ESSD 2025): https://essd.copernicus.org/articles/17/6647/2025/ · code: https://github.com/zhu-xlab/GlobalBuildingAtlas
* Global semantic footprints with GEM taxonomy (Sci Data 2025): https://www.nature.com/articles/s41597-025-06132-z
* CAL FIRE DINS 2025 Eaton: https://gis.data.ca.gov/datasets/CALFIRE-Forestry::dins-2025-eaton-public-view/about · Palisades: https://gis.data.ca.gov/datasets/CALFIRE-Forestry::dins-2025-palisades-public-view
* Lahaina conflagration (IBHS): https://ibhs.org/wp-content/uploads/FINAL-Lahaina-Conflagration.pdf · NIST WUI damage data: https://www.nist.gov/el/fire-research-division-73300/wildland-urban-interface-fire-73305/nist-investigation-california-1
* NASA Black Marble for the Turkey 2023 earthquake: https://doi.org/10.3390/rs15082120
* NASA FIRMS: https://firms.modaps.eosdis.nasa.gov/active_fire/
* NASA ARIA DPM validation: https://journals.sagepub.com/doi/10.1177/87552930251377727
* Satellite active-fire data assimilation (wildfire): https://arxiv.org/pdf/2204.00686
* ISO TC92/WG14 large outdoor fire model evaluation (phys.org, May 2026): https://phys.org/news/2026-05-fuels-global-large-outdoor.html

*Caveat: several 2026 items (PNAS, arXiv, phys.org) could only be read through search abstracts, because direct fetches were blocked from this environment. Read them in full before submission.*

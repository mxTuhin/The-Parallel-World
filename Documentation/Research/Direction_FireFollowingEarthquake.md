# Research Direction v2: Fire Following Earthquake, Globally

*Supersedes the LLM/VLM direction in `ResearchDirections.md`; the physics audit in §2 of that file still applies. Literature checked through late September 2026.*

---

## 1. The direction in one paragraph

Fire following earthquake (FFE) is the peril behind the worst urban earthquake disasters: 1906 San Francisco, 1923 Kanto, 1995 Kobe (148 fires, about 66 ha burned, 6,513 buildings destroyed) and 2024 Noto (the Wajima morning market, about 240 buildings). Yet the scientific community's open reference model, **GEM's Global Seismic Risk Model (v2026), explicitly excludes post-earthquake fire.** Existing FFE models are regional (Japan, US HAZUS, China) or proprietary (commercial catastrophe models), and they need local building inventories. The proposed research is an **open, physics-based, GPU-accelerated FFE engine driven only by global open data** (NASA, USGS, ESA, global building footprints). It is validated by hindcasting real earthquakes *that did and did not* produce conflagrations across several countries, and then used to produce **the first global estimate of how much fire adds to earthquake risk in the world's seismic cities**.

## 2. How we got here: broaden, then narrow

### 2.1 Who gets helped (stakeholders)

| Stakeholder | What they lack today | Helped by |
|---|---|---|
| GEM / OpenQuake and the global seismic-risk community | FFE is missing from the open global model | An FFE module that plugs into OpenQuake event sets |
| Insurers, reinsurers, parametric catastrophe finance (World Bank, catastrophe bonds) | FFE is modelled only in closed commercial tools, and not at all for most countries | Open loss-exceedance curves including FFE |
| Fire and city authorities in seismic megacities (Istanbul, Tehran, Kathmandu, Lima, Manila, Dhaka, LA, Tokyo) | No tool that works with their data | Where conflagrations would start and run, and where fire stations and firebreaks matter |
| Fire-science community; ISO TC92/WG14 (the large-outdoor-fire modelling work item launched 2026) | No common multi-event validation benchmark for urban conflagration models | An open hindcast benchmark |
| Researchers | FFE studies are single-city and non-reproducible | Open engine, data pipeline and benchmark |

### 2.2 Candidate directions (scored 1–5; higher is better, and for "Crowding" 5 means uncrowded)

| Candidate | Novelty | Real data available | Community value | Crowding | Uses an engine/simulation core | **Total /25** | Verdict |
|---|---|---|---|---|---|---|---|
| **Global, open, physics-based FFE (earthquake → ignition → conflagration)** | 5 | 5 | 5 | 4 | 5 | **24** | **Chosen** |
| Global wildfire spread from NASA FIRMS | 2 | 5 | 4 | 1 (WildfireSpreadTS, FireCastNet, Next-Day Spread, FireSentry, PhysFire-WM) | 3 | 15 | Crowded |
| WUI structure loss (LA 2025, Lahaina) | 2 | 5 | 4 | 1 (NIST, UL, FSRI, many DINS-ML papers) | 3 | 15 | Crowded, US-centred |
| Earthquake shaking/damage simulation alone | 1 | 5 | 3 | 1 (GEM, USGS PAGER, SCEC) | 3 | 13 | Mature field |
| Indoor fire digital twin / firefighter training | 2 | 3 | 3 | 2 | 4 | 14 | Domain-locked, crowded |
| Informal-settlement fire only | 3 | 3 | 3 | 3 | 4 | 16 | Too narrow (dropped) |
| Tsunami- or industrial-triggered fires (Natech) | 4 | 2 | 3 | 4 | 3 | 16 | Data too thin |

### 2.3 Why the chosen gap is real (checked three ways)

1. **Stated by the owners of the reference model.** GEM's Global Risk Model documentation and the 2026 map notes say the assessment "does not account for … post-earthquake fires".
2. **Stated by the field's own review.** *Fire following earthquake: a comprehensive review* (Natural Hazards, 2026) lists these as open problems:
   * the ignition-versus-shaking relationship varies strongly between earthquakes;
   * slope, vegetation and suppression are poorly modelled;
   * probabilistic modelling is lacking.

   Nishino (2023) showed large between-event variation in ignition rates even within Japan alone.
3. **Newly enabled by data that did not exist before 2025.** Global footprints conflated from Google, Microsoft and OSM with GEM construction taxonomy now cover about 2.7 B buildings (Scientific Data, 2025). GHS-OBAT adds height, age and function for about 2.3 B footprints. With these, the inputs a physics-based spread model needs are available *everywhere* for the first time.

**Closest prior work, and the difference:**
* Nishino et al. (Japan) run a regional probabilistic shaking-plus-fire cascade with a physics-based spread model. It is single-country with local inventories.
* The Chinese end-to-end FFE framework (IJDRR 2025) hindcasts Noto 2024. It is scenario-based, not probabilistic or global.
* HAZUS is US-only and largely empirical.
* Commercial models are closed.
* Rybski et al. (2026) compute *settlement percolation* from footprints, but with purely geometric distances and no fire physics.

**Nobody has a model that is global, open, physics-based and probabilistic, validated across countries on events with and without conflagrations.**

## 3. Scientific questions and hypotheses

* **Q1, Transferability.** Can one physics-based spread model with a hierarchically pooled ignition model, fed only open global data, hindcast historical FFE outcomes across countries?
  * **H1:** it separates *conflagration events* (Kobe 1995, Noto/Wajima 2024, and historical SF 1906 and Kanto 1923) from *non-events* (strong urban shaking with little fire spread: Northridge 1994, Kumamoto 2016, Christchurch 2011, Turkey 2023) without per-event tuning.
* **Q2, Controls.** What decides whether an earthquake becomes a city fire?
  * **H2:** the outcome is set by a percolation-type threshold on the city's *fire network*. Here the edges between buildings come from physics (radiation, plume and firebrand ignition probability under the event's wind), not from a fixed distance. Crossing the threshold, combined with ignition density and loss of suppression capacity, gives a sharp, nonlinear transition that explains why similar shaking produces very different fire losses.
* **Q3, Global burden.** Where and how much does fire add to seismic risk?
  * **H3:** FFE barely changes *average* annual loss but greatly fattens the *tail* (for example, 1-in-250-year loss) in cities with combustible, dense construction and windy dry seasons. Maps based on average annual loss hide this.
* **Q2b, Ignition transferability.** Is the between-earthquake ignition variance explainable?
  * **H4:** globally available covariates reduce the unexplained variance in a hierarchical Bayesian ignition model. Candidates are construction taxonomy, local time and season (cooking and heating load), and power-outage and restoration timing from NASA Black Marble (electrical re-ignition was a major Kobe mechanism).

## 4. Real data (all open)

| Need | Source |
|---|---|
| Ground shaking (historical) | USGS ShakeMap archive (PGA/PGV/MMI grids) |
| Ground shaking (future, probabilistic) | GEM Global Hazard Mosaic plus OpenQuake stochastic event sets; USGS scenario catalogs |
| Building damage (validation) | **NASA JPL ARIA Damage Proxy Maps** (Sentinel-1/ALOS SAR coherence loss); Copernicus EMS gradings; Maxar Open Data imagery |
| Buildings: footprints, height, construction | Google Open Buildings, Microsoft Global ML Footprints, OSM, the 2.7 B conflated set with GEM taxonomy, GHS-OBAT, GEM Global Exposure Model |
| Wind and humidity at event time | ERA5 hourly (1940 onward); **NASA MERRA-2** (1980 onward); 20th Century Reanalysis for 1906/1923 |
| Terrain (a stated gap) | **NASADEM** / Copernicus DEM |
| Urban vegetation fuel (a stated gap) | ESA WorldCover, Sentinel-2 |
| Power outage and restoration | **NASA Black Marble VNP46** night lights |
| Observed fire extent and timing | **NASA FIRMS** VIIRS 375 m active fire (large fires); Sentinel-2 post-event; published burn maps: Kobe 1995 surveys, Noto 2024 fire damage survey (Nishino 2025), SF 1906 and Tokyo 1923 historical maps |
| Suppression and access | OSM fire stations and roads; road blockage derived from damage states |

## 5. The system (what gets built)

```
ShakeMap / OpenQuake event ──► damage states (GEM fragility) ──► ignitions (hierarchical Bayesian, PGV + covariates)
                                          │                                        │
                                          ▼                                        ▼
                        road blockage, suppression loss ──► GPU ensemble fire spread on the building graph
                                                                  (radiation + plume + firebrands, ERA5/MERRA-2 wind, slope)
                                                                                    │
                                                                                    ▼
                                                   burned footprint, loss, exceedance curves ──► validation / global maps
```

**Why an engine contribution is necessary.** A probabilistic FFE assessment needs
(stochastic ruptures, often 10⁴–10⁵) × (ignition samples) × (wind samples)
spread simulations on building graphs of 10⁵–10⁶ nodes, per city. This is why published physics-based FFE studies stop at a single region. The engineering contribution is a **batched, ensemble-parallel GPU spread kernel** (many independent stochastic fires per launch, event-driven so that cost scales with burning buildings, not city size). Its speed and CPU/GPU agreement are to be measured against a reference CPU implementation of the same physics.

**Spread physics.** A building-level model in the Himoto–Tanaka lineage:
* per-building fire growth by construction class;
* radiation with view factors;
* wind-tilted flames and plumes;
* stochastic firebrand spotting;
* suppression capacity that decays with the number of simultaneous fires and blocked roads.

**What carries over from this repo:**
* The Burst and compute-shader patterns: double buffering, append-buffer compaction of active cells, async readback, and the benchmarking harness.
* The `IFireSimulation` seam.
* Unity as an **interactive 3D front end** for exploring scenarios and communicating results.

**What must change:**
* The grid CA becomes a **building graph**.
* The ad-hoc heat rule is replaced (see the audit in `ResearchDirections.md` §2).
* Use fixed timesteps and SI units.

For cluster-scale global runs, keep the numerical core in portable GPU code (CUDA, NVIDIA Warp or JAX), with a Unity build as the viewer. Unity batch mode with a GPU remains an option for workstation runs.

## 6. Validation plan

* **Hindcasts, leave-one-event-out.** Metrics: burned-building count, burned-area overlap (IoU against mapped burn areas), number of large fires, and a conflagration yes/no discrimination score.
* **Event set:**
  * conflagration events: Kobe 1995, Noto 2024 (Wajima), Loma Prieta 1989 (Marina);
  * historical conflagrations, where data allow: SF 1906, Kanto 1923;
  * low-spread events: Northridge 1994, Kumamoto 2016, Christchurch 2011, Turkey 2023.

  First task: a data audit to confirm per-event availability.
* **Component checks.**
  * Damage states against ARIA Damage Proxy Maps.
  * Ignition model against event ignition counts (Nishino's Japanese data; HAZUS/Davidson US data).
  * Spread sub-model against published full-scale structure-separation experiments (NIST).
* **Honesty tests.**
  * Swap global open inputs for the local inventories used in published single-city studies, and report how much accuracy is lost.
  * Report the sensitivity of the headline global numbers to the ignition-model choice, which is the dominant uncertainty.

## 7. Outputs

1. **Open FFE engine plus OpenQuake coupling.** Target venue: Earthquake Spectra or NHESS.
2. **Open multi-event FFE hindcast benchmark** (inputs, observations, metrics). Target: Scientific Data or ESSD. Directly relevant to the ISO TC92/WG14 evaluation of large-outdoor-fire models.
3. **Headline paper:** "How much does fire add to earthquake risk worldwide?" It would contain the transferability result (H1), the percolation-threshold explanation (H2), the tail-risk map (H3), and the ignition covariates (H4). Target: Nature Communications, Nature Cities or PNAS.
4. **Unity scenario viewer** for stakeholders.

## 8. Compact roadmap

| Stage | Work | Exit criterion |
|---|---|---|
| 0 | Data audit of the hindcast events; fix the physics core; define the building-graph data model | Events with complete inputs confirmed |
| 1 | CPU reference spread model; GPU ensemble kernel; parity tests | GPU matches CPU; speed-up measured |
| 2 | Pilot hindcasts on three events: Kobe (conflagration), Wajima (conflagration), Northridge (non-event) | Model separates them with no per-event tuning; **go/no-go** |
| 3 | Hierarchical ignition model; full hindcast set | Leave-one-out skill reported |
| 4 | OpenQuake coupling; 20–50 seismic cities; then global | Tail-risk maps; papers |

**Main risks:**

| Risk | Mitigation |
|---|---|
| Sparse historical validation data | Stage-0 audit before building; pilots chosen for data richness |
| Ignition uncertainty dominates results | Make it explicit and quantified, which is itself a finding for H4 |
| Global footprints are less reliable in dense areas | Quantify against local inventories where both exist |
| GEM or commercial vendors add FFE first | Open, validated and cross-country is still unique; approach GEM as a collaborator rather than a competitor |

## 9. Sources

* GEM Global Risk Model docs (no post-earthquake fire): https://docs.openquake.org/global_risk_model/world/index.html · map viewer v2026.1: https://maps.openquake.org/map/grm-v2026_1/
* GEM + Aon Impact Forecasting (commercial FFE, Canada): https://www.globalquakemodel.org/GEMNews/gem-news-briefs-mar-2023
* FFE review (Natural Hazards, 2026): https://link.springer.com/article/10.1007/s11069-025-07794-z
* Nishino, regional shaking–fire cascade and ignition uncertainty (IJDRR 2023): https://www.sciencedirect.com/science/article/pii/S2212420923006040
* Nishino, Noto 2024 fires (Earthquake Spectra 2025): https://journals.sagepub.com/doi/10.1177/87552930251335207
* End-to-end FFE framework, Noto case (IJDRR 2024): https://www.sciencedirect.com/science/article/abs/pii/S2212420924006216
* FFE spread model with building height (IJDRR 2025): https://www.sciencedirect.com/science/article/abs/pii/S2212420925000858
* Himoto & Tanaka physics-based urban fire spread: https://www.sciencedirect.com/science/article/abs/pii/S0379711207001257 · post-EQ variant (Earthquake Spectra): https://pubs.geoscienceworld.org/earthquake-spectra/article/29/3/793/585952/a-physicsbased-model-for-postearthquake-fire
* Urban fire spread model review (IJDRR 2025): https://www.sciencedirect.com/science/article/pii/S2212420925003528
* Firebrand-driven WUI/urban conflagration simulation (FSJ 2026): https://www.sciencedirect.com/science/article/pii/S0379711226000548
* Global semantic building footprints, 2.7 B with GEM taxonomy (Sci Data 2025): https://www.nature.com/articles/s41597-025-06132-z · GHS-OBAT: https://www.ncbi.nlm.nih.gov/pmc/articles/PMC12221504/
* Settlement percolation, global critical distances: https://arxiv.org/pdf/2603.04439
* NASA ARIA Damage Proxy Maps: https://appliedsciences.nasa.gov/our-impact/news/aria-damage-proxy-map-shows-damage-and-after-earthquake-palu-indonesia · validation study: https://journals.sagepub.com/doi/10.1177/87552930251377727
* NASA FIRMS: https://firms.modaps.eosdis.nasa.gov/
* HAZUS Earthquake Technical Manual 6.1: https://www.fema.gov/sites/default/files/documents/fema_hazus-earthquake-model-technical-manual-6-1.pdf
* Kobe 1995 fire statistics: https://www.researchgate.net/figure/Fires-followed-Kobe-Earthquake-1995-11_fig1_250275067
* Comparative analysis of post-earthquake fires in Japan 1995–2017 (Fire Technology): https://link.springer.com/article/10.1007/s10694-018-00813-5

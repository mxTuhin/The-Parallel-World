# Local Engine Plan: what runs on your PC (Unity 6.3 + GPU)

All code for the paper is written. In the cloud session:
* the Python side passes 35 tests;
* the Unity C# was compiled and run under Mono: 10/10 CPU tests pass, and the 2 GPU tests need a GPU.

What remains:
* runs that need the real editor, Burst or a GPU;
* long Monte Carlo runs;
* data you add by hand.

Do the steps in order; each has a pass condition. Paths are relative to the repository root. Nothing here blocks further code changes: every step reads scenario files and writes result files.

---

## Step 0: Python environment, data and scenarios (≈ 15 min)

```bash
git fetch origin claude/youthful-pascal-51lotd && git checkout claude/youthful-pascal-51lotd
cd Tools/ffe_pipeline
python -m venv .venv && source .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -e ".[dev]"
python -m pytest -q                                     # expect: 35 passed
```
`FFEData/` is git-ignored, so rebuild it locally. There is no download limit on the PC.
```bash
for z in itoigawa2016 wajima2024 eaton2025_core tokyo_nakano tokyo_west_large; do
  python -m ffe fetch $z --only buildings,weather && python -m ffe build $z; done
```
Scenario files (`.ffes`) plus the Python reference results that Unity must reproduce:
```bash
python -m ffe sim compile itoigawa2016,wajima2024,eaton2025_core,tokyo_nakano,tokyo_west_large   # base physics, 5 m/s south wind
python -m ffe sim compile itoigawa2016,wajima2024 --variant hetero --wind 5                       # varied incubation (Local vs Global rule)
python -m ffe sim compile itoigawa2016 --variant critical --wind 0                                # rare-event regime
python -m ffe sim compile tokyo_nakano --wind 5 --ignition random:20                              # 20 simultaneous ignitions (earthquake-like)
```
**Pass:** each `FFEData/zones/<zone>/sim/` has `<name>.ffes`, `.ref.json`, `.ref_tign.f64` and `.ref_stepped_dt60_end_hazard.f64`.

Scenario options:

| Option | Values |
|---|---|
| `--variant` | `base`, `critical`, `hetero`, `ftp_n2`, `calibrated` (after Step 8) |
| `--wind` | a speed in m/s, or `era5` (mean of the 6 h after the zone's ignition time) |
| `--ignition` | `center`, `zone`, `random:K`, `LON,LAT` |
| `--hours` | simulation horizon |

## Step 1: Open in Unity and compile

1. Open the project with **Unity 6000.3.9f1**. Burst, Collections and Mathematics are now explicit in `Packages/manifest.json`.
2. Wait for compilation. The new assemblies are `FireGraph.Runtime`, `FireGraph.Editor` and `FireGraph.Tests.EditMode`.

**Pass:** no Console errors.

Code that was only compiled against stand-ins in the cloud, so check it first if errors appear:
* `ExactFireGpu.cs`, `SteppedFireGpu.cs`;
* `FireGraphViewer.cs`, `FrameRateProbe.cs`;
* the two `.compute` files and `BuildingsInstanced.shader`.

## Step 2: EditMode tests

`Window > General > Test Runner > EditMode > Run All`

**Pass:** 12/12, including `GpuMatchesCpu` and `GpuSteppedMatchesCpu`.

Run once with `Jobs > Burst > Enable Compilation` **on** and once **off**. The results must be identical (`FloatMode.Strict`).

## Step 3: Parity on real towns (headless)

```bash
Unity -batchmode -projectPath . -executeMethod ParallelWorld.FireGraph.Editor.FireGraphBatch.Parity \
   -ffes FFEData/zones/itoigawa2016/sim/base_U5_D180.ffes -logFile - -quit
```
Never pass `-nographics`: it disables compute shaders. Repeat for every compiled scenario. Each run writes `<scenario>.parity.txt`.

**Pass:** `PARITY PASS`.

| Check | Condition |
|---|---|
| Exact, CPU rules | bit-identical |
| Exact, CPU vs Python | ≤ 2e-3 relative (the cloud measured 2.6e-7) |
| Exact, GPU vs CPU | ≤ 2e-3 relative, ≤ 0.2% outcome flips |
| Stepped CPU vs Python, and GPU vs CPU | mean difference ≤ dt, ≤ 1% outcome flips |

Record whether GPU vs CPU exact is bit-identical: the paper reports it either way.

## Step 4: Viewer and frame-rate independence (E6)

1. Copy a scenario to `Assets/StreamingAssets/FireGraph/itoigawa2016_base_U5_D180.ffes`.
2. New scene → empty GameObject → add **FireGraphViewer** (backend Gpu) and **FrameRateProbe**.
3. Press Play.

The probe plays the fire at 30, 60 and 144 fps and stops exactly at 0.5, 1, 2 and 4 simulated hours. It writes `Documentation/Research/results/unity/e6_framerate.csv`.

**Pass:** for each probe time, the four state counts are identical across frame rates. The old grid CA changed by +50% between 30 and 144 fps.

## Step 5: Speed (E2/E3, RQ2)

Always the same seed, so exact and stepped runs see identical thresholds:
```bash
B="Unity -batchmode -projectPath . -executeMethod ParallelWorld.FireGraph.Editor.FireGraphBatch.Benchmark -logFile - -quit"
S=FFEData/zones/tokyo_nakano/sim/base_U5_D180.ffes
$B -ffes $S -backend gpu -replicas 64 -batches 4 -seed 1 -solver exact -rule local
$B -ffes $S -backend gpu -replicas 64 -batches 4 -seed 1 -solver exact -rule sequential
$B -ffes $S -backend gpu -replicas 64 -batches 4 -seed 1 -solver stepped -dt 10 -variant interp_hazard
$B -ffes $S -backend cpu -replicas 64 -batches 1 -seed 1 -solver exact -rule local
```
Grid to cover, validating as you go:

| Axis | Values |
|---|---|
| Scenario | itoigawa, wajima, eaton, tokyo_nakano, tokyo_west_large (GPU only) |
| Backend | cpu, gpu |
| Replicas per batch | 1, 16, 64, 256, 1024 (smaller for the big graphs) |
| Exact rule | local, global, sequential (on `hetero` scenarios too) |
| Stepped | dt 1, 10, 60, 300 × `end_hazard` / `interp_hazard` / `end_bernoulli` |

VRAM needed is about 24 bytes × N × R plus 12 bytes per edge (stepped runs: about 28 bytes × N × R).

**Pass:**
* **H2c:** GPU exact ≥ 10× Burst CPU exact per run at N ≥ 10^5 and R ≥ 64.
* **H2d on GPU:** exact beats every stepped configuration whose arrival error is ≤ 60 s. The error comes from Step 6.

## Step 6: Accuracy at scale (E1, RQ1): paired runs

Add `-savetimes 1` to an exact run and to each stepped run with the same scenario, seed, replicas and batches. Then:
```bash
python -m ffe sim unitypaired itoigawa2016,wajima2024,tokyo_nakano
python -m ffe sim report itoigawa2016,wajima2024,tokyo_nakano
```
This computes building-by-building arrival errors and big-fire probability ratios.

Suggested size: 1024 replicas × 100 batches (10^5 runs) for Itoigawa and Wajima, at `dt` 10, 60 and 300 s, for `base` and `hetero`. A saved-times file is 4 bytes × N × R per batch; delete them after `unitypaired`.

**Pass:** tail ratios with confidence intervals narrow enough to support or reject H1b. Python-only alternative on the CPU:
```bash
python -m ffe sim rq1 itoigawa2016,wajima2024 --tail-runs 100000 --procs 16
```

## Step 7: Rare events (E4, RQ3)

Ground truth with 10^7 GPU runs:
```bash
$B -ffes FFEData/zones/itoigawa2016/sim/critical_U0_D180.ffes -backend gpu -replicas 1024 -batches 10000 -seed 41 -solver exact -rule local
```
Then subset simulation at 10^-5 and 10^-6 against it (CPU, parallel):
```bash
python -m ffe sim rq3deep itoigawa2016 --crude-runs 1000000 --repeats 40 --procs 16
```
The cloud pilot already ran 10^6 crude runs.

**Pass:**
* subset-simulation estimates inside the GPU confidence interval (**H3a**);
* efficiency ≥ 10× at p ≤ 10^-5 (**revised H3b**).

## Step 8: Physics sanity (E5)

1. `python -m ffe sim calibrate` writes the template `Documentation/Research/data/fullscale_tests_template.csv`. Copy it to `fullscale_tests.csv` and fill in one row per test from *Wind-Driven Building-to-Building Fire Spread: Experimental Results and Probabilistic Modeling* (Fire Technology 2025): gap, wind, facade size, ignited or not, time to ignition.
2. Fit the model and write `results/calibration.json`:
   ```bash
   python -m ffe sim calibrate --tests Documentation/Research/data/fullscale_tests.csv
   ```
   `--variant calibrated` then uses the fitted values.
3. Real fire: digitise the Itoigawa 2016 burned area into `FFEData/zones/itoigawa2016/local_inputs/burned_area.geojson` and rebuild with `python -m ffe build itoigawa2016`. Set `ignition_lonlat` in `ffe/zones.yaml` from the fire report. Then:
   ```bash
   python -m ffe sim realfire itoigawa2016 --variant calibrated --wind era5 --hours 30 --runs 500
   python -m ffe sim realfire itoigawa2016 --variant calibrated --wind 9 --wind-dir 180 --hours 30 --runs 500   # observed wind
   ```
   The second run matters because ERA5 gives only 3.5 m/s there, while the report says ~9 m/s southerly with strong gusts; the 25 km grid smooths the local foehn wind. Output: observed vs simulated burned count, Brier score, AUC, F1/Jaccard.

---

## Time and hardware

| Step | Needs | Time |
|---|---|---|
| 0 | Python 3.10+, internet | 15 min |
| 1–3 | Unity 6000.3.9f1, any DX11/12 or Vulkan GPU | 30 min |
| 4 | editor | 15 min |
| 5 | GPU; ≥ 8 GB VRAM for tokyo_west_large | 1–2 h unattended |
| 6–7 | GPU (or many CPU cores for the Python runs) | 2–6 h unattended |
| 8 | reading and digitising | half a day |

## Commit back

Put these in `Documentation/Research/results/unity/` and commit them. `python -m ffe sim report <zones>` turns them into tables in `results/ExactFire_pilot.md`:
* `unity_runs/*.json` + `.csv`;
* `*.parity.txt`;
* `e6_framerate.csv`;
* the `sim/*.json` result files.

## Files on the Unity side

```
Packages/manifest.json                             + burst, collections, mathematics
Assets/Script/FireGraph/FfeContainer.cs            FFEG/FFES container reader
Assets/Script/FireGraph/FireScenario.cs            scenario arrays, validation
Assets/Script/FireGraph/Philox.cs                  counter-based RNG + threshold job
Assets/Script/FireGraph/ExactFireCore.cs           exact node clock (shared algorithm)
Assets/Script/FireGraph/ExactFireCpu.cs            exact solver, Burst jobs
Assets/Script/FireGraph/ExactFireGpu.cs            exact solver, GPU driver
Assets/Resources/FireGraph/ExactFire.compute       exact solver kernels
Assets/Script/FireGraph/SteppedFire.cs             time-stepped baselines, Burst (3 variants)
Assets/Script/FireGraph/SteppedFireGpu.cs          time-stepped baselines, GPU driver
Assets/Resources/FireGraph/SteppedFire.compute     time-stepped kernels (Philox in HLSL for Bernoulli)
Assets/Script/FireGraph/FireGraphViewer.cs         instanced viewer, state from exact ignition times
Assets/Script/FireGraph/FrameRateProbe.cs          E6 frame-rate independence probe
Assets/Script/FireGraph/BuildingsInstanced.shader
Assets/Script/FireGraph/Editor/FireGraphBatch.cs   headless Parity / Benchmark (+ menu items)
Assets/Tests/FireGraph/EditMode/*                  12 tests
Assets/Tests/FireGraph/Fixtures/small.*            300-building fixture + Python exact and stepped references
```
The old grid fire system (`FireSpreadJob`, `FireSimulationController*`, `FireHeatmap.compute`) is untouched and still runs the playable scenes.

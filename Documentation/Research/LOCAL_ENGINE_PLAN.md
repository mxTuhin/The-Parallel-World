# Local Engine Plan: what must run on your PC (Unity 6.3 + GPU)

Everything that could run without Unity has been done and checked in the cloud session:
* the Python solver;
* the experiments;
* compiling and **running the Unity CPU code under Mono** (8/8 EditMode tests pass; C#↔Python parity on 3 towns).

This file lists only what needs the real editor, Burst, a GPU, or downloads above the cloud limit. Do the steps in order; each has a pass condition. Paths are relative to the repository root.

---

## Step 0: Get the project and data (≈ 10 min)

```bash
git fetch origin claude/youthful-pascal-51lotd && git checkout claude/youthful-pascal-51lotd
cd Tools/ffe_pipeline
python -m venv .venv && source .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -e ".[dev]"
python -m pytest -q                                     # expect: 30 passed
```

`FFEData/` is git-ignored, so rebuild the graphs and scenarios locally. There is no download limit on the PC.

```bash
python -m ffe fetch itoigawa2016 --only buildings
python -m ffe fetch wajima2024   --only buildings
python -m ffe fetch eaton2025_core --only buildings
python -m ffe fetch tokyo_nakano --only buildings        # ~15 MB, 136,560 buildings
python -m ffe fetch tokyo_west_large --only buildings    # large (> 90 MB): the scaling-study graph
for z in itoigawa2016 wajima2024 eaton2025_core tokyo_nakano tokyo_west_large; do python -m ffe build $z; done
python -m ffe sim compile itoigawa2016,wajima2024,eaton2025_core,tokyo_nakano,tokyo_west_large
python -m ffe sim compile itoigawa2016 --variant critical --wind 0
```
(PowerShell: run the `build` line once per zone.)

**Pass:** each `FFEData/zones/<zone>/sim/` has `base_U5_D180.ffes`, `.ref.json` and `.ref_tign.f64`.

## Step 1: Open in Unity and resolve packages

1. Open the project with **Unity 6000.3.9f1**. `Packages/manifest.json` now lists Burst 1.8.28, Collections 2.6.2 and Mathematics 1.3.3 explicitly; they were already installed as dependencies.
2. Wait for compilation. The new assemblies are `FireGraph.Runtime`, `FireGraph.Editor` and `FireGraph.Tests.EditMode`.

**Pass:** no compile errors in the Console.

If Unity reports errors, the most likely spots are:
* `FfeContainer.cs`, which uses `MemoryMarshal.Cast` (needs the .NET Standard 2.1 profile, the Unity 6 default);
* API-name mismatches in `ExactFireGpu.cs` / `FireGraphViewer.cs`, which were compiled only against stubs in the cloud.

Fix them or paste the errors into a new session.

## Step 2: EditMode tests (CPU with Burst, and GPU)

`Window > General > Test Runner > EditMode > Run All`

**Pass:** 9/9 green:
* 8 tests already pass on Mono;
* `GpuMatchesCpu` needs a GPU. It checks that GPU commit rules agree bit for bit, and GPU vs CPU within 2e-3 relative with at most R outcome flips.

Also check once with `Jobs > Burst > Enable Compilation` **on** (the default) and **off**. The results should be identical: `FloatMode.Strict`.

## Step 3: Parity on real towns (headless)

```bash
"<Unity>/Unity.exe" -batchmode -projectPath . -executeMethod ParallelWorld.FireGraph.Editor.FireGraphBatch.Parity \
   -ffes FFEData/zones/itoigawa2016/sim/base_U5_D180.ffes -logFile - -quit
```
Do not pass `-nographics`: that disables compute shaders. Repeat for Wajima, Eaton and Tokyo-Nakano. Each run writes `<scenario>.parity.txt`.

**Pass:** `PARITY PASS`. The GPU line shows `max rel time diff` ≤ 2e-3 and an outcome mismatch ≤ 0.2%. **Record** whether `bit-identical=True` for GPU vs CPU: the paper reports this either way.

## Step 4: Viewer and frame-rate independence (E6)

1. Copy a scenario, e.g. `FFEData/zones/itoigawa2016/sim/base_U5_D180.ffes`, to `Assets/StreamingAssets/FireGraph/itoigawa2016_base_U5_D180.ffes`.
2. New scene → empty GameObject → add **FireGraphViewer**. Set `backend` to Gpu and `playbackSpeed` to 600, then press Play. Buildings go grey → dark red (incubating) → orange (burning) → black (burnt out).
3. **E6 check:**
   * set `Application.targetFrameRate` to 30, 60 and 144;
   * pause at the same `simTime`, e.g. 3 h;
   * take screenshots, or log burned counts at that time.

**Pass:** identical state at identical `simTime`, whatever the frame rate. The old grid CA changed by +50% between 30 and 144 fps (audit in `ResearchDirections.md` §2).

## Step 5: GPU benchmarks (E3, RQ2)

```bash
# per scenario, backend and replica count; writes CSV + JSON to <scenario dir>/unity_runs/
Unity -batchmode -projectPath . -executeMethod ParallelWorld.FireGraph.Editor.FireGraphBatch.Benchmark \
   -ffes FFEData/zones/tokyo_nakano/sim/base_U5_D180.ffes -backend gpu -replicas 64 -batches 4 -seed 1 -rule local -logFile - -quit
```
Grid to run, starting small and validating as you go:

| Scenario | Backend | Replicas per batch |
|---|---|---|
| itoigawa2016, wajima2024 | cpu, gpu | 1, 16, 64, 256, 1024 |
| eaton2025_core | cpu, gpu | 1, 64, 256 |
| tokyo_nakano | cpu, gpu | 1, 16, 64 |
| tokyo_west_large | gpu | 1, 16 (watch VRAM: about 24 bytes × N × R plus 12 bytes per edge) |

Also run `-rule sequential` once per scenario: GPU iteration count vs windowed rule.

**Pass:** H2c needs a GPU/CPU speed-up of ≥ 10× at N ≥ 10^5 and R ≥ 64. Put the `unity_runs/*.json` files in `Documentation/Research/results/unity/` and commit them.

## Step 6: GPU crude Monte Carlo ground truth (E1 tails, E4)

```bash
Unity -batchmode -projectPath . -executeMethod ParallelWorld.FireGraph.Editor.FireGraphBatch.Benchmark \
   -ffes FFEData/zones/itoigawa2016/sim/critical_U0_D180.ffes -backend gpu -replicas 1024 -batches 1000 -seed 41 -rule local -logFile - -quit
```
That is 1,024,000 runs. Their burned counts give P(burned ≥ K) down to about 10^-5. Do the same for `base_U5_D180` of Itoigawa and Wajima (10^5 runs) to firm up the RQ1 tail ratios.

Commit the CSVs, compressed if large, and `python -m ffe sim report` will include them once the loader is added. That loader is a small cloud task: ask for it.

## Step 7 (optional): Physics sanity (E5)

* Get the 23-test table from *Wind-Driven Building-to-Building Fire Spread: Experimental Results and Probabilistic Modeling* (Fire Technology 2025): separation, wind and ignition yes/no.
* Digitise the Itoigawa 2016 burned area into `FFEData/zones/itoigawa2016/local_inputs/burned_area.geojson`.

Then ask for `ffe sim calibrate`: fitting `e_flame_kw_m2` and the FTP median to the tests, then comparing with Itoigawa.

---

## What each step needs

| Step | Needs | Time |
|---|---|---|
| 0 | Python 3.10+, internet | 10 min |
| 1–2 | Unity 6000.3.9f1 | 15 min |
| 3 | a GPU that supports compute (any DX11/12 or Vulkan card) | 5 min |
| 4 | editor | 20 min |
| 5 | GPU; for tokyo_west_large ≥ 8 GB VRAM recommended | 1–2 h unattended |
| 6 | GPU | 1–3 h unattended |
| 7 | reading and digitising | half a day |

## Files touched on the Unity side

```
Packages/manifest.json                           + burst, collections, mathematics (explicit)
Assets/Script/FireGraph/FireGraph.Runtime.asmdef
Assets/Script/FireGraph/FfeContainer.cs          FFEG/FFES container reader
Assets/Script/FireGraph/FireScenario.cs          scenario arrays (NativeArray), validation
Assets/Script/FireGraph/Philox.cs                counter-based RNG + threshold job
Assets/Script/FireGraph/ExactFireCore.cs         exact node clock (shared algorithm)
Assets/Script/FireGraph/ExactFireCpu.cs          Burst jobs: Predict, Reduce, Decide, Apply
Assets/Script/FireGraph/ExactFireGpu.cs          GPU driver (CommandBuffer batches of iterations)
Assets/Resources/FireGraph/ExactFire.compute     GPU kernels
Assets/Script/FireGraph/FireGraphViewer.cs       instanced viewer, time sampled from exact ignition times
Assets/Script/FireGraph/BuildingsInstanced.shader
Assets/Script/FireGraph/Editor/FireGraphBatch.cs headless Parity / Benchmark + menu items
Assets/Tests/FireGraph/EditMode/*                tests + asmdef
Assets/Tests/FireGraph/Fixtures/small.ffes       300-building fixture + Python reference result
```
The old grid fire system (`FireSpreadJob`, `FireSimulationController*`, `FireHeatmap.compute`) is untouched and still runs the playable scenes.

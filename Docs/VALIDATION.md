OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor Validation Handbook

Procedures for validating AOI Monitor: developer/QA test plan (§2), image-learning quickstart (§3), sample-data demo (§4), customer dataset validation (§5), client test kit and demo evidence (§6, §7), 8-hour soak (§8), factory acceptance plan (§9). Numeric acceptance criteria: `Docs/METRICS_VAL.md`.

## 1. Scope and correct-by-design boundaries (read first)

This handbook validates the Stage 1 prototype only: operator console, local uploaded-image inspection, image-only learning - not real camera, lighting, robot/PLC/safety, live 3D, production MES/ERP, cybersecurity, or full factory automation (Stage 2+, simulated or Not Connected on purpose). Standards traceability and quality-gate reports are project evidence, not formal certification. Correct-by-design states; staying labeled is the pass:

- Camera: *No Camera Connected* / Folder Camera Simulation (not real camera validation). Lighting, Robot/PLC, MES: *Not Connected* / *Simulated* / *Mock*.
- Default engine: Pixel Difference Prototype Engine, not a trained production model. ONNX shows `REVIEW` safely without a valid model; ONNX readiness only when a configured local ONNX model loads and succeeds.
- Learned-model/synthetic evidence is labeled synthetic, not customer acceptance. 3D Profile is sample CSV only.
- Stage 1 customer-data validation does not imply Stage 2/3/4 or Full Factory Automation readiness.

Never claim from Stage 1 or simulated evidence: live camera validation; robot/lighting/3D/MES/safety readiness; customer acceptance from synthetic or internal demo images; production model certification; absolute defect detection; absolute absence of false calls. A boundary claiming real hardware readiness would be the bug.

## 2. Developer / QA manual test plan

Run in order; the Stage 1 image-based software "works" when PASS 0-6 hold and §1 boundaries stay labeled.

- **PASS 0 - launch.** Download artifact `AOI_Monitor-windows-x64` from the **Build Windows App** GitHub Actions run (branch `claude/aoi-pcb-gui-review-qpqo05`); unzip to a writable folder (e.g. `C:\AOI\AOI_Monitor`); run `AOI_Monitor.exe` (self-contained; see `HOW_TO_RUN.txt`). Pass: opens maximized at 1920x1080, no install, no crash; first launch creates local storage silently.
- **PASS 1 - shell smoke.** Click every Home tile; resize; switch Role (Access panel: Operator / Engineer / Admin). Pass: all 13 modules open, no red "Recoverable page error" card, no freeze; dense pages scroll, text not clipped; role shown in header.
- **PASS 2 - core inspection.** Image Library > Open Record: import a PNG/JPG board image. Main Inspection: view Top/Side/Bottom, **Start**, **Next Board**; Defect List populates (No, Type, ROI, Score, Severity, Side, X, Y); Result shows green OK / red NG / amber REVIEW; **Save Result**. Golden Compare: run a comparison. Pass: end-to-end inspection; Save Result persists (visible in Log & Export); Golden Compare returns score, verdict, decision reason, hotspot overlay.
- **PASS 3 - ML with images.** Run §3.1 (zero-data CLI), §3.3 (GUI learning), then batch validation (AI / Models > Run Dataset Preflight > Run Batch Inspection > Analyze False Calls > Export Stage 1 Validation Package). Pass: run writes `visual_learning_report.html`; overlays land on actual defects, not random background; false-call rate at/under target; batch yields accuracy/precision/recall/false-call metrics and an export package.
- **PASS 4 - persistence.** Recipe Editor (Engineer/Admin): draw ROI; set type + AI threshold + tolerance rules (X/Y tolerance, rotation, IPC class, lighting profile, false-call policy); **Save Recipe**; reload - all values return; **Test Run** with an unsaved edit states it ran against current edits. Calibration (Engineer/Admin): >= 2 image-board point pairs; **Save Profile**; reload - points + transform summary persist. Settings: change storage path / engine / threshold; **Apply**; reopen - kept; **Cancel** restores last saved. Pass: lossless round-trips; behaviors as labeled.
- **PASS 5 - logs, retention, roles.** Operator views Inspection History, Review Events, Export History, Audit Trail (read-only); Admin exports inspection-history CSV + audit CSV; Operator/Engineer export/delete is blocked with a permission message, and logged; Settings > Data Retention (enable purge, retention days, pre-purge warning) loads and Applies. Pass: role gating enforced; retention persists; denials in audit trail.
- **PASS 6 - 3D Profile (sample-data mode).** Page shows **Sample Data Mode** + **3D Camera Not Connected**; **Load Height CSV** (`x,y,height`); left-drag rotate, wheel zoom, right-drag pan, **Reset View**; click surface or 2D inset; Accept/Reject a feature. Pass: interactive surface; selection syncs across surface, inset, height slice (peak markers), feature list; Accept/Reject records a review event.

Not covered (hardware acceptance, §9): real camera acquisition, lighting sync, robot/PLC/safety, live 3D, production MES. Defects: **Access > Report Issue** or **Export Support Bundle** (redacts paths by default); note the failed PASS step.

## 3. Image-learning quickstart (zero-data smoke test first)

Stage 1, image-only learning: learns "good board" appearance from OK images, calibrates a false-call threshold. Not live camera inspection; no production-ML acceptance claim. Setup: Windows 10/11 x64, .NET 10 SDK (or a published build); use a clean writable storage root:

```powershell
setx AOI_MONITOR_STORAGE_ROOT "C:\AOI\Data\AOI_Monitor"
# reopen the terminal so the variable is picked up
```

CLI runs from the repo root: `dotnet run --project AOI_Monitor.Tools -- <command> <options>` (published build: `AOI_Monitor.Tools.exe`).

### 3.1 Zero-data smoke test (no dataset needed)

```powershell
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo --synthetic --output .\MlDemoOut --operator you
```

Generates synthetic labeled images, learns, calibrates, writes overlays + report; prints images learned, OK-validation count, recommended threshold, false-call rate, possible-escape status. In `.\MlDemoOut` verify all four: `visual_learning_report.html` (learned reference, tolerance/anomaly summary, false-call calibration); `learned_reference.png` + tolerance map look like a coherent golden board, not noise; `visual_evidence`/overlays highlight the seeded defects on NG samples; report labeled synthetic/demo (correct - not customer acceptance). All four readable = learning, calibration, overlay, reporting stages work.

### 3.2 Learn from your own board images

One project folder; groups map to subfolders (PNG/JPG/JPEG):

```text
LearnProject/
  golden/          approved reference image(s) of a good board
  ok_learning/     >= 5 known-good boards the model learns "normal" from
  ok_validation/   known-good boards held out to calibrate the false-call threshold
  inspection/      boards to inspect after learning (mix of OK and suspect)
  ng_validation/   known-defect boards (optional but needed to prove missed-defect rate)
```

Rules of thumb: >= 5 OK-learning images; >= 1 OK-validation image (more is better - the threshold is only as trustworthy as this set); add `ng_validation/` for possible-escape evidence.

```powershell
dotnet run --project AOI_Monitor.Tools -- learn-from-images `
  --project-folder .\LearnProject --output .\LearnOut `
  --operator you --false-call-target 0.05 --board-model YOUR-BOARD
```

Results in `.\LearnOut`: `visual_learning_report.html` (review first); `learned_reference.png`, `learned_tolerance_map.png`; `before_after_false_call_report.html` + `threshold_sweep.csv` (false calls vs possible escapes across the sweep); `inspection_results.csv`; `visual_evidence/` overlays. Good run: OK-validation images pass at the recommended threshold; false-call rate at/under `--false-call-target`; overlays land on real defects; with `ng_validation/`, a possible-escape status (empty `ng_validation/` honestly reports missed-defect rate cannot yet be proven - add NG images). Near-zero false positives: raise `--false-call-target` only after reviewing the sweep; prefer borderline boards falling to REVIEW over hard-NG; more OK-validation images sharpen the threshold.

### 3.3 In the GUI

Engineer or Admin role (learning is role-gated) > AI / Models > AI Training Setup > point each group (Golden / OK Learning / OK Validation / Inspection / optional NG Validation) at its folder > run > review + open exported `visual_learning_report.html`. Optional: Settings > AI > Learned PCB Visual Models > Set as Active Inspection Model (does not claim live-camera validation - the label says so). Measure against a labeled set via batch validation (§5).

### 3.4 Troubleshooting

- "Missing Golden / Reference or OK Learning images": add >= 1 image to `golden/` or >= 5 to `ok_learning/`.
- "Missing OK Validation images": add >= 1 to `ok_validation/`.
- "No NG Validation images were provided; possible escapes cannot yet be fully proven": expected with empty `ng_validation/`.
- Nothing written: check the output path and `AOI_MONITOR_STORAGE_ROOT` are writable.

A clean run is Stage 1 image-based evidence only; a future real camera feeds frames through the existing camera seam without changing this workflow.

## 4. Sample dataset performance demo

Stage 1 uploaded-image validation demo on generated, non-confidential sample data - software workflow evidence only (§1). The in-app Stage 1 Readiness screen's Sample Dataset Guide button opens this walkthrough. Before a customer walkthrough, run 4.1-4.3 end to end; every generated report must label the data synthetic/demo Stage 1 evidence.

**Generate demo data** (repo root): `pwsh SampleData/demo_dataset_generator.ps1` creates `SampleData/DemoSet_Quick/images`, `SampleData/DemoSet_Quick/golden`, `SampleData/DemoSet_Quick/customer_validation_manifest.csv`, and `SampleData/DemoSet_Quick/folder_camera/top`, `.../side`, `.../bottom`. Manifest columns:

```text
image,ground_truth,golden_image,defect_type,side,refdes,roi_id,roi_type,lot_id,board_model,notes
```

**Readiness smoke:** `pwsh Scripts/run-stage1-readiness-smoke.ps1` generates the dataset (skip: `-SkipGenerate`), verifies both manifests, runs the Stage 1 preflight/benchmark service smoke, writes `TestResults/stage1_readiness_smoke_report.txt`, `.json`, and `TestResults/stage1_readiness_smoke.trx`. Service-level only (no WPF, no hardware validation); uses `dotnet test --no-restore` for offline repeatability (`-Restore` on a fresh machine).

### 4.0 Headless Stage 1 test sequence (run this first)

The whole Stage 1 evidence chain runs without the GUI, so it is repeatable and CI-checkable. Run it in this order; each step feeds persisted evidence the next one reads. `Scripts/run-stage1-testing.ps1` runs all four and stops on the first failure.

```powershell
pwsh Scripts/run-stage1-testing.ps1 -Operator <your-id>
```

Or step by step (from the repo root, after `dotnet build AOI_PCB_Database.slnx -c Release`). The benchmark must run FIRST: the `stage1-exit` validation package embeds the latest recorded benchmark, so running the benchmark after the exit step stamps a previous (possibly different-dataset) benchmark into the package (defect found 2026-09-14; two packages had byte-identical benchmark CSVs from the wrong dataset).

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- benchmark --images SampleData/DemoSet_Quick/images --golden SampleData/DemoSet_Quick/golden/tbox_ref_top.png --output TestResults/stage1/bench --priority maximize-defect-recall
```

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- stage1-exit --dataset SampleData/DemoSet_Quick/images --manifest SampleData/DemoSet_Quick/customer_validation_manifest.csv --output TestResults/stage1/exit --operator <id> --priority maximize-defect-recall --allow-simulation
```

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- record-build-evidence --operator <id> --test-results TestResults
```

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- stage1-readiness --dataset SampleData/DemoSet_Quick/images --manifest SampleData/DemoSet_Quick/customer_validation_manifest.csv --output TestResults/stage1/readiness
```

`stage1-readiness` exit codes: `0` PASS, `1` CONDITIONAL, `2` FAIL, `3` usage error. `record-build-evidence` records the statuses **you** pass it — it does not run or infer the gates, because a tool asserting its own PASS is evidence of nothing. Run the gates first (`pwsh Scripts/run-quality-gates.ps1 -Configuration Release`) and pass the real outcome.

#### Expected results on the shipped demo dataset

Measured 2026-08-08 on `SampleData/DemoSet_Quick` (64 images: 24 OK, 40 NG across solder bridge / missing component / polarity error / height anomaly), Pixel Difference Prototype Engine `PIXEL_DIFF_0.2`, CPU. A tester should reproduce these; a material deviation is a finding, not noise.

| Detection priority | Precision | Recall | False-call rate | Possible escapes |
|---|---:|---:|---:|---:|
| `minimize-false-positives` | 100 % | 50.0 % | 0 % | 10 |
| `balanced` (default) | 100 % | 66.7 % | 0 % | 10 |
| `maximize-defect-recall` | 100 % | 100 % | 0 % | 0 |

Difference-score separation on this dataset: known-good boards 0.16-0.90 %, known-defect boards 7.2-34.4 % — roughly 8x, comfortably wider than any policy's review band. Recall counts NG verdicts only; REVIEW rows are excluded from the confusion matrix by design, which is why the stricter policies show lower recall while still flagging the boards for human review.

`stage1-readiness` on this dataset, after the three preceding steps, is **PASS with 15/15 checks**. Anything less means an evidence step did not run or did not persist — read the per-check `Next:` line.

These are synthetic-data workflow numbers. They demonstrate that the pipeline detects and reports correctly; they are **not** model accuracy evidence and never substitute for customer-dataset acceptance (§5).

#### Public-dataset engineering validation runs (2026-09-14/15)

The same four-step chain was run against two public PCB defect datasets converted to 
ROI-tile Stage 1 datasets (converter scripts and split protocols are colocated with each 
dataset's README):

- **DeepPCB** (100 unmodified 224 px tiles, 40 OK / 60 NG, per-tile goldens from the 
  dataset's own reference captures): readiness gate **PASS 15/15** under the default 
  `maximize-defect-recall` bands with zero false calls and zero possible escapes. 
  **Read the metrics with the denominator**: 48 of 100 tiles end in REVIEW (metrics count 
  decided rows only, per §7), so this run demonstrates the pipeline and honest 
  REVIEW routing on real board imagery — it does not meet the review-rate expectations 
  for an acceptance claim, and the false-call sweep finds no VALID operating point at 
  this tile geometry.
- **PKU PCB_DATASET** (112 px tiles, synthesized defects composited onto real board 
  photos, binary OK/NG ground truth): threshold profile calibrated on a 124-tile 
  calibration split (boards 01-08), then frozen and verified at **100 % accuracy / 
  precision / recall, zero REVIEW, zero false calls** on a 76-tile evaluation split and 
  a 50-tile frozen-geometry hold-out (board 09) that was never used for calibration. 
  Readiness gate **PASS 15/15**; the deployed board-model-scoped profile is stamped in 
  every result.

These runs are **engineering validation on public data** — pipeline proof, not customer 
acceptance. "Customer validation of AI accuracy" (§5) still requires the 
customer/evaluator dataset and the §10 sign-off.

### 4.1 Batch validation

Run §5.3-5.4 with `Test Image Folder` = `SampleData/DemoSet_Quick/images`, `Ground Truth CSV` = `SampleData/DemoSet_Quick/customer_validation_manifest.csv`: `Run Dataset Preflight`, `Run Batch Inspection`; review accuracy/precision/recall, OK/NG/REVIEW counts, false calls, possible escapes, timing, rows, selected-image preview; use `Export CSV`, `Export Annotated Images`, or `Export Stage 1 Validation Package` (contents: §5.6).

### 4.2 Performance benchmark

`Readiness & QA > Performance Benchmark` > `Image folder` > `SampleData/DemoSet_Quick/images`; select `SampleData/DemoSet_Quick/golden/tbox_ref_top.png` as golden so the benchmark measures the operator golden-compare workload (otherwise the default engine measures the lighter no-reference path; the report says so); default run count, warm-up 1 for a cold-start figure; `Run`. Headless equivalent:

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- benchmark `
  --images SampleData/DemoSet_Quick/images `
  --golden SampleData/DemoSet_Quick/golden/tbox_ref_top.png `
  --output TestResults/perf `
  --count 60 --warmup 1
```

Outputs: `benchmark_report.html`/`.pdf`/`.json`, `benchmark_results.csv`, latest summary JSON - p50/p90/p95/p99/max frame-to-overlay, images per minute, over-threshold count, per-stage p95 timings (load/preprocess/inference/overlay/persistence), engine + model configuration (CPU-only in this build; detection priority, confidence threshold, threshold profile), per-sample dimensions, cold-start vs steady-state. Frame-to-overlay = image load through overlay-data preparation, headless; on-screen WPF draw is measured in-app by the latency service.

**Golden-reference cache before/after (2026-07-30).** Verdict: `PixelDifferenceGoldenCache` caches the prepared golden per file version; cached bytes bit-identical (scores/verdicts/hotspots unchanged, pinned by `PixelDifferenceGoldenCacheTests`). Same demo dataset, 60 samples, warm-up 1, golden `tbox_ref_top.png`, CPU, Pixel Difference Prototype Engine - frame-to-overlay ms before -> after: p50 13.0 -> 8.8 (-33%), p90 15.8 -> 10.7 (-33%), p95 16.4 -> 11.2 (-31%), p99 17.8 -> 12.5 (-30%), max 18.2 -> 12.6 (-31%); p95 image load 4.5 -> 3.2 (-29%); p95 inference 8.7 -> 5.6 (-36%). Still true: cache key = size + last-write time + head/tail SHA-256 fingerprint (first/last 4 KB), so the engine always scores the current on-disk golden even after timestamp-preserving overwrites; the golden cache never serves sample images; demo boards are small synthetics far below the 1000 ms budget; only frame-to-overlay totals compare across the stage-split change. Full pre-consolidation text: git history (`Docs/Sample_Dataset_Performance_Demo.md` at commit b2c4616).

### 4.3 Stage 1 readiness report

After preflight, batch validation, false-call review, package export, benchmark: `Readiness & QA > Stage 1 Readiness` > `Refresh Stage 1 Readiness`; check overall status, missing evidence, preflight/batch/benchmark summaries, package path, next action; `Export Stage 1 Readiness Report` writes `stage1_readiness_report.html`, `.pdf`, `.json`. Handoff gate for uploaded-image validation only: can pass with no real camera/lighting/robot/MES evidence but must keep that limitation visible. (Content requirements: §5.7.)

### 4.4 Folder Camera Simulation

`Run Inspection` > view Top/Side/Bottom > select `SampleData/DemoSet_Quick/folder_camera/top`, `.../side`, or `.../bottom` when prompted > `Start`, `Next Board`. Must stay labeled Folder Camera Simulation: Stage 2 workflow preparation, not validation of real GigE/USB3 cameras, lighting, robot motion, PLC safety, or MES traceability.

**Limitations.** Generated sample images are synthetic and non-production; Pixel Difference Prototype Engine evidence is deterministic prototype workflow evidence, not a trained production ML model claim; remaining boundaries per §1.

## 4.5 Running a third-party or public PCB dataset

Synthetic data proves the pipeline runs. Real boards prove it works. Public PCB datasets each ship in their own folder shape and none match the Stage 1 dataset contract, so `prepare-dataset` converts one into that contract and reports what it could and could not infer:

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- prepare-dataset `
  --source <folder you downloaded> `
  --output TestResults/stage1/thirdparty `
  --board <board-name> --emit-learning
```

Then run the normal chain against `--output`:

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- stage1-exit `
  --dataset TestResults/stage1/thirdparty/dataset/images `
  --manifest TestResults/stage1/thirdparty/dataset/customer_validation_manifest.csv `
  --output TestResults/stage1/thirdparty/evidence --operator <id> --priority maximize-defect-recall --allow-simulation
```

**The tool downloads nothing.** Obtaining the data and confirming its licence — especially for anything shipped to or shown to a customer — is the operator's responsibility. Several widely used PCB datasets are research-use-only.

### What to check before trusting the numbers

1. **Registration.** The pixel-difference engine compares a sample against a golden. That only works if the two are framed alike. Photographs taken free-hand differ by pose, not by defect. When the report says the golden was promoted `FromNormal`, open two or three Golden Compare overlays and confirm the highlighted region is the defect and not the board edge. If it is the board edge, the dataset is anomaly-detection material (use the learned visual model via `--emit-learning`), not golden-compare material.
2. **Preflight gates.** The preparation report lists every default gate the dataset will fail (>= 50 images, >= 20 OK, >= 20 NG, >= 2 defect classes, >= 5 per class) before a run is attempted.
3. **Unmapped defect classes.** Class folder names are normalized through the active defect taxonomy. Anything unmapped is reported by name; add it through `System Settings > Defect Taxonomy` CSV import rather than renaming the customer's folders.
4. **Duplicates.** Byte-identical images across class folders are reported. They inflate apparent accuracy and are a preflight blocker.
5. **Ambiguous boards are the point.** Run the same dataset at all three `--priority` settings. Boards that flip between OK, REVIEW, and NG across policies are the ambiguous population, and they are what `Analyze False Calls` (§5.4) and the threshold sweep exist to tune. A dataset where every board is unambiguous proves far less.

### Dataset shapes recognised

| Source shape | Example structure | Ground truth from | Golden from |
|---|---|---|---|
| MVTec-AD style | `train/good`, `test/good`, `test/<defect>` | folder name | promoted known-good image |
| VisA style | `Data/Images/Normal`, `Data/Images/Anomaly` | folder name (no per-image defect type) | promoted known-good image |
| One folder per class | `good/`, `solder_bridge/`, ... | folder name | promoted known-good image |
| Paired sample/template | `<stem>_test.jpg` + `<stem>_temp.jpg` + sidecar | annotation sidecar (empty = OK) | the paired template |

Use `--layout` to override detection, and `--golden per-board --golden-folder <templates>` when a dataset ships board templates in a separate folder.

## 5. Customer dataset validation kit

Repeatable Stage 1 customer-data validation: an engineer runs it; management reviews the package without rerunning the software.

### 5.1 Folder structure and manifest

Keep customer data outside the repository; one dataset folder per customer/run:

```text
CustomerDataset/
  images/
    board_0001_top_ok.png
    board_0002_top_bridge.png
  golden/
    board_ref_top.png
    board_ref_bottom.png
  customer_validation_manifest.csv
  README.txt
```

`images/` = inspection samples; `golden/` = approved references for the Pixel Difference prototype engine; manifest paths may be dataset-relative. Start from `SampleData/customer_validation_manifest_template.csv` (`Open Manifest Template` on the AI / Models screen). Required columns:

```text
image, ground_truth, golden_image, defect_type, side, refdes, roi_id, roi_type, lot_id, board_model, notes
```

| Column | Required | Expected Values |
|---|---:|---|
| image | Yes | Relative or absolute path to a PNG/JPG/JPEG sample image. |
| ground_truth | Yes | `OK` or `NG`. Unknown labels do not support acceptance metrics. |
| golden_image | Yes | Relative or absolute path to the approved golden/reference image. |
| defect_type | Yes for NG | Defect class such as `solder_bridge`, `missing_component`, `polarity`. Use `OK` for OK samples. |
| side | Yes | `top`, `bottom`, or `side`. |
| refdes | Strongly recommended | Reference designator such as `U10`, `R24`, `J1`. |
| roi_id | Strongly recommended | Stable ROI identifier from the recipe or labeling tool. |
| roi_type | Strongly recommended | ROI category such as `pad`, `component`, `lead`, `marking`. |
| lot_id | Yes | Customer lot or dataset batch identifier. |
| board_model | Yes | Board/model/program name. |
| notes | Optional | Labeling comments and customer limitations. |

### 5.2 Dataset gates, goldens, naming

Default gates: min total images 50; min known ground-truth images 50; min OK 20; min NG 20; max unknown-label rate 5%; all-OK/all-NG datasets fail preflight; >= 2 NG defect classes, >= 5 images per class, named consistently (`solder_bridge` = `Solder Bridge`; inconsistent naming hurts review). Missing golden references are blocking preflight failures by default; for an intentional model-only run, document the waiver in `notes`, treat missing goldens as warnings, and do not call the result Pixel Difference golden-compare validation. Naming: stable, sortable, non-confidential - `{board_model}_{lot_id}_{serial}_{side}_{label_or_defect}.png`, e.g. `tbox_lot07_0001_top_ok.png`, `tbox_lot07_0042_top_solder_bridge.png`; avoid spaces, customer secrets, operator names, timestamps identifying private production; prefer PNG (JPG/JPEG accepted from the customer).

### 5.3 Dataset preflight

`AI / Models`: select `images/` + manifest; `Run Dataset Preflight`; fix all blocking failures before acceptance. Checks: folder structure, manifest columns, image existence, golden existence, duplicate rows, OK/NG balance, defect-class coverage, duplicate file hashes, side/view metadata, ROI/refdes completeness when ROI metadata is supplied, hard-to-audit names. Card: `PASS` no blocking failures or warnings; `CONDITIONAL` warnings need management/customer review; `FAIL` blocking failures must be fixed before acceptance evidence is repeatable.

### 5.4 Batch test and false-call reduction

With preflight `PASS` (or accepted `CONDITIONAL`): `Run Batch Inspection`; review metrics, dataset quality, class breakdowns, `FAIL`/`N/A` rows; export annotated evidence only after confirming the intended dataset/manifest. Stage 1 may use the Pixel Difference prototype - no production model accuracy claim. Then choose the false-call mode, `Analyze False Calls`; review precision, recall, false-call rate, possible escape rate, review load, recommendation status; Engineers may draft a threshold profile or apply a recommended threshold when the recommendation is valid. Threshold changes are Stage 1 labeled-data evidence only - not production readiness across new cameras, lighting, boards, or factories.

**Headless threshold calibration** (same lifecycle as the UI, for the CLI testing kit): run `stage1-exit` on a CALIBRATION manifest whose boards are disjoint from the evaluation boards, then `AOI_Monitor.Tools threshold-profile draft --operator "<id> [Engineer]" --role Engineer` (refuses anything but a VALID recommendation), `threshold-profile approve --profile <id> --operator ... --role Engineer`, `threshold-profile deploy ...`, and finally `stage1-exit` on the EVALUATION manifest - the deployed profile now governs full-frame verdicts and is stamped into every result. `threshold-profile list` shows profiles and rules. Selecting the threshold on the same rows used for acceptance metrics invalidates the metrics; keep the split. **Scope the draft to the board model it was calibrated on** (`--board-model <model>`): a profile left at the `ANY` default governs every dataset on the station and silently changes other boards' verdicts. `threshold-profile retire --profile <id> --operator ... --role Engineer --reason "<why>"` deactivates a deployment (audited; results it already decided keep their stamped profile id, and a retired revision can never be redeployed) - use it to withdraw a mis-scoped or superseded profile.

### 5.5 Benchmark and model acceptance

Benchmark: `Readiness & QA > Performance Benchmark` > `Image folder` > the same `images/` folder; review p50, p95, p99, max frame-to-overlay, images-per-minute, over-one-second count; required for a Stage 1 readiness PASS; local image-folder timing evidence only. Model acceptance (ONNX): register + validate in `Settings`, set active; `Run Model Acceptance` with the validation dataset folder + formal manifest CSV; review PASS/CONDITIONAL/FAIL, preflight summary, dataset quality, performance, limitations; release-package only when evidence suits the claim; promote a production candidate only from a PASS run; scoped to the supplied dataset and criteria.

### 5.6 Customer package

`Export Stage 1 Validation Package` (AI / Models, after a successful batch run): `validation_manifest.json`, `validation_summary.html`, `validation_summary.pdf`, `dataset_preflight_summary.json`, `validation_results.csv`, `validation_breakdown.csv`, `benchmark_results.csv`, `customer_validation_manifest.csv` when selected, `customer_validation_report.html`, `customer_validation_report.pdf`, `limitations.txt`, annotated image samples when available, package README (`README.txt`) with print instructions. For management review and customer evidence; prototype/hardware limitations explicit (summary covers p95 timing, false calls, possible escapes, limitations; the manifest records package/run IDs, statuses, criteria, files).

### 5.7 Readiness report and status meanings

After preflight, batch, false-call review, package export, benchmark: `Readiness & QA > Stage 1 Readiness` > `Refresh Stage 1 Readiness`; verify overall status, missing evidence, preflight summary, latest batch run, benchmark p95 + over-one-second count, latest package path, next action; `Export Stage 1 Readiness Report` (`stage1_readiness_report.html`/`.pdf`/`.json`). The report must identify what was tested, data used, row counts, false calls, possible escapes, p95 timing, over-one-second count, reports generated, missing evidence, limitations, remaining Stage 2/3/4 work. `PASS` = data, manifest, metrics, dataset quality, configured gates passed for the Stage 1 claim (not full factory readiness); `CONDITIONAL` = no blocking gate failed, warnings need review/waiver/follow-up; `FAIL` = a blocking requirement failed (missing images or manifest columns, all-OK/all-NG data, insufficient OK/NG balance or class coverage, excessive unknown labels, missing goldens under Pixel Difference criteria). Factory readiness stays separate (§9).

## 6. Client test kit (packaged build evaluation)

For a client/evaluator running a packaged proof-of-concept build independently - software evaluation and staged industrial-HMI/software-quality review only, standards-aligned evidence, not formal certification (§1).

**Package contents:** `app/` (`AOI_Monitor.exe` + runtime files); `RUN_RELEASE.md` (generation notes); `CLIENT_HANDOFF_README.md` (shortest launch path); `Docs/`; as a customer validation kit also `SampleData/README.md`, `SampleData/customer_validation_manifest_template.csv`, `SampleData/demo_dataset_generator.ps1`. Excluded on purpose: customer images, generated demo images, local SQLite databases, image vaults, generated exports, model files, secrets, MES credentials, production hardware adapters.

**Client PC:** Windows 10/11 64-bit; local unzip-and-run folder (e.g. `C:\AOI\AOI_Monitor_ClientTest\`); writable storage (default `%LOCALAPPDATA%\AOI_Monitor\`, changeable by Admin in Settings); small non-confidential PNG/JPG/JPEG PCB images; framework-dependent packages need the matching .NET Desktop Runtime/SDK (self-contained packages include it).

**First launch:** unzip, open `app/`, run `AOI_Monitor.exe`; first-run wizard if shown; Demo Mode unless LocalUsers authentication is configured; Admin or Engineer role for setup/export tests; the readiness banner must label simulated/not-connected/mock/not-validated features. Expected: local database + image vault available; Pixel Difference Prototype Engine default; real camera, robot, lighting, live 3D, production MES not validated unless accepted adapters were separately installed and acceptance-tested.

**Stage 1 demo in 10 minutes:** in the package folder run `pwsh .\SampleData\demo_dataset_generator.ps1 -OutputRoot "$PWD\SampleData\DemoSet_Quick"`, open `app\AOI_Monitor.exe`, follow §4.1-4.3 against the generated `SampleData\DemoSet_Quick`; review `stage1_readiness_report.html`/`.pdf`/`.json`, `validation_summary.html`, `customer_validation_report.html`, `benchmark_report.html`, `benchmark_results.csv`, `limitations.txt`. Produces Stage 1 uploaded-image validation evidence only (§1).

**Independent test flow** (small non-confidential images; keep private datasets outside the package, import locally):

1. Readiness: panel readable; simulated/mock/not-connected clearly labeled; open `Factory Readiness` + `Standards & Quality Checklist`; missing evidence shows `Missing`/`Partial`, not hidden.
2. Core workflow: run §2 PASS 2 on a sample image; the imported record survives an app restart; engine labeled Pixel Difference Prototype Engine unless a tested ONNX model is configured.
3. Disposition: `Defect Review` - record `Confirm NG`, `Mark False Call`, `Mark Possible Escape`, or `Hold for 2nd Review`; confirm it lands in logs.
4. Batch: run §4.1 with a folder of 3-10 small demo images (optional ground-truth CSV); export CSV, annotated images, Stage 1 validation package where available.
5. 3D: run §2 PASS 6 with `SampleData/profile_height_map_sample.csv` (or another non-confidential `x,y,height` CSV).
6. Exports (`Export & Trace`): inspection history CSV; review log CSV; annotated overlays (when image paths available); Performance Benchmark; Stage 1 Readiness report; Factory Readiness Go/No-Go package; Standards Traceability Matrix (from `Standards & Quality Checklist`); Client Demo Readiness gate. Expect readable HTML/JSON/PDF/CSV/PNG; export verification records show verified/warned/failed.

Clients need not run automated tests to review image-learning evidence - the internal team runs §7 and sends the artifacts.

**Send back:** readiness/warning/alarm panel screenshots; Stage 1 readiness report folder; Stage 1 validation package folder; Factory Readiness package; Standards Traceability Matrix; client demo readiness gate report; issue reproduction steps; crash report folder if any; environment (Windows version, package folder, storage root, resolution, DPI scaling, local vs networked data). No confidential production images without an approved data-sharing agreement.

**Reset/re-test:** data lives under `%LOCALAPPDATA%\AOI_Monitor\` or the configured storage root; pick a new storage root or archive/delete the test folder after confirming no needed evidence remains; never delete client evidence folders before exported reports, crash reports, and screenshots are reviewed.

## 7. Client image-learning demo evidence folder

Client-facing evidence folder for image-only PCB learning; the client reviews the report without running tests or reading logs. Shows learned normal PCB appearance, false-call reduction on OK Validation images, and abnormal regions with heatmaps and boxes. Not proof of live camera readiness or customer acceptance unless produced from the customer/evaluator dataset and reviewed in scope.

**Synthetic (fastest) demo**, repo root - always describe as synthetic only, not customer acceptance, not production model certification:

```powershell
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo `
  --synthetic `
  --output TestResults/image-learning-demo `
  --operator ci-image-learning `
  --false-call-target 0.05
```

**Customer folder mode** - §3.2 layout plus optional `image_truth.csv` (image-level only; feeds false-call and possible-escape reporting); no defect labels, bounding boxes, per-defect variables, model files, or camera hardware required:

```powershell
dotnet run --project AOI_Monitor.Tools -- client-image-learning-demo `
  --project-folder C:\AOI\customer_image_project `
  --output C:\AOI\client_image_learning_evidence `
  --operator engineer01 `
  --false-call-target 0.05
```

**Output folder:** `README_CLIENT_IMAGE_LEARNING_DEMO.txt`, `visual_learning_report.html`, `visual_learning_report.json`, `learned_reference.png`, `learned_tolerance_map.png`, `before_after_false_call_report.html`, `before_after_results.csv`, `threshold_sweep.csv`, `inspection_results.csv`, `annotated_overlays/`, `heatmaps/`, `example_images/`, `package_manifest.json`.

**Review.** Open `visual_learning_report.html` first (written for a non-software reader). It must show: learned normal appearance and variation from OK samples; harmless lighting/position variation ignored where calibration supports it; unusual regions flagged on inspection examples; false calls before vs after learning; the OK Validation image count behind false-call metrics; whether NG Validation images backed possible-escape evidence; recommended threshold and evidence limits; a boundary statement that Stage 2 live camera validation remains separate. With no NG Validation images it must say missed-defect rate cannot yet be fully proven.

**Claim rules:** §1 applies; false-call-reduction claims must include the OK Validation image count; defect-detection claims must state whether NG Validation images and possible-escape evidence were used; synthetic packets are workflow demonstrations only - customer acceptance requires the customer/evaluator image set and in-scope review.

**Send:** `visual_learning_report.html`; `before_after_false_call_report.html`; representative `annotated_overlays/` + `heatmaps/` files; `learned_reference.png`; `learned_tolerance_map.png`; README + package manifest. Keep customer images and generated payloads outside git; share only via the approved client data channel.

## 8. Stage 1 soak test procedure (8-hour batch-inspection stability)

Auditable evidence for the customer acceptance criterion "stable operation for 8-hour continuous PoC testing" (gap-audit ID ACC-11-03, `Docs/Customer_Spec_Gap_Audit.md`), via the headless `batch-soak` harness in `AOI_Monitor.Tools`.

> Stage 1 uploaded-image batch-inspection soak evidence. Frames come from a local image folder processed by the offline batch-inspection pipeline. This is **not** live camera acquisition, lighting, robot/PLC, safety, or MES evidence, and it does **not** satisfy Stage 2-4 hardware readiness gates.

This scope statement is embedded in every report and the console banner; never present a batch soak report as camera or factory-automation stability evidence. Live-source soak = the separate Admin soak tool in `Export & Trace` (Folder Camera Simulation) and, from Stage 2 on, real cameras.

### 8.1 What the harness does

Each pass runs the real batch-inspection pipeline (same as the AI Model Test screen and the `stage1-exit` CLI) over every PNG/JPG/JPEG in the folder (optional ground-truth manifest), recording pass duration; per-image timing (average, max, count over 1 second); verdict counts (OK / NG / REVIEW / ERROR); managed memory (sampled after a full GC = surviving objects), working set, handles, threads; SQLite size (incl. WAL/SHM); error + alarm-service events. Fail conditions:

| Condition | Report fail reason |
|---|---|
| Unhandled exception escaping the pipeline | `UnhandledException` |
| An inspection exceeding the stuck-iteration watchdog (default 5 min) | `StuckIteration` |
| Sustained managed-memory growth trend (default: slope > 64 MB/h over the second half of samples AND total growth > 256 MB; evaluated only after a warm-up of a quarter of the requested duration so short runs stay informational) | `MemoryGrowthTrend` |
| No readable images in the folder | `NoImagesFound` |
| Every image in a pass erroring | `EveryImageFailed` |
| Process crash | non-zero exit + `crash_marker.txt` in the run folder |

Per-file errors among readable images are tolerated and recorded (batch bad-file-skip convention); a mid-run SQLite persistence failure is recorded and disables further batch-run persistence instead of aborting; durations are monotonic (Stopwatch), so a clock step cannot inflate the 8-hour claim.

**Artifacts** (timestamped `batch_soak_<stamp>` subfolder): `batch_soak_report_<stamp>.html` (run ID, software version, engine/model configuration incl. detection priority + threshold profile, dataset file-list fingerprint, stability metrics, failure conditions, pass samples); `batch_soak_report_<stamp>.json` (full result, every pass); `batch_soak_passes_<stamp>.csv` (per-pass metrics; leading `#` lines carry the scope statement + run identity); `soak_debug.txt` (full traces, only after an unhandled failure; operator reports carry type + message only); `ExportHistory` + `ExportVerification` (SHA-256) records in local SQLite. Each pass persists as a batch test run by default (`--no-persist-batch-runs` disables); after a soak, the AI / Models "latest run" is a soak pass - re-run your validation batch if needed.

### 8.2 Prerequisites

1. Windows 10/11, Release build (`dotnet build AOI_PCB_Database.slnx --configuration Release`) or a published Tools binary.
2. Folder of PNG/JPG/JPEG board images (rehearsal: `pwsh SampleData/demo_dataset_generator.ps1`, use `SampleData/DemoSet_Quick/images`); optional manifest CSV for ground truth per pass.
3. **Disable sleep/hibernation and Windows Update restarts for the run window** (Settings > System > Power: sleep Never while plugged in); a mid-run sleep invalidates the evidence.
4. Disk space for images + database growth (extrapolate from the smoke run).
5. Storage root: same local database as the app (default `%LOCALAPPDATA%\AOI_Monitor`; set `AOI_MONITOR_STORAGE_ROOT` to isolate the soak).

### 8.3 Step 1 - smoke rehearsal (5 minutes, required before any 8-hour claim)

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- batch-soak `
  --images SampleData/DemoSet_Quick/images `
  --output TestResults/batch-soak `
  --operator <your-id> `
  --profile smoke
```

Confirm: exit code 0, `PASS` in the console summary, three report files in `TestResults/batch-soak/batch_soak_<stamp>/`, and a populated scope banner, engine/model configuration, and pass table in the HTML report.

### 8.4 Step 2 - full 8-hour run

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- batch-soak `
  --images <dataset-folder> `
  --manifest <manifest.csv> `
  --output TestResults/batch-soak `
  --operator <your-id> `
  --profile eight-hour
```

One progress line per pass (elapsed, remaining, images, errors, memory, handles); leave the window open, do not log off. Ctrl+C cancels: in-flight image abandoned, partial evidence written, report labeled `CANCELED` (**not** 8-hour evidence). Options: `--duration-minutes <n>`; `--delay-seconds <n>` (default 2); `--engine pixel-difference|onnx|learned-pcb-visual` (default: configured engine - pixel-difference prototype unless an ONNX/learned model is ready); `--priority balanced|minimize-false-positives|maximize-defect-recall` (default balanced; recorded); `--stuck-timeout-minutes <n>`; `--memory-slope-fail-mb-per-hour <n>` / `--memory-growth-fail-mb <n>`; `--board-model <name>` / `--lot-id <id>`. Unknown or duplicated options are rejected with exit code 2.

### 8.5 Step 3 - interpret and preserve

Exit code 0 + `Result: PASS` + `8-hour uploaded-image PoC evidence: YES` = acceptance-criterion run complete; any `FAIL` reason, `CANCELED` status, or `crash_marker.txt` = not acceptance evidence (investigate, fix, re-run). Review: managed-memory trend "within bounds"; handle count start/end/peak flat-ish, not climbing; SQLite growth roughly linear with passes; count over 1 second; alarm events. Preserve the whole `batch_soak_<stamp>` folder with the release-candidate evidence set (SHA-256-tied via the export-verification record). Log: run ID, software version, engine + model configuration, dataset, operator.

### 8.6 Relationship to other stability evidence

`Readiness & QA > Soak Test` (in-app, Admin; Folder Camera Simulation, camera-source seam; Factory PoC 8-hour profile: §9) and `Readiness & QA > UI Stability Test` (WPF shell navigation; client-demo gate) complement this harness, which is the artifact for the 8-hour criterion at Stage 1 scope. Regression tests: `AOI_Monitor.Tests/BatchSoakTestServiceTests.cs` (smoke, memory-trend evaluation, stuck watchdog, unhandled exceptions, truthful labeling, CLI validation).

## 9. Factory acceptance test plan (Stage 1 vs hardware/MES gates)

Evidence plan for management/customer review, separating Stage 1 data validation from camera, lighting, robot, MES, and full factory automation readiness. Never treat simulation evidence as real equipment validation.

**Deployment profiles** (set in Settings before exporting a Go/No-Go package): Stage 1 Customer Data Validation; Stage 2 Camera Pilot; Stage 3 Robot Cell Pilot; Stage 4 MES Traceability Pilot; Full Factory Automation. The profile controls the readiness gates - the same evidence can be acceptable for Stage 1 and No-Go for later stages.

**Stage 1 dataset validation.** Inputs: customer-labeled image folder or manifest; OK and NG ground-truth labels; golden images when the Pixel Difference engine is used; defect class, side/view, ROI, refdes, lot, board model when available. Run §5 end to end (gates: `Docs/METRICS_VAL.md`); review aggregate accuracy, precision, recall, false-call rate, possible escapes, review burden, and the per-class/per-side/per-ROI breakdown; export and verify the package. Output: validation package manifest, customer validation HTML report, validation results CSV, validation breakdown CSV, dataset quality summary, export verification record.

**False positive / possible escape acceptance.** False-positive reduction must not hide possible escapes. False Call Reduction Workbench with customer-labeled data: compare candidate thresholds; record false call rate, possible escape count and rate, review burden estimate; recommendation status must be VALID for the selected operating mode; apply thresholds only as Engineer/Admin after confirmation. Gates and the INVALID/CONDITIONAL rule for insufficient ground truth: `Docs/METRICS_VAL.md`.

**Stage 2 camera/lighting acceptance.** Camera, per required view in the selected profile: select camera source + view configuration; run N frames per view; verify connect time, first-frame latency, average frame interval, dropped frame count/rate, trigger failures and timeouts; verify frame metadata (frame ID, camera ID, view type, timestamp, width, height, pixel format, source kind); confirm real hardware runs are distinguishable from folder/fake/null evidence; export camera acceptance JSON/HTML. Lighting: configure mode explicitly + per-view program names; confirm timeout and command template; run the sync test per view; record command latency and trigger-to-frame latency when supported; export lighting acceptance JSON/HTML. Simulated evidence supports workflow review only.

**Stage 3 robot/e-stop acceptance.** Robot cycle state machine + e-stop at the app boundary: confirm controller status and source kind; run load, move-to-inspect, inspection, unload, reset; measure each transition; verify invalid transitions rejected; verify e-stop blocking evidence when available; confirm reset returns to Idle; confirm audit events recorded; export robot acceptance JSON/HTML. Simulated or real per controller status - simulation is not safety certification nor proof of production robot movement.

**Stage 4 MES traceability acceptance.** Failed uploads must stay visible; production REST must be explicit: confirm MES mode (Not Connected, Mock, REST); confirm REST base URL, upload paths, timeout, authentication mode, max retry count, retry backoff; redacted settings expose no secrets; send a controlled traceability payload; success records an upload attempt; simulate a failed REST upload - a spool item is created; retry eligible items; retry success marks Sent; retry failure increments retry count and stores the last error; Admin-only abandon where applicable; export the MES queue report. Mock/local MES evidence is interface evidence only.

**In-app 8-hour soak (Factory PoC profile)** - full-factory evidence, distinct from §8: select profile Full Factory Automation; prepare the image or camera source; select the Factory PoC soak-test profile; confirm duration 480 minutes and output-folder free space; start and monitor live progress (elapsed, estimated remaining, pass count, fail count, status); do not close the app unless cancelling; at completion export/retain HTML and JSON reports. Acceptance: completed 8-hour requested duration; not canceled; no critical iteration errors; iterations persisted to SQLite; HTML and JSON reports exported; p95/max/avg inspection time and memory start/end/max recorded; source kind clearly states simulated or real camera source. Full Factory Automation readiness does not accept a simulated source as real camera evidence.

**Deliverables for management signoff.** Stage 1: validation package, dataset quality summary, validation report, validation CSV, breakdown CSV, export verification. Stage 2: Stage 1 plus camera and lighting acceptance reports. Stage 3: Stage 2 plus robot/e-stop acceptance report. Stage 4: Stage 3 plus MES queue/readiness report and REST/spool evidence. Full Factory Automation: Stage 4 plus 8-hour soak report and real hardware evidence where required. Signoff package: factory readiness summary HTML + JSON; package manifest; latest validation manifest; latest export verification summary; latest camera, lighting, robot, MES, soak evidence when available; README describing validated evidence, simulated evidence, unmet criteria, known limitations. Any No-Go category needs an owner, planned corrective action, and target date before pilot approval.

## 10. Customer deviation statement and Stage 1 sign-off

Hand this section to the customer with the Stage 1 validation package. It states, in customer-facing terms, every place the delivered software deliberately differs from the source specifications in `Docs/customer-specs/`, and why. Internal traceability for each row is the deviation register in `Docs/Customer_Spec_Gap_Audit.md` §15.

Nothing here is a defect report. Each item is a deliberate engineering decision recorded before delivery; the purpose of the sign-off is to confirm the customer accepts it, or to convert it into scoped work.

### 10.1 Deviations requiring customer acknowledgement

| # | Specification says | Delivered instead | Why |
|---|---|---|---|
| DEV-01 | Stage 1 deliverable: "AI model (.pt or .h5)" | **ONNX** single-file model format. No model artifact ships until training on customer data completes. | `.pt` and `.h5` both carry executable pickle/HDF5 payloads that execute code on load. Loading a model file received over email or a shared drive would be arbitrary code execution on the inspection PC. ONNX is a data-only graph format with equivalent portability and is the industry norm for deployed inference. |
| DEV-02 | TensorFlow / PyTorch engine with NVIDIA CUDA acceleration | **ONNX Runtime, CPU execution provider.** PyTorch is used offline for training only. | Measured Stage 1 frame-to-overlay is 12-14 ms p50-p95 on CPU against a 1000 ms budget — roughly 70x headroom. Adding a GPU dependency would raise per-station hardware cost and driver-support burden for no measurable benefit. GPU adoption is a tracked open decision, gated on Stage 2 live-camera timing evidence. |
| DEV-03 | Five main GUI modules | **13 focused workflow windows.** Every specified function is reachable; the shell aliases the specification's vocabulary onto the same routes. | Per-page density is a hard constraint of the factory HMI design (1920x1080, >= 14 pt text, >= 120x40 primary buttons). The five specified modules would each become a crowded multi-purpose page. The mapping table is in `Docs/Customer_Spec_Gap_Audit.md` §3. |
| DEV-04 | 12-column responsive grid | WPF star-sizing and adaptive panels, verified by a machine-enforced layout audit at 1920x1080 across 100 / 125 / 150 % DPI. | A 12-column grid is a web layout idiom with no WPF equivalent. The delivered substitute is checked automatically on every build (48 views x 3 DPI scales) rather than by inspection. |
| DEV-06 | Buttons >= 120x40 px | Enforced for **primary operator actions**. Secondary and mini buttons are 96x34-104x38. | Applying 120x40 to every button, including inline row actions, would force scrolling on dense pages. |
| DEV-07 | Auto-save after each board | Operator opt-in toggle, **default off**. | The delivered default is review-then-save, so an operator confirms a verdict before it enters the permanent record. Flipping the default is a one-line change if the customer prefers it. |
| DEV-09 | "Exported reports verified for accuracy" | Report **integrity** verification: per-file and aggregate SHA-256, PNG/PDF signature checks, required CSV headers and JSON fields, package manifest reconciliation. | Verifies that exported artifacts are complete and unaltered. It does **not** re-derive exported values from the database, so it does not detect a value that was computed wrongly before export. A content cross-check is scoped, separate work. |
| DEV-13 | Distinct "Short Circuit" defect class | Optically visible shorts are reported as **Solder Bridge**; the literal label `Short Circuit` normalizes onto it. | A short that an optical system can see *is* a solder bridge. Non-visible electrical shorts require ICT, which is outside every stage of the roadmap. (Excess Solder, previously also merged, is now its own class.) |

### 10.2 Recorded differences not requiring sign-off

Noted for completeness; each is equal to or stricter than the specification.

| # | Item | Delivered |
|---|---|---|
| DEV-05 | Font >= 14 pt | **Closed by compliance (M4, 2026-08-23):** the operator text floor was raised to a true 14 pt (18.67 DIP), audit-enforced at Fail severity, so the earlier DIP reinterpretation is withdrawn and the specification is met literally. No acknowledgement needed. |
| DEV-08 | Third verdict named "Warning" | Named `REVIEW`; the display accepts both. |
| DEV-10 | "Within 1 second per image" | P95 frame-to-overlay budget with a zero-tolerance over-threshold count — stricter than a per-image average. |
| DEV-11 | "8-hour continuous testing" | 8-hour PoC soak as the minimum; a 5-minute rehearsal is required first (§8). |
| DEV-12 | MES via REST **or** OPC UA | REST-over-HTTP implemented; OPC UA is a named, unimplemented boundary. Both are specification-allowed. |
| DEV-14 | Windows 10/11 **Industrial Edition** | Runs on generic Windows 10/11; Windows 11 IoT LTSC is the reference performance platform. |
| DEV-15 | Defect list X/Y | Percent-normalized coordinates plus board-millimetre columns when a calibration profile is selected. |

### 10.3 What Stage 1 sign-off does and does not cover

**Covers:** uploaded-image inspection, defect overlays and confidence, batch validation against a labelled manifest, accuracy / precision / recall / false-call / possible-escape metrics, CSV and annotated-image export, report integrity verification, recipe and threshold governance, roles and audit, and local SQLite traceability.

**Does not cover, and is not claimed by any Stage 1 artifact:** real camera or lighting hardware, 3D acquisition, side-view acquisition, robot handling, PLC or safety interlocks, production MES/ERP connectivity, and any full-factory automation claim. Stage 1 evidence generated from folder-based image sources is labelled as such in every report, and readiness gates refuse simulated evidence for later-stage claims.

### 10.4 Sign-off

By signing, the customer confirms they have reviewed §10.1 and accept the delivered behaviour, or have listed the items they want changed.

```text
Deviations accepted as delivered (list any exceptions): ______________________________________

Customer representative  Name: ______________________  Role: ______________________

                         Signature: _________________  Date: ______________

Supplier representative  Name: ______________________  Role: ______________________

                         Signature: _________________  Date: ______________

Stage 1 validation package ID: ______________________  Readiness report status: ______________
```

## Related documents

- `Docs/METRICS_VAL.md` - metric definitions, numeric acceptance criteria, completion matrix.
- `Docs/USER_MANUAL.md` - operator-facing screen reference.
- `Docs/RUNBOOK.md` - operations, storage roots, support bundles.
- `Docs/ROADMAP.md` - stage feature status (implemented vs planned).
- `Docs/Customer_Spec_Gap_Audit.md` - requirement IDs (e.g. ACC-11-03).
- `Docs/standard/00_Index.md` - the engineering standard binding all changes.

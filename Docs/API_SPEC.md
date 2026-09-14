OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor API Specification

Outward-facing interfaces of AOI Monitor: the `AOI_Monitor.Tools` command line, the machine-integration boundary contracts, central sync payloads, and evidence export formats. Contract rules are normative in Docs/standard VOL05 §22 and VOL11 §34–§35; this file records the current Stage 1 surface: boundary-only integrations, no production MES writes.

## AOI_Monitor.Tools command-line interface

`AOI_Monitor.Tools` provides a repeatable command-line workflow for Stage 1 customer dataset evidence, intended for engineering, QA, and evaluator reruns when manual WPF clicking would make evidence hard to reproduce. No command requires live camera, lighting, robot, PLC, 3D, MES, or ERP hardware.

```powershell
dotnet run --project AOI_Monitor.Tools -- <command> [options]
```

`-h`/`--help`/`help` prints usage; env var `AOI_MONITOR_STORAGE_ROOT` redirects the storage root; failures return non-zero, usage errors 2.

### stage1-exit

```text
AOI_Monitor.Tools stage1-exit --dataset <folder> --manifest <csv> --output <folder> --operator <id>
                              [--priority balanced|minimize-false-positives|maximize-defect-recall] [--allow-simulation]
```

Example:

```powershell
dotnet run --project AOI_Monitor.Tools -- stage1-exit `
  --dataset C:\AOI\Validation\CustomerDataset01 `
  --manifest C:\AOI\Validation\CustomerDataset01\customer_validation_manifest.csv `
  --output C:\AOI\Evidence\Stage1Exit `
  --operator ENG-042 `
  --priority maximize-defect-recall
```

`--priority` selects the detection policy thresholds (default `balanced`); the chosen policy is echoed on the console and carried into the evidence. All commands share one `--priority` parser, so a policy name is spelled the same everywhere.

Runs the same service boundaries as the WPF app: customer dataset preflight; Stage 1 batch validation against the manifest; model acceptance when an active ONNX model is configured and runtime-validated as `Ready`; false-call and possible-escape metric generation; Stage 1 customer validation package export; explicit export verification; Stage 1 factory readiness Go/No-Go package export; and a concise PASS/WARN/FAIL console summary.

Output layout under `--output` (the summary files are the stable index; some package services create timestamped subfolders):

- `stage1_exit_summary.json`
- `stage1_exit_summary.txt`
- `stage1_validation_package\...`
- `export_verification\...`
- `stage1_factory_readiness\...`

Production model boundary: if no active ONNX model is configured and validated as `Ready`, the command still runs the Stage 1 prototype batch path, but it reports `PROTOTYPE_ONLY` and does not claim production model readiness. Production model readiness is only claimed when `ModelAcceptanceService` records `PASS` evidence for the active ONNX model and supplied validation dataset. Pixel Difference Prototype Engine evidence can support Stage 1 workflow review, but it is not production model acceptance.

Failure behavior: returns non-zero and prints `FAIL` (for example `FAIL Dataset folder was not found: ...`) when required inputs are missing, preflight fails, export verification fails, the validation package fails acceptance, or the Stage 1 factory readiness package is No-Go.

### stage2-camera-pilot

```text
AOI_Monitor.Tools stage2-camera-pilot --output <folder> --operator <id> [--allow-simulation]
```

Assembles the Stage 2 camera pilot evidence package (Stage 1, camera, lighting, 3D-profile statuses; factory readiness; export verification), recording simulation-evidence presence and real-hardware validation. `--allow-simulation` produces an explicitly labeled dry-run package.

### learn-from-images

```text
AOI_Monitor.Tools learn-from-images --project-folder <folder> --output <folder> --operator <id> [--false-call-target 0.05] [--board-model <name>]
```

Trains a learned PCB visual model and exports a client evidence package; folder convention and worked example: `Docs/DATA_PIPELINE.md`.

### client-image-learning-demo

```text
AOI_Monitor.Tools client-image-learning-demo --output <folder> --operator <id> [--synthetic] [--project-folder <folder>] [--false-call-target 0.05]
```

Runs the image-learning demo from a real `--project-folder` or `--synthetic` generated data. Synthetic output proves workflow capability only — not customer acceptance, not production model certification.

### prepare-dataset

```text
AOI_Monitor.Tools prepare-dataset --source <folder> --output <folder>
      [--layout auto|mvtec|visa|class-folders|paired-template]
      [--golden auto|paired|per-board|from-normal|none] [--golden-folder <folder>]
      [--board <name>] [--lot <id>] [--max-ok <n>] [--max-ng-per-class <n>]
      [--seed <n>] [--emit-learning]
```

Converts a third-party PCB image dataset into the Stage 1 dataset contract (`images/`, `golden/`, `customer_validation_manifest.csv`), so a public or customer dataset can be run without hand-writing a manifest for hundreds of images. `--emit-learning` additionally writes the image-only learning role folders (`golden/`, `ok_learning/`, `ok_validation/`, `inspection/`, `ng_validation/`).

**It downloads nothing.** It reads `--source` and writes `--output`; the source folder is never modified. Licensing and redistribution rights for the source images are the operator's responsibility.

Recognised layouts: MVTec-AD style (`train/good`, `test/<defect>`), VisA style (`Data/Images/Normal|Anomaly`), one-folder-per-class, and paired sample/template (`<stem>_test` + `<stem>_temp`, with the annotation sidecar deciding OK vs NG). Auto-detection falls back to class folders and reports what it saw rather than guessing.

Golden assignment: `paired` (per-sample template), `per-board` (longest name-prefix match against `--golden-folder`), `from-normal` (promote a known-good image — **only sound for registered captures**, and the report says so), or `none` (learned/ONNX engines only).

The report (`prepare_dataset_report.txt`/`.json`) states the detected layout, per-class counts and their taxonomy mapping, every defect class not in the active taxonomy, byte-identical duplicate images, and each default preflight gate the dataset will fail — before a run is attempted. Sampling caps are seeded, so the same source always yields the same prepared dataset.

Exit codes: `0` prepared with no warnings, `1` prepared with warnings (read them first), `2` usage or input error.

### stage1-readiness

```text
AOI_Monitor.Tools stage1-readiness [--dataset <folder>] [--manifest <csv>] [--output <folder>] [--p95-target-ms <ms>]
```

Evaluates the Stage 1 readiness gate — the same service behind `Readiness & QA > Stage 1 Readiness` — against persisted evidence, prints every check with its evidence and next action, and writes `stage1_readiness_report.html`, `.pdf`, and `.json`. Omit `--dataset`/`--manifest` to fall back to the latest persisted batch run, then to the generated `SampleData/DemoSet_Quick` dataset.

Exit codes: `0` PASS, `1` CONDITIONAL, `2` FAIL, `3` usage error. CONDITIONAL has its own code so a pipeline can distinguish "evidence incomplete" from "evidence contradicts readiness".

The gate reads evidence that earlier steps persist, so it is only meaningful after `stage1-exit`, `benchmark`, and `record-build-evidence` have run. `Scripts/run-stage1-testing.ps1` runs all four in order.

### record-build-evidence

```text
AOI_Monitor.Tools record-build-evidence [--configuration Release] [--hygiene PASS|FAIL] [--restore PASS|FAIL]
                                        [--build PASS|FAIL] [--test PASS|FAIL] [--publish-validation PASS|FAIL]
                                        [--test-results <path>] [--operator <id>]
```

Records a build/test/quality-gate outcome as persisted evidence, which the readiness gate's "App build/test evidence" check reads. The record carries the git commit, configuration, machine name, and operator.

The command **does not run or infer the gates** — it records the statuses the caller supplies. A tool that asserted its own PASS would be evidence of nothing. Run `pwsh Scripts/run-quality-gates.ps1 -Configuration Release` first and pass the real outcome; defaults are `PASS`, so state a failure explicitly.

### Additional commands

```text
AOI_Monitor.Tools camera-adapter-validate --adapter-folder <path> [--settings-json <path>] --output-folder <path>
AOI_Monitor.Tools import-image-learning-project --project-folder <folder> --operator <id> --evidence-mode CustomerData|InternalDemo|SyntheticDemo
AOI_Monitor.Tools benchmark --images <folder> --output <folder> [--golden <image>] [...]
AOI_Monitor.Tools batch-soak --images <folder> --output <folder> --operator <id> [...]
```

`camera-adapter-validate` validates a vendor camera adapter package offline (real-hardware vs simulated-evidence flags). `import-image-learning-project` imports a project folder into the vault with an explicit evidence mode. `benchmark` measures inspection timing; `--golden` selects the operator golden-compare workload, otherwise the lighter no-reference path runs (the report says which). `batch-soak` is the headless Stage 1 soak: default profile smoke (5 minutes), `--profile eight-hour` is the customer 8-hour PoC soak, and without `--engine` the configured engine selection is used (pixel-difference prototype unless a model is configured and ready); exit codes 0 = PASS, 1 = FAIL or CANCELED, 2 = usage error. Run each with `--help` for the full optional-flag set (benchmark: count, warmup, threshold-ms, priority; batch-soak: manifest, profile, duration, delay, engine, priority, max-passes, stuck-timeout, memory-trend fail thresholds, board/lot metadata, persistence opt-out).

### Evidence limits

Folder simulation, null adapters, fake adapters, generated test images, and prototype-only batch evidence are not real camera readiness. Stage 2 camera pilot readiness still requires accepted vendor camera acquisition, real frame metadata, lighting synchronization evidence, real 3D acquisition when in scope, and real-camera performance evidence.

## Integration boundary contracts (boundary-only)

Planned machine-integration contracts. These are architecture boundaries only: the current build does not control real hardware, write to production MES/ERP, or monitor a real emergency-stop circuit. A clearly labeled Mock MES REST mode exists only for traceability-flow demonstration.

### Status vocabulary

| Status | Meaning |
| --- | --- |
| Not Connected | No live integration is configured. This is the default safe PoC state. |
| Simulated | A future simulator or test double is active. This should not be presented as real hardware. |
| Error | The endpoint exists but reports a fault or unusable state. |
| Ready | A future implementation has connected and passed its own readiness checks. |

### Contracts and implementations

Interfaces live in `AOI_Monitor/Services/IntegrationContracts.cs` (`MockMesClient` in `AOI_Monitor/Services/MockMesClient.cs`); each has a safe null default, plus mock/simulated variants for Stage 1 demos:

- `ILightingController` → `NullLightingController`
- `IRobotController` → `NullRobotController`; demo: `SimulatedRobotController`
- `IPlcSafetyController` → `NullPlcSafetyController`; demo: `SimulatedPlcSafetyController`
- `IMesClient` → `NullMesClient`; demo: `MockMesClient`
- `ITraceabilityUploader` → `NullTraceabilityUploader`
- `IOpcUaMesClient` → `NullOpcUaMesClient` (boundary only; no OPC UA package or production implementation is bundled)
- `ICentralProductionDatabaseClient` → `NullCentralProductionDatabaseClient`
- `IEmergencyStopMonitor` → `NullEmergencyStopMonitor`; demo: `SimulatedEmergencyStopMonitor`, `PlcEmergencyStopMonitor` (PLC-derived)

The null implementations always report `NotConnected` and return non-accepted command results. They do not call vendor SDKs, open network connections, write to MES, or control equipment.

`MockMesClient` reports `Simulated`. It can POST a MES-style traceability payload to a configured mock REST endpoint, or write the same payload to local JSON when no endpoint is configured. It is not production MES/ERP authentication or writeback (attempts audit to `MesUploadAttempts`/`MesSpoolQueue`).

`SimulatedRobotController` reports `Simulated` unless the software emergency-stop simulation is active, in which case it reports `Error`. It supports Load, Inspect, Unload, Reset, and emergency-stop simulation for Stage 1 workflow demonstrations. It never calls a vendor SDK, PLC, handler, conveyor, robot, or safety circuit.

### Commands and stage ownership

Command models are deliberately small for later vendor/customer protocol mapping: `LoadCommand`, `InspectCommand`, `UnloadCommand`, `UploadResultCommand`, `UploadImageCommand`, `TraceabilityPayload`.

- Stage 2: lighting controller (recipe/view-based lighting program selection) and live camera/3D hardware sources.
- Stage 3: robot, handler, PLC, emergency-stop/safety monitoring, and machine action handshakes.
- Stage 4: MES/ERP authentication, traceability upload, result upload, image upload, and production database integration.

### Readiness panel

The readiness panel shows Lighting, Robot, MES / Traceability, and E-Stop Monitor. In the safe default PoC state these show `Not Connected`; Mock MES REST mode and Simulated Robot / Handler show `Simulated` and are labeled as mock/demo behavior. The UI must not imply that real robot, production MES, lighting, PLC, or safety hardware is connected until a future implementation replaces the null, mock, or simulated services and passes readiness checks.

### Future implementation guidance

1. Implement the existing interfaces in separate classes.
2. Keep vendor SDK code isolated behind those implementations.
3. Preserve the null implementations for offline demos and safe test runs.
4. Report truthful status values.
5. Return friendly command results instead of throwing for normal connection or validation failures.
6. Log operator-visible failures in the app event/review log.
7. Avoid enabling machine-control UI until the integration reports `Ready` and has passed acceptance testing.

## Central sync payload shape

Local SQLite is the offline source of truth; central sync is optional reporting for management aggregation across stations. Modes: Disabled; FileDrop (JSON payloads to a configured folder); RestApi (boundary only in this build, no production client installed); PostgreSqlBoundary (interface boundary only, no Npgsql package or production database writer bundled). Payload types: InspectionResult (defect detail joins a future expansion), ReviewEvent, ValidationPackage, ExportVerification, and future acceptance report payloads; full local-to-central mapping table: `Docs/DATA_PIPELINE.md`.

Raw customer images are not uploaded by default; image references require explicit opt-in in sync settings. Secrets and endpoints are redacted from exported queue reports when configured. Failures leave queue items pending for retry and never remove or modify local SQLite records.

## Evidence export formats

The app exports evidence as CSV, PNG, PDF, JSON, HTML, and TXT. Evidence used for readiness or client packages must be verified: exports are recorded in `ExportHistory` and verified through `ExportVerification` (status, SHA-256 checksums, messages). Storage and export rules: Docs/standard VOL05 §37.

## Related documents

- `Docs/DATA_PIPELINE.md` — data flow, learning workflow, ONNX training, schema, sync mapping table.
- `Docs/VALIDATION.md` — evidence gates the CLI packages feed.
- Docs/standard VOL05 §22/§37, VOL11 §34–§35 — normative contract rules.

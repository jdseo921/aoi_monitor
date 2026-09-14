OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor

AOI Monitor is a Windows WPF desktop prototype for PCBA automated optical inspection review workflows. It gives operators a simplified local console organized around focused workflow menus: Home, Board & Images, Run Inspection, Golden Compare, Defect Review, Recipe Rules, AI / Models, Yield Analytics, Export & Trace, Readiness & QA, Calibration, 3D Profile, Hardware Readiness, and System Settings.

The application currently demonstrates the review loop with local files, local SQLite records, and clearly labeled demo placeholders where production data sources are not yet implemented. It can load a sample PCB image and a golden reference image, run the deterministic Pixel Difference Prototype Engine, optionally run a configured ONNX ML Model, produce an `OK`, `REVIEW`, or `NG` verdict, record disposition actions, collect candidate samples for local training-set export review, and write local export artifacts. It is not yet connected to live AOI hardware, cameras, PLCs, robots, conveyors, a centralized production database, or a bundled trained production ML model.

Home includes an explicit readiness panel for Database, Image Vault, Inspection Engine, Camera, Robot, and MES/ERP. Stage 2 Camera Pilot architecture includes camera adapter boundaries, plugin loading, and camera/lighting/3D acceptance services, while real vendor adapters and real hardware acceptance evidence remain open. Stage 3 (robot/handler control) and Stage 4 (production MES/ERP authentication and traceability) are planned boundaries. A clearly labeled 2D calibration profile workflow, a clearly labeled Mock MES REST mode, and a Sample-Data-Mode 3D viewer exist for Stage 2+ planning; none of them are live hardware or production integrations.

This project is standards-aligned, not formally ISO, IEC, ISA, safety, cybersecurity, or regulatory certified. Simulated, mock, and boundary-only evidence is always labeled as such.

## Documentation Map

The documentation is deliberately consolidated into a small fixed set. Do not add new standalone markdown documents; extend the right one below (rule recorded in [AGENTS.md](AGENTS.md)).

Root:

- [README.md](README.md) — this overview and quickstart.
- [CONTRIBUTING.md](CONTRIBUTING.md) — dev setup, local checks, CI quality gates, branch protection, contributor checklist.
- [AGENTS.md](AGENTS.md) — binding engineering rules for AI agents and contributors ([CLAUDE.md](CLAUDE.md) includes it).
- [DESIGN.md](DESIGN.md) — the design contract merged with the factory HMI style guide, quality baseline, scroll rules, and competitive HMI reference patterns.

`Docs/`:

- [ARCHITECTURE.md](Docs/ARCHITECTURE.md) — layered architecture, integration boundaries, vendor adapter guide, UI service coverage, Stage 2–4 seam inventory.
- [DATA_PIPELINE.md](Docs/DATA_PIPELINE.md) — image intake → registration → inspection engines → storage (SQLite schema, image vault) → export/central sync; image-only learning and ONNX model training.
- [API_SPEC.md](Docs/API_SPEC.md) — the `AOI_Monitor.Tools` CLI, machine-interface boundary contracts, sync payloads, export formats.
- [DEPLOYMENT.md](Docs/DEPLOYMENT.md) — factory floor and customer-PC provisioning, packaging, and the hardware-in-the-loop commissioning checklist.
- [RUNBOOK.md](Docs/RUNBOOK.md) — on-site IT troubleshooting, backup/restore/rollback, logs, crash reports, support bundles.
- [CALIBRATION.md](Docs/CALIBRATION.md) — coordinate systems, current prototype calibration behavior, planned Stage 2+ camera/lighting/optics alignment.
- [METRICS_VAL.md](Docs/METRICS_VAL.md) — inspection and AI-model acceptance criteria (false-call rate and possible-escape gates) and the completion-assessment methodology.
- [SECURITY.md](Docs/SECURITY.md) — image data retention, network posture, roles; maps to the canonical standard volumes.
- [VALIDATION.md](Docs/VALIDATION.md) — manual test plan, image-learning quickstart, sample-dataset demo, customer dataset validation kit, client test kit, soak test procedure, factory acceptance test plan.
- [ROADMAP.md](Docs/ROADMAP.md) — the four delivery stages, current Stage 1 status, milestone history, dated review verdicts.
- [USER_MANUAL.md](Docs/USER_MANUAL.md) — operator manual and per-window feature reference.
- [Requirements_Traceability_Matrix.md](Docs/Requirements_Traceability_Matrix.md), [Customer_Spec_Gap_Audit.md](Docs/Customer_Spec_Gap_Audit.md), [Standards_Traceability_Matrix.md](Docs/Standards_Traceability_Matrix.md) — kept traceability records with stable requirement IDs.

Annexes:

- `Docs/standard/` — the canonical **AOI Software Architecture, Secure Development, and Change-Control Standard** (start at [00_Index.md](Docs/standard/00_Index.md)); machine-validated by `Scripts/standard_catalogue.py` (FF-STD-01).
- `Docs/customer-specs/` — customer source specifications (kept verbatim as the requirements baseline).
- `Templates/*/README.md`, [SampleData/README.md](SampleData/README.md) — colocated build/usage notes.

## Project Layout

- `AOI_Monitor/` - WPF application source (`net10.0-windows`).
- `AOI_Monitor/Views/` - page-specific UI and workflow code.
- `AOI_Monitor/Services/` - shared workflow state, image analysis, and machine-interface exports.
- `AOI_Monitor/Models/` - AOI workflow and export contract models.
- `AOI_Monitor/Data/` - local SQLite initialization and image-vault persistence.
- `AOI_Monitor.Tools/` - headless evidence CLI (see [Docs/API_SPEC.md](Docs/API_SPEC.md)).
- `AOI_Monitor.Tests/`, `AOI_Monitor.UiTests/` - unit and UI test projects.
- `Docs/` - the consolidated documentation set described above.
- `Scripts/` - local build, quality-gate, and release packaging scripts.
- `Templates/` - vendor adapter starting-point projects (camera, lighting, robot, robot+PLC).
- `SampleData/` - instructions and generator for small local demo images.

## Run

Requirements: Windows, and a .NET SDK with Windows desktop/WPF support for the project's `net10.0-windows` target.

```powershell
cd AOI_Monitor
dotnet run
```

To build without launching:

```powershell
dotnet build AOI_Monitor\AOI_Monitor.csproj
```

## Tests And Quality Gates

```powershell
dotnet test AOI_PCB_Database.slnx
```

The test fixture uses an isolated temp folder per run, so tests do not write into the real `%LOCALAPPDATA%\AOI_Monitor` storage. For the full local gate loop (hygiene, build, tests, code quality, HMI layout audit, navigation performance, export verification, standards traceability, package validation):

```powershell
pwsh Scripts/run-quality-gates.ps1 -Configuration Release
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the complete developer workflow.

## Publish A Shareable PoC Package

For a client/evaluator handoff package:

```powershell
pwsh Scripts/prepare-client-test-package.ps1 -Zip
```

For direct packaging:

```powershell
.\Scripts\publish.ps1 -SelfContained
```

The release folder is intended to be zipped and shared. It intentionally excludes local SQLite databases, image vaults, customer images, generated exports, overlays, customer packages, and `%LOCALAPPDATA%\AOI_Monitor` runtime data. Details: [Docs/DEPLOYMENT.md](Docs/DEPLOYMENT.md).

## Basic Workflow

1. Open Run Inspection from the Home workflow map.
2. In Board & Images, choose a sample PCB image with Open Record, then a golden reference image with Compare Golden.
3. Review the generated score, verdict, evidence, and hotspot in Golden Compare; use the Large Image buttons for zoomed inspection.
4. Use Defect Review to confirm, mark false calls, hold for review, or queue local candidate samples.
5. Use Export & Trace to inspect SQLite history, create CSV exports, build customer packages, and open database health.

The formal verification script, demo walkthroughs, and acceptance criteria live in [Docs/VALIDATION.md](Docs/VALIDATION.md) and [Docs/METRICS_VAL.md](Docs/METRICS_VAL.md).

## Image-Only PCB Learning

AOI Monitor includes a Stage 1 image-only PCB learning workflow for uploaded image folders (no defect labels, bounding boxes, model files, or camera hardware required). Use `AI / Models > AI Training Setup` for the guided GUI, or the CLI:

```powershell
dotnet run --project AOI_Monitor.Tools -- learn-from-images `
  --project-folder <folder> `
  --output <folder> `
  --operator <id> `
  --false-call-target 0.05
```

Synthetic demo output proves workflow capability only. Customer acceptance requires customer/evaluator images and reviewer signoff; Stage 2 live camera validation remains separate. Full workflow documentation: [Docs/DATA_PIPELINE.md](Docs/DATA_PIPELINE.md) and [Docs/VALIDATION.md](Docs/VALIDATION.md).

## Stage 1 Testing

The complete Stage 1 evidence chain runs headlessly. One command generates the demo dataset, runs the exit-evidence workflow, the performance benchmark, and the build-evidence record, then evaluates the readiness gate:

```powershell
pwsh Scripts/run-stage1-testing.ps1 -Operator <your-id>
```

It exits `0` when the Stage 1 readiness gate reports PASS and `1` on CONDITIONAL, and prints every check with its evidence and next action. Individual commands, expected demo-dataset results, and the customer deviation statement with sign-off lines: [Docs/VALIDATION.md](Docs/VALIDATION.md) §4.0 and §10.

Stage 1 covers uploaded-image validation only. No Stage 1 artifact claims real camera, lighting, 3D, robot, PLC safety, MES/ERP, or factory-automation readiness.

## Stage 1 Batch Soak Test

For the 8-hour continuous-operation acceptance criterion, a headless soak harness loops the real batch-inspection pipeline over an image folder and emits HTML/JSON/CSV stability evidence:

```powershell
dotnet run --project AOI_Monitor.Tools -c Release -- batch-soak `
  --images <folder> `
  --output TestResults/batch-soak `
  --operator <id> `
  --profile eight-hour
```

Use `--profile smoke` for a 5-minute rehearsal. Reports are labeled as uploaded-image pipeline evidence only, never camera or factory-automation readiness. Full procedure: [Docs/VALIDATION.md](Docs/VALIDATION.md).

## Local Database And Image Vault

On startup, the app creates its local PoC persistence store automatically, by default under `%LOCALAPPDATA%\AOI_Monitor\` (SQLite database `aoi_monitor.sqlite`, managed `image_vault\`, training candidates under `image_vault\training\`). Admins can change the storage root from `Settings`; existing runtime folders are not deleted or migrated automatically. If the OS local-app-data folder cannot be resolved, the app falls back to a `data/` folder beside the executable.

Log retention: inspection results, defects, review events, audit events, and export history older than a configurable window (default 30 days) are archived into the recoverable `LogArchive` table and purged from live tables at startup; configure or disable this in `Settings > Basics > Data Retention` (Admin). Schema details: [Docs/DATA_PIPELINE.md](Docs/DATA_PIPELINE.md). Backup/restore and troubleshooting: [Docs/RUNBOOK.md](Docs/RUNBOOK.md).

## Repository Hygiene

This repository should contain source code, XAML, docs, and small non-confidential instructions only. Do not commit build outputs, local SQLite databases, image vaults, training-set exports, generated packages, or customer/production images. Local runtime data is generated automatically. Before committing:

```powershell
pwsh Scripts/check-repo-hygiene.ps1
```

## Current State

This is a functional local prototype focused on operator review flows, local SQLite-backed evidence, and file-based exports. The main production gaps are centralized production database integration, real machine/hardware integration, expanded persistent workflow storage, and a production-grade inspection model. Current stage status and milestone history: [Docs/ROADMAP.md](Docs/ROADMAP.md).

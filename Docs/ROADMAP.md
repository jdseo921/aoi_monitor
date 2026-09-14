OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor Roadmap: Stages, Status, and Milestone History

For client, evaluator, and engineering review when planning work or assessing stage-exit claims: the four delivery stages, the current readiness assessment and evidence gates, the condensed history, and the review record. The staged plan traces to `Docs/customer-specs/AOI_PoC_Development_Roadmap_and_Commercialization_Plan.md`; per-requirement status: `Docs/Customer_Spec_Gap_Audit.md`, `Docs/Requirements_Traceability_Matrix.md`.

## The Four Delivery Stages

| Stage | Scope | Status |
|---|---|---|
| **1 — Image Upload & AI Learning** (8 wks) | Upload PNG/JPG, offline learning/inference, defect overlays, batch validation, CSV/PNG/PDF evidence export, customer validation | **Code-complete** (all customer-spec bullets passing 2026-07-13); exit evidence-gated: real customer dataset run + reviewed validation package |
| **2 — AI Vision Camera Integration** (8 wks) | GigE/USB3 cameras, lighting control, real-time acquisition, Top/Side/Bottom views, on-site validation | Architecture seams ready (adapter templates, folder-camera simulation, 2D calibration screens); no real hardware yet |
| **3 — Robot Integration** (12–16 wks, 2027) | Load→Inspect→Unload automation, trigger sync, safety interlock/E-stop | Software simulation panel only (simulated cycle + E-Stop boundary) |
| **4 — MES/ERP Integration** (12–16 wks, 2027) | REST/OPC UA (target: IPC-CFX), lot traceability, MES authentication | Mock MES boundary only (mock REST + spool queue) |

**Commercial plan.** 2026: Phase 1 (Stages 1–2) PoC + customer validation. 1Q 2027: first release (1 customer, 5–10 licenses). 2027: Korea-first rollout, then ASEAN/Japan/Europe.

**Boundary statement.** The current PoC is suitable for Stage 1 workflow validation, customer evidence review, and offline demonstration with local files. It is not a production AOI controller until the planned hardware, machine, MES/ERP, security, and validated ML-inference stages are implemented and accepted. The project is standards-aligned, not formally certified.

**한국어 요약.** 이 프로젝트는 "사진만으로 검증 → 실제 카메라 연결 → 로봇 자동화 → 공장 시스템(MES/ERP) 연동"의 네 단계로 성장합니다. 1단계는 코드상 완성되었고, 남은 일은 실제 고객 보드 사진으로 성능을 입증하고 증빙 패키지를 검토받는 것입니다. 2단계는 연결 통로만 준비(실물 장비 없음), 3단계는 시뮬레이션, 4단계는 모의(Mock) 연동만 제공합니다.

### Stage 2 — planned camera/optics work

The blocked hardware gates are enumerated under Stage 2 blockers below. Planned engineering beyond those gates: vendor-adapter connection status/diagnostics; exposure, gain, acquisition-settings, and error-recovery validation against the selected camera model; camera-lighting trigger synchronization; production image calibration and coordinate mapping with validated hardware images and fixtures; real 3D camera integration for live height/coplanarity inspection.

Folder Camera Simulation keeps Stage 1 workflows testable with a clean seam for Stage 2 hardware sources; it is not real camera validation. The lighting boundary can exercise null, simulated, TCP, or serial controller paths, but null/simulated lighting evidence does not control or validate real lighting hardware.

### Stage 3 — planned robot/handler work

Robot/handler communication layer; PLC or machine interface integration; board-present and trigger handshakes; pass/fail/review decision output to machine control; interlock and stop-line policy support; safe retry and fault recovery; mapping inspection coordinates to robot/handler coordinates; production event logging for machine actions.

Machine-interface JSON exports are evidence artifacts only — they control no hardware. `IRobotController` / `IEmergencyStopMonitor` ship null implementations and a labeled software simulator (Load, Inspect, Unload, Reset, emergency-stop interruption, cycle timing); it sends no commands to real equipment.

### Stage 4 — planned MES / ERP work

MES authentication or single sign-on replacing the local role selector with production identity; work order, lot, serial number, and board route validation; production recipe download and revision control; inspection result, defect, and disposition upload; audit trail export to production systems; ERP/quality-system reporting hooks; centralized configuration and path management; production database integration.

The local user/role model records user ID and role in audit rows, but it is not MES authentication. `IMesClient` / `ITraceabilityUploader` ship null implementations plus a mock REST implementation: mock mode generates local JSON traceability payloads and can POST to a configured test endpoint, but it is not production MES/ERP authentication, traceability, or writeback.

## Current Status: Stage 1 Exit / Stage 2 Camera Pilot

Stage 1 workflow capability is implemented in the local prototype and Stage 2 camera-pilot architecture is present. Stage 1 exit and Stage 2 hardware readiness remain evidence-gated. Do not describe Stage 2 as complete.

Public-dataset engineering validation (2026-09-14/15): the headless Stage 1 evidence chain reaches a 15/15 readiness PASS at the 97 % acceptance gates on the synthetic demo set and on two public PCB defect datasets converted to ROI tiles — DeepPCB (100 tiles; decided-row metrics only, 48 of 100 tiles end REVIEW under the default bands, so it demonstrates the pipeline rather than the acceptance gates) and PKU PCB_DATASET (calibrated threshold profile on a 124-tile split, verified at 100 % with zero REVIEW on a 76-tile evaluation split and a 50-tile frozen hold-out; binary OK/NG scope, synthesized defects). This is pipeline proof on real board imagery, not customer acceptance; the exit blockers below stay open until the customer/evaluator dataset run and §10 sign-off.

### Evidence boundary

Simulation, folder-source, null-adapter, fake-adapter, sample CSV, mock REST, and boundary-only evidence is not real hardware readiness: Folder Camera Simulation is not real camera acquisition; `NullVisionCameraAdapter`, fake test adapters, and plugin-template adapters are not vendor camera acceptance; simulated/null lighting or command-format tests without physical controller confirmation are not real lighting sync evidence; 3D sample CSV evidence is not live 3D acquisition; Mock MES REST and local JSON payloads are not production MES/ERP acceptance; software-only robot/handler simulation is not robot, PLC, conveyor, or safety-circuit acceptance.

### Stage 1 — implemented

- Fourteen focused workflow windows — Home + 13 destinations (window list and per-window procedures: `Docs/USER_MANUAL.md`) — with local Operator/Engineer/Admin roles, route authorization, and audited access-denied events.
- Image/batch import into a managed vault (SQLite records, SHA-256 hashes); golden comparison with defect overlays; disposition logging with false-call / possible-escape support and candidate export review; recipe ROI editing, thresholds, revisions, recipe lock, centroid CSV auto-ROI import.
- Pixel Difference Prototype Engine default; optional ONNX Runtime inference for a valid local model/tensor/threshold/label-map configuration (readiness-tested, safe REVIEW fallback); results, defects, reviews, recipes, audits, exports, readiness, and acceptance evidence persisted in SQLite with versioned migrations.
- Batch validation with manifests, confusion metrics, per-image timing (1 second target warnings), customer validation reports.
- AI Training Setup image-only learning producing Learned PCB Visual Model v1 artifacts and the visual learning report (image groups and workflow: `Docs/USER_MANUAL.md`); one-command packages `learn-from-images` and `client-image-learning-demo` (labeled synthetic demo output).
- 2D calibration profiles (approximate Stage 2 preparation); labeled Simulated Robot / Handler panel and Mock MES mode; Admin soak test; machine-interface JSON evidence exports.
- Stage 1 customer evidence package export with missing-evidence warnings; export verification records; factory-style audit trail; Stage 1 exit evidence CLI (preflight, batch validation, model acceptance, export verification, readiness package).
- Build/test, HMI layout audit, navigation performance, standards traceability, and quality-gate evidence via repository scripts; hardware/camera/lighting/robot/3D/MES/central-sync/readiness service boundaries keep Stage 1 local evidence separate from later-stage factory evidence.

### Stage 1 — deliberately deferred (not production functionality)

- No trained production ML model bundled; ONNX ML Model inference is claimed only when a configured local model loads and inference succeeds.
- No real AOI camera acquisition, real lighting controller, or real 3D camera / live height-map acquisition; no real robot, handler, conveyor, PLC, or safety-circuit control (software simulation only).
- No MES/ERP authentication or production traceability — Mock MES is clearly labeled mock mode only.
- No centralized production database service; no production installer or auto-update mechanism; no hardened cybersecurity model beyond local role separation.
- No production calibration workflow for real optics, lighting, 3D height, or robot coordinates — 2D calibration profiles are approximate Stage 2 preparation only.
- Remaining placeholder panels are labeled demo/prototype data; SQLite-backed health and summary counts use local PoC records.

### Stage 1 exit blockers (open)

Exit is not feature presence; it requires current, reviewable evidence from the customer/evaluator dataset and release candidate:

- Real customer dataset evidence — run the customer dataset through the validation workflow with a manifest, truth labels where available, and a generated validation package.
- Image-only customer learning evidence — run the customer Golden / OK Learning / OK Validation / Inspection groups through AI Training Setup or `learn-from-images`; review `visual_learning_report.html`, OK Validation image count, false-call calibration, anomaly overlays, possible-escape status.
- Model acceptance evidence — record an accepted model or explicitly scope the exit to Pixel Difference Prototype Engine / configured local model evidence; a configured ONNX path alone is not a production model claim.
- Learned visual model acceptance — synthetic/internal demo learned models show workflow capability only; customer acceptance requires customer images and review of the exported evidence.
- False-call and possible-escape evidence — false calls, possible escapes, missed-defect annotations, operator dispositions, and any approved operating threshold profile.
- Export verification evidence — verify the CSV, HTML, JSON, PNG, PDF, and package artifacts used for the handoff; unresolved verification errors block exit.
- Build/test evidence — preserve passing hygiene, build, test, quality-gate, HMI layout audit, navigation performance, and package-validation artifacts for the release candidate.

Numeric acceptance criteria: `Docs/METRICS_VAL.md`. Validation procedures and the exit evidence CLI: `Docs/VALIDATION.md`.

### Stage 2 camera-pilot architecture already implemented

Architecture confidence that supports a pilot, not hardware readiness (details: `Docs/ARCHITECTURE.md`): `GenericVisionCameraSource` runs a vendor adapter behind the camera-source workflow; `IVisionCameraAdapter` / `IVisionCameraAdapterFactory` / `IVisionDeviceDiscovery` define the GigE/USB3 vendor SDK boundary; `VisionCameraPluginLoader` / `CameraAdapterPluginService` load manifest-based external adapter plugins, failing safely to diagnostic null adapters, with adapter templates and tests keeping vendor SDK binaries out of the main app; `CameraAcceptanceTestService`, `LightingAcceptanceTestService`, and `Profile3DAcceptanceTestService` record per-view camera/lighting/3D acceptance evidence (frame and trigger timing, metadata validation, dropped frames, units/pitch/invalid-height counts) with `IsRealHardware` and simulated-vs-real labeling; `FactoryReadinessService` provides the `Stage2CameraPilot` profile (camera, lighting, 3D profile, export verification, build/test, known-limitation categories); `CompletionAssessmentService` scores real acceptance separately from simulated boundary exercise; and `CameraSourceContractTests` locks the runtime drop-in seam (stub adapter through `GenericVisionCameraSource` / `ICameraSource` exactly as Run Inspection consumes it — StartAcquisition, GetNextFrame, StopAcquisition — with a safe not-connected default). None of this satisfies a real camera acceptance blocker.

### Stage 2 blockers (open)

- Vendor camera adapter — no customer-selected vendor SDK adapter accepted; a real adapter must be built and packaged externally through the plugin boundary.
- Real camera metadata — no accepted real camera run proving stable physical camera IDs, view assignment, frame IDs, UTC capture timestamps, dimensions, pixel format, source kind, and non-simulated frame evidence for Top/Side/Bottom.
- Real lighting sync — no accepted physical lighting-controller run proving selected lighting programs, controller acknowledgement, command latency, and camera trigger-to-frame timing under the intended profile.
- Real 3D acquisition — no accepted real 3D sensor run proving live height/profile acquisition, calibrated units, valid dimensions, pitch, invalid-height limits, and source diagnostics.
- Real performance benchmark — frame-to-overlay timing with the accepted real camera source and selected inspection path.
- Customer/factory pilot package — Stage 2 evidence exported in a readiness package keeping simulation evidence separate from real hardware evidence.
- Image-only learning does not close Stage 2 hardware blockers; it remains separate from real camera, lighting, 3D, robot, safety, and MES acceptance.

### Required next evidence (in order)

1. Complete Stage 1 exit evidence for the customer dataset and release candidate.
2. Select the camera, lighting, and 3D hardware scope for the pilot.
3. Build and install vendor/customer adapter plugins outside the main app repository.
4. Camera acceptance with real hardware for every required view.
5. Lighting acceptance with the physical controller and camera timing where applicable.
6. 3D profile acceptance with the real sensor if 3D is in pilot scope.
7. Performance benchmark evidence against the real camera source.
8. Export and review the factory readiness package for `Stage2CameraPilot`.

## Development History (2026-05-17 → 2026-07-13)

Dates from git history; off-repo events approximate. Full record: git history (`Docs/Stage1_Development_History.md` at commit b2c4616).

| Dates (2026) | What landed |
|---|---|
| 05-17 → 06-10 | Inception and prototype growth: repository, first WPF prototype, first Korean text (bilingual EN/KO from day one), first runtime test, first documentation |
| 06-15 → 06-17 | PoC foundation rebuild: 12-module shell, SQLite image vault with hashing, refactored analysis engine, roles + simulated hardware boundaries, test project, async/cancellation |
| 06-17 → 06-20 | Requirements alignment against the two customer specs; spec re-checks became a recurring gate (re-verified ~07-07) |
| 06-21 | Validation/evidence discipline: batch validation (TP/TN/FP/FN, accuracy/precision/recall), SHA-256 evidence verification registry, end-to-end tests, pilot execution discipline |
| 06-23 → 06-24 | First demo packages; GUI clipping fixes; Stage-1 self-test rehearsal; UI Client Experience Audit (below) |
| 07-01 | Architecture/layout pass; PR "readiness claim" guardrails block over-claiming language |
| 07-02 → 07-03 | Image-only learning core (per-pixel reference + tolerance map, threshold calibration, persisted artifacts); milestone review with same-day fixes (PR #1); calibration defects L1–L3 fixed with regression tests (PR #2); GUI spec completion (PR #3); maintainability (PR #4); camera drop-in seam contract test |
| 07-04 | HMI design-token system with build-enforced layout gate; Clopper-Pearson 95% CIs, DPMO/PPM, min-sample gates; operator hotkeys; centroid-CSV auto-ROI; robustness study harness; EN/KO parity audit; P0 crash and learning fixes |
| 07-05 → 07-10 | Localization integrity (ComboBox display decoupled from persisted tokens; KO-token recipes self-heal); diagnostics tooling (desktop-automation MCP, Stryker, stage1-gate, wpf-ui-verifier); silent-failure sweep; CI restored green with push-gate and stop-build hooks |
| 07-11 | 3D Profile: real flood-fill region-volume computation replaces the placeholder |
| 07-13 | Statistical rigor + ML pipeline: held-out false-call estimation, ±2° rotation search, photometric normalization, box-average downsampling, James-Stein shrinkage, wider perturbation coverage; anomalib PatchCore→ONNX pipeline (`Scripts/ml/train_patchcore.py`, `Scripts/ml/evaluate_onnx.py`) |
| 07-13 | Full-project diagnosis and hardening: 12-screen audit; shell state root cause fixed; alarm hygiene (14-day auto-expiry, Acknowledge All, 90-day pruning); six screens' clipping fixed; heavy analysis off the UI thread; single-instance mutex; PBKDF2 120k→600k; final verify — Release build 0 errors, 508 unit + 12 UI tests 0 failures, all gates and smokes PASS, both CI green |

**Quality and delivery infrastructure.** 520 tests (508 unit, 12 STA UI incl. the HMI layout audit). CI: `.NET CI` and `Build Windows App` on every push to `main`. Local gates: `Scripts/run-quality-gates.ps1`, `Scripts/check-code-quality.ps1`, push-gate/stop-build hooks, the `/stage1-gate` release loop. Delivery: self-contained single-file `win-x64` publish (~154 MB, no .NET install); test build at `Desktop\AOI_Monitor_TestBuild\AOI_Monitor.exe`; app data in `%LOCALAPPDATA%\AOI_Monitor`.

**Evidence snapshot (2026-07-13; local 640-px benchmark set — dataset-specific, not universal).** Statistical engine and PatchCore ONNX both measured 0/15 held-out false calls and 0/20 escapes (NG detection); PatchCore separation 0.40, AUROC 0.9999 (statistical engine: threshold-sweep guardrail); both report rates as Clopper-Pearson 95% CI + PPM behind min-N gates.

**Known boundaries.** Image vault / learning tables grow without automatic pruning (retention plan needed before Stage-2 volumes); Stryker mutation testing blocked on WPF project structure; camera/lighting/robot/MES paths are simulated seams awaiting Stage-2+ hardware; xunit v3 migration available.

## External and Cold Review Record

### 2026-06-23 — UI Client Experience Audit

Screenshot-driven audit against the design contract (audit only). P0: Run Inspection calibration-profile combo showing a raw object string; System Settings light content area breaking dark-theme contrast; Export & Trace filter/date clipping and tab overload; Defect Review queue headers fragmenting. P1: oversized shell chrome, wrapping readiness chips, a large empty-alarms panel, cramped tables/panels on most pages, stronger always-visible evidence boundaries requested for Calibration, 3D Profile, and Hardware Readiness. Much of this has recorded fixes in later passes (06-24, 07-01, 07-13). Still open or scheduled: splitting Export & Trace's overloaded tab strip into subviews; broader dense-page decompositions. Full pre-consolidation text: git history (`Docs/UI_Client_Experience_Audit.md` at commit b2c4616).

### 2026-07-02 — Milestone Review: GUI Readiness and Stage 1 Image-Learning

Full static review of the GUI, learning pipeline, and traceability against the three client documents. Verdicts: GUI "close to industrial standard, not yet clean at the shipped defaults" — six geometry defects G1–G6, worst: the SIM/MOCK truthfulness banner clipping out of the shell at the default launch size; learning capability **met** (the workflow is real end-to-end, nothing mocked) but the accuracy claim **not yet trustworthy** — calibration defects L1–L3 meant the reported false-call rate did not hold at deployment; small samples overstated (L5). Recommendation: **conditional GO** — fix the P0 list, demo with fixtured same-camera imagery, present the capability as statistical visual learning with an ONNX hook, then run customer-dataset validation.

Closures: G1–G6, demo semantics, and synchronous report export (L6) same day (PR #1); L1–L3 with regression tests (PR #2); spec gaps closed 07-03 (PR #3: interactive 3D viewer, taxonomy completion, "AI model v1.0" designation, PDF Korean support, log archive-and-purge, role gates, `REVIEW_PENDING` reporting, recipe tolerance persistence); L5 on 07-04 (Clopper-Pearson + min-sample gates); remaining P0s (incl. L4 corrupt-image skip) in the 07-04 hardening commit.

Still open / still true: L7 (result contract mixes raw difference-percent with anomaly-score thresholds) and L8 (out-of-frame border band filled with the reference — an edge escape path) have no recorded closure; scheduled debt — MVVM on only two views, very large code-behind files, the ~8,600-line `AoiDatabase` static class, Export & Trace's 14 tabs, and the file-path `IInspectionEngine` / per-image ONNX session shape that needs an in-memory frame path before streaming acquisition; 12-column grid and broader `AutomationProperties` pass optional pending sign-off. Fixturing requirement stands (rotation later widened to ±2°): see capture requirements in `Docs/USER_MANUAL.md`. Full pre-consolidation text: git history (`Docs/Milestone_Review_GUI_and_Image_Learning.md` at commit b2c4616).

### AOI Industry Viability & Usability Assessment (cold review; the 2026-07-04 pass)

Benchmarks: IPC-A-610, IPC-CFX/IPC-2591, IPC-Hermes-9852, IPC-2581/DPMX, AIAG MSA/Gage R&R, DPMO/PPM + Clopper-Pearson, commercial 2D/3D AOI vendors. Verdict: **as a production AOI machine, not viable — and it correctly does not claim to be**; **as a Stage-1 image-only proof of concept and customer-evidence tool, viable and unusually disciplined** — honesty is a genuine competitive asset. Two core pieces are placeholders to replace outright: the pixel-difference engine and hand-drawn recipe programming.

Still-true gaps: no CAD/Gerber/BOM-driven programming (the largest usability gap — hand-drawn ROIs do not scale; centroid-CSV import is a partial step); alignment is a pixel search radius, not fiducial registration; no Gage R&R / MSA reproducibility study (the synthetic robustness study is labeled not a substitute); no 3D measurement — Solder Volume, Coplanarity, Pin Height cannot be measured from 2D (encoded per class in `DefectDetectionCapability`); mock MES JSON only, IPC-CFX/Hermes unbuilt (Stage 4); throughput undefined until Stage 2; verification-station features missing (CAD-linked board map, taxonomy quick-codes, live yield dashboard, repair-loop routing). Improvements in that pass: Clopper-Pearson 95% CI + PPM/DPMO reporting (`BinomialConfidence` / `RateEstimate`), the per-defect detection-capability reference, operator hot-keys, spacing pass.

**Post-Stage-1 priorities (highest leverage first):**

1. Trained anomaly model via the existing ONNX seam (anomalib PatchCore → `OnnxInspectionEngine`); a first training pipeline and dataset-specific benchmark landed 2026-07-13 — no trained production model is bundled or accepted.
2. CAD/BOM-driven programming (IPC-2581/Gerber + centroid auto-ROI) with fiducial-based registration.
3. MSA capability harness (repeatability + reproducibility) gating any accuracy claim.
4. IPC-CFX (IPC-2591) to MES and IPC-Hermes-9852 hand-off in place of mock JSON.
5. Stage 2 3D acquisition path for the 3D-only defect classes.
6. Lighting calibration + fiducial alignment to attack false calls at the root cause.

Full pre-consolidation text: git history (`Docs/AOI_Industry_Viability_Assessment.md` at commit b2c4616).

## Related Documents

- Customer specification set: `Docs/customer-specs/AOI_PoC_Development_Roadmap_and_Commercialization_Plan.md`, `Docs/customer-specs/AOI_PoC_GUI_Concept_and_Functional_Specification.md`, `Docs/customer-specs/PCBA_Defect_Classification_Table.md`.
- `Docs/Customer_Spec_Gap_Audit.md`, `Docs/Requirements_Traceability_Matrix.md` — per-requirement status and traceability.
- `Docs/VALIDATION.md`, `Docs/METRICS_VAL.md` — validation procedures and numeric acceptance criteria.
- `Docs/USER_MANUAL.md` — operator procedures and the 10-minute demo route.
- `Docs/ARCHITECTURE.md` — service boundaries and hardware/MES seams.
- `Docs/standard/00_Index.md` — the engineering standard governing all changes.

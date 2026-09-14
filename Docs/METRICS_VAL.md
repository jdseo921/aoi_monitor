OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AI Model and Inspection Acceptance Criteria

For engineers and reviewers deciding whether a validation run, model, or readiness stage is acceptable: metric definitions (the false-call / possible-escape trade-off), numeric acceptance gates per stage, and the completion-assessment methodology. Procedures that produce the evidence live in `Docs/VALIDATION.md`. This is business-readiness guidance, not a certification claim.

## Metrics and the false-call / escape trade-off

Batch validation reports accuracy, precision, recall, false-call rate, false-call count, possible-escape count, review count, TP/TN/FP/FN, category metrics, and timing. For business review, minimizing false positives means reducing good-board false calls without hiding real defects; false calls and possible escapes must stay separate metrics in UI, CSV, reports, readiness packages, and dashboards. Controls in force:

- Dataset preflight requires a labeled manifest, image evidence, golden references, OK/NG balance, defect-class coverage, side/view metadata, ROI/refdes completeness, and duplicate-hash checks.
- `FalseCallReductionService` sweeps operating thresholds and marks a recommendation `VALID` only when the configured false-call and possible-escape constraints are both met.
- Threshold changes are role-gated (Engineer/Admin) and audited; applied recommendations are labeled Stage 1 labeled-data evidence, not universal production accuracy proof.
- `ModelAcceptanceService` blocks production model acceptance unless an active ONNX model is selected, runtime-validated as `Ready`, tested against the validation dataset, and passes configured metrics, dataset-quality, false-call, possible-escape, review-rate, and inference-time gates.
- Customer validation and factory readiness packages include limitations and export verification so evidence can be reviewed outside the app.

## Acceptance criteria and gates

### Stage 1 dataset gates (defaults)

Minimum total images 50; minimum known ground-truth images 50; minimum OK images 20; minimum NG images 20; maximum unknown-label rate 5%; at least 2 NG defect classes with at least 5 images per class; all-OK and all-NG datasets fail preflight; a golden reference per sample (missing goldens block under Pixel Difference criteria). Procedure: `Docs/VALIDATION.md` §5.

### False-call / possible-escape gates

Typical configured gates: maximum allowed false call rate; maximum allowed possible escape rate; minimum known OK sample count; minimum known NG sample count; management review of limitations when data coverage is insufficient. Insufficient ground truth must be INVALID or CONDITIONAL - never presented as PASS evidence. The documented image-learning walkthrough default is `--false-call-target 0.05`.

Default metric gates for the Stage 1 validation package and for ONNX model acceptance (raised from 90% on 2026-09-14; pinned by `Stage1ReadinessGateServiceTests.AcceptanceCriteriaDefaultMetricGatesAre97Percent`): minimum accuracy 97%, minimum precision 97%, minimum recall 97%; maximum false-call rate 5% (model acceptance additionally caps possible-escape rate at 2% and review rate at 10%). Metrics count decided verdicts only — REVIEW rows are excluded from the confusion matrix and reported as review burden instead.

A Stage 1 exit package is acceptable only when all of these hold:

- customer/evaluator dataset preflight is `PASS` or explicitly accepted with documented warnings;
- false-call rate is within the configured acceptance criterion;
- possible-escape rate and possible-escape count are reviewed and not hidden by threshold tuning;
- precision, recall, and review burden meet the agreed customer/evaluator thresholds;
- threshold profiles are linked to the false-call reduction run that produced them;
- model acceptance is not claimed unless the active ONNX model has `PASS` evidence;
- generated exports verify successfully and contain the prototype/hardware limitations.

### Timing and stability gates

- Performance benchmark (required for a Stage 1 readiness PASS): p50/p95/p99/max frame-to-overlay, images-per-minute, and over-one-second count against the validation image folder. The frame-to-overlay budget is 1000 ms (sub-1-second operator feedback); the over-one-second count must be reviewed in the Stage 1 readiness report. Procedure: `Docs/VALIDATION.md` §4.2/§5.5.
- 8-hour stability (gap-audit ID ACC-11-03): the headless batch soak must complete the requested 8-hour duration, uncanceled, with no failure condition (stuck-iteration watchdog default 5 min; memory-trend failure default slope > 64 MB/h AND total growth > 256 MB). The in-app Factory PoC soak requires 480 minutes, non-canceled, no critical errors, p95/max/avg inspection time and memory start/end/max recorded. Procedures: `Docs/VALIDATION.md` §8-§9. The completion matrix credits at least 30 minutes of stability evidence as partial; full weight requires completed 8-hour factory evidence.

### Stage 1 acceptance evidence set

Build gate: `dotnet build AOI_PCB_Database.slnx --configuration Release` succeeds and the app launches on Windows with WPF desktop support; demo images are available locally (`SampleData/README.md`); the optional service-level smoke `pwsh Scripts/run-stage1-readiness-smoke.ps1` passes. The readiness panel must truthfully show: Database `Connected`; Image Vault `Available`; Inspection Engine `Pixel Difference Prototype Engine`; Camera `Folder Camera Simulation / Not Connected`; Robot `Simulated Robot / Not Connected`; MES / ERP `Mock MES / Not Connected`. Engine status labels: `Pixel Difference Prototype Engine` by default; `ML Model Missing` for absent ONNX files; `Model Not Tested` for unverified ONNX settings; `Ready` only after the current local ONNX configuration passes the readiness test; `Test Model Configuration` records a model-check event in the review/audit log.

Evidence to capture per acceptance run: readiness panel screenshot; imported image list; Golden Compare result with overlay; filtered Log & Export rows; exported inspection CSV and review CSV; annotated overlay PNG; customer validation package folder; Stage 1 readiness report folder (HTML/PDF/JSON); benchmark report folder with p95 and over-one-second evidence.

## Completion assessment methodology

The Completion Matrix is an internal gap report showing how much objective evidence exists per factory-readiness stage and preventing simulated or prototype evidence from being presented as production completion. Scores are calculated from persisted evidence records in the local SQLite database and service settings - not hardcoded readiness claims. If evidence has not been recorded by the relevant test, package, export, or configuration service, the criterion remains missing. Each area scores 0-100%:

| Area | Criteria and Weights |
| --- | --- |
| Stage 1 image validation | Customer validation package recorded 40, persisted validation batch run 25, false-call reduction evidence 20, export verification 15 |
| Production model readiness | Active model registered 15, runtime validation completed 15, PASS model acceptance 35, ProductionCandidate/Deployed lifecycle 20, release package path recorded 15 |
| False-positive reduction readiness | Validation run has measurable false-call rate 20, false-call sweep completed 30, recommended operating point exists 25, deployed threshold profile linked to false-call evidence 25 |
| Stage 2 camera/lighting/3D | Real camera acceptance PASS 35, real lighting sync PASS 25, real 3D profile PASS 25, simulated boundary exercise 15 |
| Stage 3 robot/safety | Real robot cell acceptance PASS 35, real safety/PLC interlock evidence 30, invalid transition/reset checks 20, robot audit events 15 |
| Stage 4 MES/ERP | Passing traceability acceptance 35, MES REST ready 25, MES queue clear 20, abandoned-item disposition visible 20 |
| Central sync/management | Central sync configured 20, central sync queue has no failed items 20, management dashboard exported 30, central sync or management report exported 30 |
| Reliability/soak | Soak run recorded 25, at least 30 minutes stability evidence 20, 8-hour factory evidence 30, no failed cycles/critical errors 15, latency traces 10 |
| Deployment/supportability | Passing build/test/publish evidence imported 30, configuration backup exported 25, factory readiness package exported 25, factory acceptance checklist/package exported 20 |
| Commercial readiness | LocalUsers accountability mode 25, management dashboard evidence exported 20, FullFactoryAutomation has no blocking issues 25, release/support build evidence 15, customer/commercial package exported 15 |

The overall percentage is the average of the area percentages - a gap indicator, not a Go/No-Go decision; Go/No-Go remains controlled by the Factory Readiness profiles and acceptance gates.

**Why simulated evidence caps a stage.** Simulation evidence supports software smoke tests and integration rehearsals but cannot satisfy real-hardware criteria: a simulated camera, lighting controller, 3D source, robot controller, safety controller, MES endpoint, or central sync target may add "software path exercised" evidence while the real-hardware weighted criteria stay missing. The matrix deliberately separates simulation boundary evidence from real factory evidence, so a Stage 2 or Stage 3 score cannot reach production-level completion without acceptance records showing real adapters, real devices, and real safety behavior.

**How evidence changes scores.** Customer dataset execution and Stage 1 package export raise only the Stage 1 score. A registered model without PASS acceptance and lifecycle promotion stays incomplete. Hardware scores rise only on real-hardware passing acceptance records; simulated records remain visible but do not satisfy real-hardware weights. MES/ERP depends on traceability evidence, REST readiness, queue health, and abandoned-item disposition; central sync and management reporting are scored separately, so a local MES queue does not imply enterprise aggregation readiness (the dashboard uses local SQLite first and needs no central server). Short or simulated soak runs count as partial reliability evidence; the 8-hour criterion requires the service to mark the run completed factory evidence. Deployment/supportability answers whether another customer/factory PC can be installed, restored, and reviewed. Commercial readiness requires LocalUsers accountability, management review exports, customer/commercial packages, and FullFactoryAutomation with no blocking issues; Demo role selection produces a readiness warning and does not satisfy accountability.

## Business-readiness framing (guidance, not certification)

Camera boundary: the Stage 2 camera-pilot architecture (camera adapter interfaces, plugin loading, camera/lighting/3D acceptance, factory readiness profiles) defines how evidence will be collected, but it is not real hardware acceptance until the selected customer/vendor adapter produces accepted real frames with stable camera IDs, view assignments, frame IDs, timestamps, dimensions, pixel format, source kind, lighting timing, and real-camera performance evidence. Folder Camera Simulation, null adapters, fake adapters, sample CSV profiles, and generated test images are workflow evidence only and must not be used to claim real hardware readiness.

Forward quality expectations for future changes:

- run the Stage 1 exit CLI or WPF evidence workflow for customer validation reruns;
- run false-call reduction after dataset, model, recipe, threshold, camera, or lighting changes;
- keep false calls and possible escapes as separate metrics everywhere;
- keep camera, lighting, robot, 3D, MES, and central sync evidence separated by real/simulated status;
- run repository hygiene, Release build/test, quality gates, HMI layout audit, and navigation performance checks before readiness claims;
- do not commit customer images, generated evidence packages, local databases, vendor SDK binaries, or runtime exports.

Current open evidence: real customer dataset evidence is still required for a true Stage 1 exit claim; a production model acceptance claim still requires active ONNX `PASS` evidence from `ModelAcceptanceService`; Stage 2 camera readiness still requires a real vendor adapter and accepted real camera, lighting, and 3D acquisition evidence; simulation evidence remains valuable for development and smoke testing but is not factory hardware readiness.

## Related documents

- `Docs/VALIDATION.md` - the procedures that produce this evidence.
- `Docs/ROADMAP.md` - stage feature status (implemented vs planned).
- `Docs/Customer_Spec_Gap_Audit.md` - requirement IDs (e.g. ACC-11-03) and deviation register.
- `Docs/Requirements_Traceability_Matrix.md` - requirement-to-evidence traceability.

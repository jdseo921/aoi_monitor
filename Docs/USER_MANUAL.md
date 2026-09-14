OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor User Manual

For operators, engineers, and administrators running the AOI Monitor proof of concept during a client review or factory demo: the behavior implemented today, with prototype boundaries called out. Installation, database maintenance, crash reports, and IT troubleshooting: `Docs/RUNBOOK.md` and `Docs/DEPLOYMENT.md`.

## Prototype Boundaries

Implemented today: local Windows WPF operator console with local user/role selection; local SQLite database and managed image vault; operator-first Run Inspection screen with Folder Camera Simulation; Pixel Difference Prototype Engine (default), the AI Training Setup image-only learning workflow, and an ONNX Runtime path for configured local models with safe `REVIEW` fallback on missing model, invalid model, or runtime failure; 2D calibration profiles for approximate image-to-board mapping (Stage 2 planning); image import, golden comparison, disposition logging, recipe revisions, batch validation, customer package export, and 3D sample-data CSV review.

Not implemented as production integration: real AOI camera acquisition; real 3D camera hardware; PLC, robot, handler, conveyor, or line-stop control; MES/ERP authentication and traceability; production database service; a bundled trained production ML model (ONNX inference is available only when a valid local model is configured and successfully runs).

Simulated, mock, demo, and non-production evidence is always labeled: purple chips/styles mean simulated/mock/demo, and the shell shows a SIM / MOCK banner while simulated sources are active. Green is reserved for validated OK/pass/ready, red for NG/fail/alarm, amber for warning/review/pending, gray/blue for disabled/not connected (see `DESIGN.md`).

## Workflow Windows

Home opens the module map for the 13 destination windows and the station status chips: Board & Images, Run Inspection, Golden Compare, Defect Review, Recipe Rules, AI / Models, Yield Analytics, Export & Trace, Readiness & QA, Calibration, 3D Profile, Hardware Readiness, and System Settings. Older documents and exports may use earlier module names (Main Inspection, Image Library, Disposition, Recipe Editor, AI Model Test, Log & Export, 3D Profile Viewer, Settings / Guide).

## Roles and Sign-In

Local roles only; not MES login. The shell user panel provides `User` (user ID), `Role` (Operator / Engineer / Admin), `Auth` mode, a `PW` box with `Login`, and Admin user management (`Add User`, `Set PW`, `Disable`, `Delete`). Auth modes: `DemoLocalRoleSelector` (pick a role for demos), `LocalUsers` (local accounts with password login; passwords stored as salted PBKDF2 hashes, sessions and login attempts recorded locally), and `MesAuthenticationBoundary` (labeled Stage 4 boundary; MES authentication not implemented).

| Role | Allowed |
| --- | --- |
| Operator | Run inspection, view results, save inspection result, view guide. |
| Engineer | Operator permissions plus edit recipes, run AI / Models batch validation, change inspection thresholds. |
| Admin | Engineer permissions plus export logs, manage settings, change database/vault/model paths, and access maintenance actions. |

Restricted actions show permission-denied messages and are recorded in the event log. Operator/Engineer review Export & Trace read-only; exporting and deleting logs remain Admin-only.

## Readiness Panel

Expected Stage 1 values:

- Database: `Connected`
- Image Vault: `Available`
- Inspection Engine: `Pixel Difference Prototype Engine` — for ONNX configurations, `ML Model Missing` (absent file), `Model Not Tested` (unverified settings), or `Ready` after the readiness test passes
- Camera: `Folder Camera Simulation / Not Connected`
- Robot: `Simulated Robot / Not Connected`
- MES / ERP: `Mock MES / Not Connected`

## Run Inspection

1. Open `Run Inspection` (the primary operator screen).
2. Confirm station, board model, lot ID, operator, engine, and model version.
3. Select the view: `Top`, `Side`, or `Bottom`.
4. Click `Start` (F5) to begin simulated inspection mode.
5. Click `Next Board` (F7) to load the next queued or simulated image frame.
6. Review the image area, overlay boxes and labels, and the defect list table: No, Type, Score, Side, X, and Y.
7. Check the result indicator: green `OK`, red `NG`, yellow `REVIEW` or warning.
8. Click `Save Result` (Ctrl+S) to persist the result and defects to SQLite; optionally enable auto-save.
9. Click `Stop` (F6) to pause simulated inspection.

Hotkeys are suppressed while typing. The event log tracks starts, stops, advances, completions, saves, and errors; per-cycle timing warns over the 1 second target. Frames come from Folder Camera Simulation when configured, otherwise imported images or the current workflow sample — never real camera hardware. Selecting a saved `2D Cal Profile` shows approximate board X/Y millimeters beside defect centers (Stage 2 preparation values, not robot-ready coordinates).

The `Simulated Robot / Handler` panel is a software-only Stage 1 demonstration: manual `Load`, `Inspect`, `Unload`, `Reset`, and `E-Stop Sim`, or a full `Run Cycle` (load simulated board, inspect, save, unload) with cycle timing; every simulated robot event goes to the event/review log. It does not control a real robot, handler, PLC, conveyor, or safety circuit.

## Board & Images

1. Open `Board & Images` and click `Open Record`.
2. Select a PNG/JPG/JPEG image; it is validated and copied into the managed image vault.
3. The SQLite record stores original path, vault path, board model, lot ID, view type, timestamp, and file hash; duplicates are detected by SHA-256 hash and not copied again.

Bulk import: click `Batch Import` and select a folder; supported images import with progress and cancellation; the summary reports imported, duplicate, unsupported, missing, or invalid files; issues are logged as review events. From a selected record: preview images, add to the training-set export folder, export the record to CSV, or click `Compare Golden` to pick a golden reference — analysis runs and the app opens Golden Compare.

## Golden Compare

The Stage 1 prototype inspection path.

1. Import or select a sample image.
2. Click `Compare Golden` and select a golden/reference image.
3. The selected engine runs (deterministic image-difference with the Pixel Difference Prototype Engine).
4. Review score, confidence, verdict, suggested defect, decision reason, evidence, and the hotspot/defect overlay.

The page synchronizes zoom/pan, toggles AI/golden overlay opacity, and exports a PNG snapshot. Without a golden image the result stays `REVIEW` (not enough reference data). Boundary: the default engine supports workflow validation and evidence generation — it is not a trained production ML model; a selected ONNX configuration reports `REVIEW` with clear evidence if loading or inference fails.

## Defect Review

Human review of results.

1. Open `Defect Review` and review the current analysis details.
2. Use the disposition actions: `Confirm NG` (1), `Mark False Call` (2), `Mark Possible Escape` (3), `Hold for 2nd Review` (4), and `Queue Candidate`.
3. Guardrails warn when blocked: confirming NG is blocked below 70% confidence; marking a false call is blocked for high-confidence NG at or above 85%.
4. Actions are recorded in SQLite review events with user ID and role, visible in Export & Trace.

Also provided: overlay toggle, canvas zoom, navigation to Golden Compare, ROI crop export, workflow history popup, training-set queueing. The queue without local data is demo-labeled.

## Recipe Rules

Engineer and Admin roles only.

1. Open `Recipe Rules` and load a background image.
2. Draw rectangular ROIs; select and adjust position and size.
3. Choose ROI type: Presence, Polarity, Placement, Solder Bridge, Solder Volume, Height, Surface Defect, or Anomaly.
4. Set the AI score threshold and optional height/volume limits.
5. Set the recipe-wide Processing & Tolerance Rules: X/Y placement tolerance (mm), rotation tolerance (deg), IPC acceptability class, lighting profile, and false-call policy — persisted with the revision and restored on reload.
6. Click `Test Run` to analyze the loaded image against the current in-editor edits, including unsaved ROI and tolerance changes; the status line notes the run used unsaved edits.
7. Click `Save Recipe` — the revision is written to SQLite with board program, operator, role, detection priority, background image path, tolerance/IPC/lighting/false-call rules, and JSON ROI data.
8. Use recipe lock/unlock to prevent accidental edits during evaluation.

Optional: import a pick-and-place centroid CSV (KiCad/Altium headers; comma/semicolon/tab; mil-to-mm) to auto-generate one Presence ROI per component; positions are uncalibrated approximations that must be reviewed before saving. Active ROI is yellow, saved green, unsaved inactive blue. The editor is a local Stage 1 proof of concept, not synchronized with a production recipe server or MES.

## Calibration

Engineer and Admin roles; labeled `2D Calibration Profile / Stage 2 Preparation`.

1. Open `Calibration` and load a sample calibration image.
2. Enter point pairs: image X/Y and board X/Y in millimeters (clicking the preview fills image X/Y).
3. Add at least two points to calculate an approximate 2D scale/offset transform.
4. Save the profile to SQLite; reload to confirm points and transform summary.
5. In `Run Inspection`, select the saved `2D Cal Profile` to show approximate board-mm coordinates.

No claim of live camera calibration, robot coordinate validation, or production machine alignment. Concepts and Stage 2 plans: `Docs/CALIBRATION.md`.

## AI / Models

Batch runs and model configuration are Engineer/Admin.

### Stage 1 batch validation

1. Open `AI / Models` and select a validation image folder.
2. Optionally select a ground-truth CSV manifest.
3. Click `Run Dataset Preflight` and resolve blocking failures.
4. Click `Run Batch Inspection`; watch progress, cancel if needed.
5. Review metrics: Accuracy, Precision, Recall, False call rate; TP/TN/FP/FN; OK, NG, REVIEW, false call, possible escape, unknown/unlabeled counts; average/max/min inspection time and the count over the 1 second target. REVIEW verdicts are reported separately as pending human review, outside the confusion matrix.
6. Export CSV results and annotated images if needed, then click `Export Stage 1 Validation Package`.

The package contains `validation_summary.html`/PDF, the full customer validation report (project/station info, operator role, model configuration, metrics, performance summary, confusion matrix, failed samples, prototype limitations, signature/approval section), CSV results, benchmark CSV and manifest copy when available, a limitations file, and sample annotated images. False-call and escape rates are reported as exact Clopper-Pearson 95% confidence intervals + PPM behind minimum-sample gates, not bare percentages.

The richer manifest format supports:

```text
image,ground_truth,golden_image,defect_type,side,refdes,lot_id,board_model,notes
```

Simpler CSVs with image and ground-truth/label columns remain supported. Bad files, missing/unsupported images, invalid rows, and database write failures are logged and skipped where possible.

### AI Training Setup (image-only PCB learning)

Learns a board's normal appearance from images: a per-pixel reference plus tolerance map with a threshold calibrated against validation images. Describe it truthfully as statistical visual learning — "the software learns the normal appearance of your board and calibrates its own tolerance" — not deep learning; the ONNX model registry is the upgrade path for trained detectors. Image groups: Golden / Reference, OK Learning, OK Validation, Inspection, optional NG Validation — imported via convention folders, picker, or drag-drop onto the role cards.

Typical run (client demo script):

1. Prepare 20–40 OK images plus all available NG images of one board model on a fixed rig (same camera, distance, lighting). Split OK images half Learning / half Validation; keep 5–10 mixed images as Inspection samples.
2. Create a project named after the board and import the image groups.
3. Press `Learn Normal PCB Appearance` — the learned reference and tolerance-map images appear. Learning writes `learned_reference.png`, `tolerance_map.png`, `anomaly_threshold_map.png`, `learning_summary.json`, `alignment_summary.csv`, and `threshold_sweep.csv`.
4. Review the False-Call Behavior panel: false calls quoted against the OK Validation image count with confidence intervals. `Calibrate False Calls` re-runs learning and creates a new model version.
5. Press `Inspect Samples` and open the anomaly overlays — defective boards show flagged regions, good boards do not.
6. Press `Export Client Learning Report` (`visual_learning_report.html`, PDF); manage learned models with versioning/activation.

One-command equivalents: `learn-from-images` (customer folders), `client-image-learning-demo` (labeled synthetic demo output).

Capture requirements and limits: fixtured, same-camera, same-framing imagery only (tripod rig or scanner) — handheld phone photos will mass-false-call. Rotation handling is a small-angle (±2°) search; scale/perspective changes, directional lighting/shadows, and color-only defects (grayscale pipeline) are unhandled; defects smaller than the minimum anomaly area at model resolution may be missed. Training without OK Validation images keeps the default threshold and marks the result REVIEW instead of deploying an uncalibrated threshold.

## Export & Trace

Audit review and operational evidence generation. Operator/Engineer open it read-only (Inspection History, Review/Disposition Events, Export History, Audit Trail, MES Upload Queue, Central Evidence Sync); export, delete, and Mock MES upload are Admin-only.

Common actions: filter by date, board/model, operator, or result; review the six grids; export `Inspection History CSV`, `Review Log CSV`, audit trail CSV, and `Annotated Overlays` (each shows a confirmation dialog and appears in Export History); run DB Integrity and rebuild the image index (maintenance detail: `Docs/RUNBOOK.md`); upload the selected/latest result to Mock MES; retry or abandon MES/central-sync queue items. The Audit Trail tab filters by date, user, role, and action type; the audit CSV includes UTC/local timestamps, user ID, role, station ID, action category/detail, and related record/image/path fields.

Mock MES upload is not production MES/ERP integration: it builds a MES-style traceability payload (lot ID, board model, station, operator, result, timestamp, defect summary, image path); `Mock REST` mode POSTs to the configured mock endpoint, otherwise it writes local JSON; each attempt is recorded in SQLite.

### Data retention

Live log rows are kept 30 days by default. At startup, older rows are copied into a recoverable local archive with their full payload, then purged from live tables; with the pre-purge warning enabled, Export & Trace shows an advisory a configurable number of days ahead (default 7). Configure in `System Settings > Data Retention` (Admin only): enable/disable purge, retention window in days, warning lead time. Disabling purge keeps all live rows; the archive is retained indefinitely so purged history can be reconstructed for audits.

## Readiness & QA

Stage gates, checklists, dashboards, and acceptance evidence, split out of Export & Trace so each window keeps a single tab row. Tabs: Pilot Issues (with a filtered inspection-row list for capturing false-call / possible-escape issues), UI Stability Test, Management Dashboard, Factory Readiness, Stage 1 Readiness, Standards & Quality Checklist, Evidence Completion, and Factory Acceptance. The footer keeps the readiness status block (auto-archive policy, latest customer package, latest soak report, build/test evidence, client demo readiness) and the Readiness and Quality Gates actions; exports, Soak Test, and issue actions are Admin-only, and the UI Stability Test and Soak Test require maintenance permission.

The Stage 1 customer package is a timestamped folder: HTML customer validation report with print-to-PDF instructions, Markdown companion report, batch CSV, sample annotated validation images, annotated inspection overlays, inspection/review/audit CSVs, engine/model configuration summary, database health summary, recipe revision summary, calibration profile summary, README, and warnings; missing optional evidence produces a warning, not a failure.

The Soak Test repeatedly inspects images from a selected folder through Folder Camera Simulation for the requested duration, supports cancellation, and exports an HTML report (cycle counts, success/failure counts, timing, memory estimates, start/end time, errors); use a short duration such as 2 minutes before an 8-hour evidence soak. The `Performance Benchmark` and `Stage 1 Readiness` tabs used in the demo route also live here.

## 3D Profile

Sample Data Mode only — an interactive 3D height surface from a sample CSV; no real 3D camera connection.

1. Open `3D Profile` and confirm it clearly shows `Sample Data Mode`, `3D Camera Not Connected`, and the Stage 2 hardware requirement.
2. Leave the source set to `Sample CSV`, click `Load Height CSV`, and select a CSV with columns:

```text
x,y,height
```

3. Review the surface: left-drag to rotate (yaw/pitch), mouse wheel to zoom, right-drag to pan, `Reset View` to reset.
4. Use the 2D top-down height-map inset; click it to select a point — surface, inset, and height slice stay in sync.
5. Review the height legend (min/max), selected-point height, and the slice/profile line; notable peaks are marked automatically.
6. Review the feature/defect list (Type, Height, Volume, X, Y); row and surface/inset selection are synchronized. Volume is a real flood-fill estimate of the connected out-of-tolerance region, summed as height deviation from the mean plane and reported in **grid units cubed** — not calibrated mm³. Absolute volume requires calibrated Z data from Stage 2 3D hardware.
7. Click `Accept Defect` or `Reject Defect`; the disposition is recorded as a SQLite review event.

Optional: `Run 3D Acceptance Test` records a sample-data acceptance evidence run; `Export 3D Report` writes the acceptance summary. Both are labeled sample-data evidence, not live 3D sensor acceptance.

## Yield Analytics and Hardware Readiness

- **Yield Analytics** — SPC/Pareto/trend view plus local database health: SQLite table counts, health indicators, inspection/review/image summary counts; the SPC trend chart remains prototype data, labeled as such.
- **Hardware Readiness** — pilot readiness wizard for camera, lighting, robot, and 3D gate evidence per deployment profile (e.g. `Stage2CameraPilot`); all current evidence is simulation/boundary-level and labeled; open Stage 2 blockers: `Docs/ROADMAP.md`.

## System Settings

Engine, model, label-map, camera-source, Mock MES, threshold, and path controls per role, plus the operator guide and prototype installation notes (documentation only). Use it to:

- Select the Pixel Difference Prototype Engine or a configured ONNX ML Model; configure model file path, model version, label map path, confidence threshold, input size, and ONNX tensor names.
- Run `Test Model Configuration` to verify model file availability, label-map validity, tensor names, ONNX Runtime session creation, and detection output compatibility. Last check result/timestamp is shown; `Ready` appears only after the current configuration passes (others: `Missing Model`, `Invalid Label Map`, `Runtime Error`, `Unsupported Output Format`); each check records an audit event.
- Configure Folder Camera Simulation and review camera status.
- Set MES mode (Not Connected, Mock REST, or Future Production planned), mock endpoint URL, and upload timeout (blank endpoint = local JSON payload evidence only).
- Set language (English/Korean) and font-size presets; switching language never corrupts saved data (display text is decoupled from persisted values).
- Select detection priority: `Minimize False Positives`, `Balanced`, or `Maximize Defect Recall` (recipe lock enforced).
- Change the local storage root (Admin) for the SQLite database, image vault, local settings, and exports; existing folders are left in place.
- Configure Data Retention (defaults: 30 days retention, 7 days warning lead).
- Prepare Training Set Export (prepare export, validate list, stop preparation, open training folder; status, queued-sample count, list-check count, and list-quality score shown). Local candidate-file preparation only — no model training, fine-tuning, or deployment pipeline runs.

MES authentication and production ERP/MES writeback are planned for Stage 4 and are not implemented in the local role selector or mock upload tool.

## Run the Stage 1 Demo in 10 Minutes

Setup: build with `dotnet build AOI_PCB_Database.slnx --configuration Release`, launch, and confirm the Readiness Panel values above. Demo images: `SampleData/README.md`. Optional smoke: `pwsh Scripts/run-stage1-readiness-smoke.ps1`.

Management/customer walkthrough with synthetic, non-confidential data:

1. Generate sample data: `pwsh SampleData/demo_dataset_generator.ps1`.
2. Launch AOI Monitor.
3. Open `AI / Models`.
4. Select `SampleData/DemoSet_Quick/images`.
5. Select `SampleData/DemoSet_Quick/customer_validation_manifest.csv`.
6. Click `Run Dataset Preflight`.
7. Click `Run Batch Inspection`.
8. Review rows, OK/NG/REVIEW counts, false calls, possible escapes, timing, and selected-row preview.
9. Export CSV and annotated images if needed.
10. Click `Export Stage 1 Validation Package`.
11. Open `Readiness & QA > Performance Benchmark` and benchmark `SampleData/DemoSet_Quick/images`.
12. Open `Readiness & QA > Stage 1 Readiness`, click `Refresh`, then `Export Report`.
13. Review `stage1_readiness_report.html`, `stage1_readiness_report.pdf`, `stage1_readiness_report.json`, `validation_summary.html`, `customer_validation_report.html`, `benchmark_report.html`, `benchmark_results.csv`, and `limitations.txt`.
14. Confirm every report keeps the claim scoped to Stage 1 uploaded-image validation and does not claim real camera, lighting, robot, MES, safety, or full factory automation readiness.

Acceptance evidence to capture: readiness panel screenshot; imported image list; Golden Compare result with overlay; Export & Trace filtered rows; exported inspection CSV and review CSV; annotated overlay PNG; customer validation package folder contents; Stage 1 readiness report folder (HTML/PDF/JSON); benchmark report folder with p95 and over-one-second evidence.

For a learning-centric client demo, follow the AI Training Setup steps and close with the staged roadmap (`Docs/ROADMAP.md`).

## Appendix — Feature Reference by Window

Demo framing defaults: board program `TBOX-MAIN`, station `AOI-LIB-01`, model version `AOI_AI_0.8.1`. The shell keeps a shared workflow summary visible across pages (detection policy, sample/golden images, latest score/verdict) plus global refresh, recipe lock/unlock, export, and open-export-folder actions; shared workflow state coordinates the pages.

- **Home** — module map plus station status chips.
- **Board & Images** — image-record browser (`Demo Data`-labeled fallback and schema reference rows); import, preview, compare, training-set add, relabel logging, record CSV export.
- **Run Inspection** — view selector, overlays, defect table, run controls, auto-save, event log, timing warnings, verdict indicator, simulated robot panel, board-mm display.
- **Golden Compare** — synchronized zoom/pan, overlay opacity toggles, decision evidence, hotspot ROI, PNG snapshot; demo-labeled board illustration.
- **Defect Review** — demo-labeled queue, overlay/zoom toggles, guarded dispositions, ROI crop export, history popup, training-set queueing.
- **Recipe Rules** — ROI editing, types/thresholds, tolerance rules, Test Run on unsaved edits, revisions with lock, centroid CSV import.
- **AI / Models** — preflight, batch validation with confidence reporting, validation package, AI Training Setup with model versioning/activation.
- **Yield Analytics** — prototype-labeled SPC trends, database health summaries.
- **Export & Trace** — history/audit grids, confirmed exports, DB integrity, image index rebuild, Mock MES upload, MES/central sync queues.
- **Readiness & QA** — pilot issues, UI stability test, management dashboard, factory/Stage 1 readiness, standards checklist, evidence completion, factory acceptance, customer package, soak test, benchmark.
- **Calibration** — 2D point-pair profiles, approximate transform, customer-package summary.
- **3D Profile** — sample-CSV height surface, synchronized inset/slice/feature list, dispositions, acceptance test/report.
- **Hardware Readiness** — camera/lighting/robot/3D gate wizard; simulation/boundary evidence only.
- **System Settings** — display presets, detection priority, engine/ONNX configuration, camera simulation, MES mode, storage root, retention, training-set export, guide.

Detection policy thresholds:

| Detection Priority | Review Threshold | NG Threshold | Intent |
| --- | ---: | ---: | --- |
| Minimize False Positives | 12% | 24% | Conservative review mode |
| Balanced | 8% | 18% | Middle-ground review mode |
| Maximize Defect Recall | 5% | 14% | More sensitive defect-recall mode |

Machine-interface evidence export (file export only — it controls no robot, PLC, conveyor, or interlock): accepted results are written under `<application folder>/exports/machine_interface/` as `latest_decision.json`, timestamped `decision_yyyyMMdd_HHmmss.json` snapshots, and append-only `decision_history.ndjson`; disposition events append to `disposition_events.ndjson`. The decision contract carries full inspection context and evidence, the verdict code (OK 0, REVIEW 1, NG 2), and machine-action hints (hold for review, stop-line recommendation, human confirmation required).

Local data:

- SQLite database: `%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite`
- Managed image vault: `%LOCALAPPDATA%\AOI_Monitor\image_vault\` (training candidates under `image_vault\training\`)
- Exports root: `<application folder>/exports/` — including `review_disposition_log.csv`, `image_index.csv`, `packages/`, `training_set/`, and `machine_interface/`

Engineering detail (engine internals, preprocessing, schema, service map): `Docs/ARCHITECTURE.md`, `Docs/DATA_PIPELINE.md`.

## Related Documents

- `Docs/RUNBOOK.md` — installation, maintenance, troubleshooting; `Docs/DEPLOYMENT.md` — build and delivery.
- `Docs/ROADMAP.md` — stages, status, evidence gates; `Docs/METRICS_VAL.md` — numeric acceptance criteria.
- `Docs/CALIBRATION.md` — calibration concepts; `Docs/ARCHITECTURE.md`, `Docs/DATA_PIPELINE.md` — engineering internals.
- `SampleData/README.md` — demo dataset generation; `DESIGN.md` — HMI color semantics and layout contract.

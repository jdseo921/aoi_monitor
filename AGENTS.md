OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AGENTS.md

This file applies to the entire repository.

AOI Monitor is a production-oriented WPF PCBA AOI review console, not a cosmetic demo. It currently contains Stage 1 prototype evidence and clearly labeled simulation/mock boundaries, but the architecture and user interface must be maintained as an industrial HMI/software-quality project. Future Codex tasks must preserve the existing design contract, service boundaries, evidence gates, and truthful separation between prototype evidence and real camera/lighting/robot/MES readiness.

This project is standards-aligned, not formally ISO, IEC, ISA, safety, cybersecurity, or regulatory certified. Do not create wording, UI states, exports, reports, or release notes that imply certification or production readiness without the required real evidence and formal process.

The canonical engineering constitution is the **AOI Software Architecture, Secure Development, and Change-Control Standard** in `Docs/standard/` (start at `Docs/standard/00_Index.md`). It must be consulted before every change of any kind; its Change Execution Contract (VOL01 §3), Definition of Done (VOL17 §51), auto-reject list (VOL17 §49), and AI-assisted-development controls (VOL17 §48) bind all work in this repository — including work by AI agents. Where this file and the standard conflict, the standard wins and the conflict is a defect to fix. The requirement catalogue is machine-validated by `Scripts/standard_catalogue.py` (fitness function FF-STD-01, wired into CI).

## Required Orientation Before Editing

Before changing production code or UI, read the relevant current contracts and surfaces:

- `Docs/standard/00_Index.md` (and the volume covering your subsystem)
- `README.md`
- `DESIGN.md` (the design contract merged with the HMI style guide, quality baseline, and scroll rules)
- `Docs/ARCHITECTURE.md`
- `AOI_Monitor/App.xaml`
- `AOI_Monitor/Styles/FactoryHmiLayout.xaml`
- `AOI_Monitor/MainWindow.xaml`
- the affected major pages under `AOI_Monitor/Views/`

Documentation is deliberately consolidated into a small fixed set (see the documentation map in `README.md`): root `README.md`, `CONTRIBUTING.md`, `AGENTS.md`, `CLAUDE.md`, `DESIGN.md`, the themed documents under `Docs/` (`ARCHITECTURE.md`, `DATA_PIPELINE.md`, `API_SPEC.md`, `DEPLOYMENT.md`, `RUNBOOK.md`, `CALIBRATION.md`, `METRICS_VAL.md`, `SECURITY.md`, `VALIDATION.md`, `ROADMAP.md`, `USER_MANUAL.md`, plus the three kept traceability documents), the canonical standard under `Docs/standard/`, and the customer source specifications under `Docs/customer-specs/`. Do not create new standalone markdown documents; extend the correct consolidated document instead. Every markdown file in this repository must keep its first-line reviewer notice exactly as written.

The current focused workflow windows are Home, Board & Images, Run Inspection, Golden Compare, Defect Review, Recipe Rules, AI / Models, Yield Analytics, Export & Trace, Readiness & QA, Calibration, 3D Profile, Hardware Readiness, and System Settings. Keep the shell, Home module map, route handling, role authorization, HMI layout audit, navigation smoke tests, and documentation aligned when any workflow changes.

## Architecture Contract

Preserve the existing layered design:

- UI: WPF shell, Views, shared HMI styles, controls, operator-safe presentation.
- Domain models: typed workflow, inspection, export, alarm, readiness, settings, and machine-interface records.
- AOI pipeline and inference adapters: image analysis, registration, inspection engines, ONNX/model boundaries, pixel-difference prototype boundaries.
- Storage: SQLite, image vault, settings persistence, schema migration, export history, audit records.
- Hardware and integration boundaries: camera, lighting, 3D profile, robot/PLC/safety, MES/traceability, central sync.
- Services/config: workflow state, authorization, readiness gates, quality gates, crash reports, support bundles, exports, diagnostics, validation, and settings.

Do not move business rules into XAML/code-behind when a service or model boundary already owns them. Code-behind may coordinate UI events and call services; it must not become the AOI pipeline, database policy, model adapter, or machine-interface layer.

## Non-Negotiable Reminders

1. Do not build a monolith. Keep UI, domain models, AOI pipeline, inference/model adapters, storage, hardware boundaries, services, and config separated.
2. UI code must not contain AOI algorithm logic, image-processing logic, storage rules, machine-interface rules, or model-inference rules.
3. Do not block the UI thread with image loading, database scans, model loading, inference, exports, report generation, filesystem scans, network calls, hardware checks, sleeps, or long loops.
4. Page constructors must be lightweight. Heavy work belongs in async refresh commands, background services, cancellable jobs, or explicit operator commands.
5. Inspection workflow state must be explicit and recoverable. Avoid scattered booleans for inspection state.
6. Use typed/intentional errors and operator-safe messages. Never swallow broad exceptions silently.
7. Do not show raw stack traces to operators.
8. Keep image pixels, corrected image coordinates, board/world coordinates, overlay coordinates, and screen coordinates separate.
9. Treat calibration, fiducials, registration, board orientation, lighting normalization, and recipe selection as first-class concepts.
10. Version anything that affects inspection results: recipes, thresholds, models, camera profiles, calibration profiles, defect taxonomy, schema, report format, and software release.
11. Inspection results must be traceable to the exact image, recipe, model, threshold set, calibration profile, software version, and operator/session where applicable.
12. Do not hard-code thresholds, model paths, image paths, dimensions, or readiness claims.
13. Detection, classification, operator review, false-call handling, possible-escape handling, and reporting must remain separate stages.
14. Support false positives, false negatives, missed-defect annotation, operator disposition, review history, and candidate export intentionally.
15. Use stable storage and schema migrations for persisted inspection data. Do not store critical results only in UI state.
16. Use explicit configuration validation and fail early with clear messages when config is invalid.
17. Design for large images and large result sets. Use lazy loading, tiling, caching, bounded queues, virtualization, and deterministic cleanup where needed.
18. Add observability: structured logs, run IDs, timing, model/config IDs, crash reports, diagnostic bundles, and operator-visible error IDs.
19. Add or update tests for meaningful behavior changes.
20. UI work is not done until text clipping, resizing, long strings, empty data, error states, small windows, and high-DPI assumptions are checked.
21. Do not use fixed-size or absolute layouts for normal UI. Use WPF Grid, DockPanel, WrapPanel, star sizing, scrolling, trimming-with-tooltip, and adaptive structure.
22. Primary actions must stay readable, consistently placed, and at least 120x40. Operator text must remain at least a true 14 pt (18.67 DIP); the HMI layout audit fails below it.
23. Dense pages must scroll or decompose into tabs/subviews. Do not squeeze more controls into already crowded pages.
24. Simulated, mock, demo, sample-data, boundary-only, not-connected, and not-validated states must be visibly labeled and must never be presented as production readiness.
25. Codex must not claim completion unless it lists changed files, architecture impact, tests added/updated, checks run, known limitations, UI layout cases checked, performance implications, and migration implications.

## HMI And Design Rules

Use shared resources in `AOI_Monitor/Styles/FactoryHmiLayout.xaml` and `AOI_Monitor/App.xaml` before introducing page-local styling. Repeated local styles should be promoted into shared resources.

Keep these design principles intact:

- Minimum operator display target is 1920x1080.
- Operator-facing text must remain at least a true 14 pt (18.67 DIP).
- Primary action buttons must be at least 120x40.
- High contrast is mandatory in dark and light themes.
- Green means validated OK/pass/ready/connected/running-normal only.
- Red means NG/fail/alarm/stop/critical error.
- Amber/yellow means warning/review/pending/conditional/not tested.
- Gray/blue means disabled/not connected/unavailable/not configured.
- Purple means simulated/mock/demo/non-production evidence.
- Color must never be the only signal.
- Long operator names, file paths, model IDs, recipe names, defect labels, endpoints, and translated strings must wrap, scroll, or trim with tooltip instead of clipping.
- Critical verdicts, alarms, mode/profile labels, and primary actions must not be hidden below scroll-only areas.
- Planned or boundary-only features must look planned or boundary-only, never implemented production capability.

Do not add decorative hero sections, marketing screens, one-off color systems, tiny controls, hidden scroll requirements, duplicated status panels, or cramped all-in-one pages. If a page is crowded, split it into tabs/subviews or move the workflow to the correct focused window.

## Responsiveness And Reliability

Navigation must be fast, cancellable where appropriate, and free of stuck loading overlays. Constructors should initialize UI only. Use `IAsyncNavigationPage`, refresh methods, background services, cancellation tokens, progress indicators, and operator commands for work that can take time.

Expected failures must be recoverable. Unhandled failures must create crash reports and operator-safe messages. Critical alarms, failed exports, open release blockers, and simulated-hardware limitations must be visible in the UI and evidence reports.

## Evidence And Truthfulness

AOI Monitor evidence must remain auditable:

- Results must include enough context to reproduce the decision.
- Exported CSV/PNG/PDF/JSON/HTML/TXT evidence must be verified when it is used for readiness or client packages.
- Stage 1 image-validation evidence must not satisfy real camera, lighting, robot, safety, MES, or full factory automation gates.
- Mock REST, sample folders, null adapters, local demo role selection, and simulated robot/handler flows must stay visibly labeled.
- Never write "production ready", "factory accepted", "certified safe", "MES connected", or similar claims near simulated, mock, or unvalidated paths.

## Definition Of Done

For meaningful code, UI, workflow, service, release, or evidence changes, the final response must report the checks below. Run them unless the task is documentation-only or an explicit constraint prevents them. If any check cannot run, state the reason and the residual risk.

- `dotnet build AOI_PCB_Database.slnx --configuration Release`
- `dotnet test AOI_PCB_Database.slnx --configuration Release`
- `pwsh Scripts/run-quality-gates.ps1 -Configuration Release`
- HMI layout audit artifacts reviewed: `hmi_layout_audit.json` and `hmi_layout_audit.html`
- navigation performance artifact reviewed: `ui_navigation_performance.json`
- screenshots or manual evidence for 1920x1080 at 100%, 125%, and 150% DPI assumptions
- explicit notes for any checks that could not be run

The final response must also include:

- changed files
- architecture impact
- tests added or updated
- checks run
- known limitations
- UI layout cases checked
- performance implications
- migration implications

Do not claim completion if these are missing for a task that affects behavior, UI, readiness, evidence, or release packaging.

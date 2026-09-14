OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor Design Contract and Factory HMI Style Guide

Single design authority for AOI Monitor: design contract, factory HMI style rules, standards-aligned quality baseline, and open design rework list. Read it before changing WPF UI, navigation, styles, or evidence screens. Runtime standards-traceability evidence cites this file as the style guide; keep the color/status semantics section explicit.

AOI Monitor is an industrial desktop HMI for PCBA automated optical inspection: calm, legible, accountable, production-oriented — not a marketing site or decorative demo. The default screen is the usable factory workflow: work-focused; dense but organized; honest (simulated/mock/demo/pending/not-validated always labeled); calm under failure (errors become alarms, recovery guidance, and audit records — never crashes or raw stack traces); stable across time (a new feature must not shrink everything else until text clips).

The project is standards-aligned, not formally certified: rules align with ISO 9241-style HMI principles, ISO/IEC 25010-style quality categories, and IEC 62682 / ISA-18.2-style alarm discipline; nothing here claims formal ISO, IEC, ISA, safety, cybersecurity, or regulatory certification. Audience split: operator pages answer "what is the board status and what do I do next?"; engineer pages "how do I tune, validate, and explain the inspection?"; admin pages "what evidence and readiness do we have?". Diagnostics, tuning, and evidence review live in Engineer/Admin areas.

## Design Authority And Scope

Priority order in conflicts: 1) operator safety, clarity, recoverability; 2) truthful evidence and clear separation of simulated versus real hardware readiness; 3) layout stability at 1920x1080 and common Windows DPI scales; 4) fast, cancellable interaction without UI-thread blocking; 5) consistency with shared HMI styles and workflow language; 6) visual polish. If a screen violates these rules, fix the structure — rewrite, split, or move diagnostics behind tabs — rather than patch locally to protect a weak layout.

Scope: the shell (`AOI_Monitor/MainWindow.xaml`) and navigation; every page in `AOI_Monitor/Views/`; shared controls, styles, dialogs, popups; dashboards, reports, export/evidence screens; calibration, hardware-boundary, MES, robot, camera, lighting, and model-validation flows; readiness-representing docs/screenshots. Machine enforcement: `Tools/quality-gates/industrial_quality_gates.json`, `Scripts/check-pr-quality.ps1`, HMI layout audit.

## Layout Constraints And WPF Rules

- Minimum display 1920x1080; critical actions visible at 125% Windows DPI; important actions reachable without resizing. No clipped, overlapped, or vanishing text at 100/125/150/200% DPI; resizing must not clip alarms, buttons, headers, grid columns, or export controls. Pages tolerate missing images, disconnected hardware, empty datasets, long strings, failed exports, unavailable services.
- Pages fill the operator display: at 1920x1080 the exposed page background below the lowest panel or control (trailing dead space) must stay at or under 20% of the viewport — let a table, chart, tile grid, or status band absorb spare height instead. Machine-checked by the HMI layout audit (`TrailingDeadSpace`: Warn > 20%, Fail > 35%; per-view `TrailingVoidPercent` is reported in `hmi_layout_audit.json`/`.html`). The five worst pages measured 19-59% before the 2026-08-23 layout pass and now measure <= 0.9%.
- Operator text at least a true 14 pt (18.67 DIP) - raised from the earlier "14 DIP equivalent" reading on 2026-08-23 and machine-enforced: the HMI layout audit fails critical text below 18.67 DIP (`SmallCriticalText`, Fail severity) and the shared `HmiFontSize*` tokens carry the floor (Body/Label/Small 18.67, Section 20, PageTitle 22). Base-font presets were retired with the raise; per-station sizing is Windows DPI scaling, validated at 100/125/150%. Primary buttons at least 120x40 px; icon-only commands get accessible names or tooltips; targets spaced against accidental activation.
- Important text wraps or resizes, never clips or relies on hover. Long names, paths, model/station IDs, recipes, defect labels, endpoints/MES URLs, and translated-like strings wrap, scroll, or trim with tooltip; `TextBlock` wraps unless intentionally trimmed.
- Dense pages use `ScrollViewer`, virtualized tables, tabs, or approved adaptive layout. Critical verdicts, alarms, machine/station states, images, mode/profile labels, and primary actions never hide below scroll-only areas.
- Tables keep readable headers and timestamp/status/ID/action columns and never push action buttons off-screen; large data uses `DataGrid` internal scrolling — no unbounded `DataGrid` inside a page-level `ScrollViewer`.
- Root content: adaptive `Grid` (`Auto`/`*`), `DockPanel` for in-page bands, `WrapPanel` for toolbars that wrap at 125% DPI; no fixed page heights or small fixed label widths; absolute positioning / `Canvas` only for image overlays, defect boxes, coordinate visualization.
- Dense page bodies scroll via `FactoryScrollablePage` (a `ScrollViewer` around the page `Grid`; never around `MainWindow`); the top banner/nav strip and bottom evidence footer stay fixed; wheel scrolling moves only the page body. Secondary content may scroll while critical items stay reachable; pages that fit cleanly stay unchanged; add page scrolling only on clear overflow risk.

### Per-Page Scroll Decisions

Re-check changed pages at 1920x1080 / 125% DPI with long strings and full data; full audit incl. risk ratings and manual checks: git history (`Docs/HMI_Page_Scroll_Audit.md` at commit b2c4616).

- Body wrapped in `FactoryScrollablePage`: Main Inspection / `MonitorView` (high risk: Start/Stop/Next Board/Save Result band above the height-bounded image viewport, defect/alarm tables height-bounded, band stays visible unscrolled); Defect Review / `ReviewView` (disposition band/footer reachable, queue table height-bounded); Golden Compare / `CompareView` (adaptive min/max center column); Yield Analytics / `SpcView` (database warning banner stays reachable).
- Already present: Home / `HomeView`, AI / Models / `AIModelTestView`, System Settings / `SettingsView` (tabs), Guide / `GuideView`, Install / `InstallView`.
- Not added (bounded internal scrolling suffices, no observed clipping): Export & Trace / `ReportsView`; Readiness & QA / `ReadinessQaView`; Recipe Rules / `RecipeView`; Board & Images / `LibraryView`; Calibration / `CalibrationView`; Hardware Readiness / `PilotWizardView`; 3D Profile / `ProfileView` (`Viewport3D` surface + 2D inset + fixed-height cards; star/auto rows keep header/footer band reachable; feature list and slice scroll internally).

## Shell And Navigation

Home is a menu, not a dashboard: it shows the engine truth chip, ABNORMAL states as colored chips (healthy and Stage-1-expected not-connected states collapse into one summary line that names them — nothing is silently omitted), the destination tiles in three labeled clusters (Operate / Analyze & Evidence / Machine & System, `NavPage.Group`), and the evidence-boundary banner. Live inspection state belongs to Run Inspection; hardware detail belongs to Hardware Readiness. Developer/maintenance harnesses (for example the layout stress test) live in System Settings diagnostics, never in the operator Access flyout.

- Persistent top banner: compact instrument strip — operating mode, deployment profile, engine/model status, user/role, purple simulation/mock warning whenever demo/simulated/mock sources are active, and active critical/alarm counts. The full alarm list (filters, acknowledgement, details, export) opens from a flyout without growing the banner.
- Home module map: large tiles at 1920x1080 (header, readiness chips, four-column module grid, Current State / Evidence Boundary band); normal navigation and noncritical readiness live on Home only. Workflow pages fill the workspace between banner and bottom evidence footer (record count, image link health, database revision/update, station); only critical/alarm status and Home return stay persistent outside pages; critical alarms never require page navigation.
- Run Inspection and Golden Compare stay embedded in the shell; their image panels may open a separate large-image viewer window (zoom, fit, 100% view, PNG save, critical/alarm status, prototype/simulation boundary labels). Pop-ups only for image viewing or short dialogs.
- Startup window opens below 1920x1080 so operators can find minimize/maximize/close; 1920x1080 remains the operator-display and audit target.

The fourteen workflow menus: `01 Home`, `02 Board & Images`, `03 Run Inspection`, `04 Golden Compare`, `05 Defect Review`, `06 Recipe Rules`, `07 AI / Models`, `08 Yield Analytics`, `09 Export & Trace`, `10 Readiness & QA`, `11 Calibration`, `12 3D Profile`, `13 Hardware Readiness`, `14 System Settings`. Support pages (Installation Notes, Guide) live under System Settings, never as top-level workflows.

A new focused window requires updating the Home map, shell routes, role authorization, HMI audit, navigation smoke tests, and this contract — and must reduce cognitive load and clipping risk. Never duplicate a status or control between shell and page unless it serves different operator decisions; if two areas answer the same question, consolidate.

## Page Composition

Layouts are task-suitable: every screen presents the next operator action clearly. Standard structure (documented exceptions only): 1) page title and workflow state; 2) primary status/verdict area with sample/board/recipe/model context; 3) primary action band; 4) main work area; 5) secondary evidence/logs/diagnostics/settings; 6) export/audit actions. First viewport answers: system state; next operator action; real vs simulated/mock/demo/pending/not-validated; active alarms or release blockers; recoverability. Never open a workflow page with explanatory prose (help lives in guide, tooltips, docs). Empty, loading, error, simulated, not-connected, and not-validated states are designed explicitly.

## Shared Styles, Spacing, And Alignment

Use shared resources in `AOI_Monitor/Styles/FactoryHmiLayout.xaml` and `AOI_Monitor/App.xaml` before page-local styling:

- Layout/content: `FactoryPageContainer` (page roots), `FactoryScrollablePage` (dense pages), `FactoryCard` / `HmiKpiCard` (status/metrics), `HmiTable` (operator `DataGrid`), `HmiDenseTable` (dense tables), `FactoryTrimmedTextWithTooltip` (secondary long strings).
- Buttons: `HmiOperatorActionButton`, `ActionBtnGreen`, `ActionBtnBlue`, `ActionBtnAmber`, `ActionBtnRed`. Primary actions lower-right or in a marked top action band; destructive actions red and separated.
- Spacing scale (no ad-hoc pixel margins): `HmiSpaceXS`=4, `HmiSpaceS`=8, `HmiSpaceM`=12, `HmiSpaceL`=16, `HmiSpaceXL`=24. Gap tokens: `HmiButtonGap` (buttons), `HmiRowGap` (stacked rows), `HmiFieldGap` (wrapped filter fields), `HmiChipGap` (status-chip rhythm), `HmiSectionGap` (between page sections), `HmiPageMargin` (page root). Button rows use `HmiRightActionBand` (bottom/top-right) or `HmiInlineActionBand` (toolbars), each button with `Margin="{StaticResource HmiButtonGap}"`; right-aligned rows never touch the container edge.
- Viewport-fill pages use `FactoryViewportFillPage`: the page inset lives on the ScrollViewer `Padding` and the root Grid carries no outer Margin — a Margin outside a `ViewportHeight`-bound `MinHeight` adds itself to the scroll extent and produces a permanent sliver of scroll. Panel titles use `HmiPanelHeader` + `HmiSectionHeaderText` (one header strip app-wide); large verdict surfaces use the `HmiVerdictBanner`/`Ok`/`Ng`/`Warn` family with `HmiFontSizeVerdict`, swapped by style (never raw hex brushes in code-behind); empty tables/canvases announce themselves with `HmiEmptyStateText`.
- Prefer `Auto`, `*`, or `MinWidth` over fixed pixel `Width` (fixed widths clip under DPI scaling and localization); the PR gate warns (`PR-HMI-WIDTH-001`) on new fixed `Width` >= 80 outside `Styles/`.

Page-local styles only for structure that cannot live in the shared layer; promote repeats. No second visual language for one feature: no one-off colors, custom cards/buttons, decorative panels, or unrelated spacing scales.

## Color And Status Semantics

Industrial Dark is the single supported theme (the light theme was removed 2026-08-23: it predated the token system, its worst measured text pairing was 1.05:1, and no station used it). High contrast is mandatory for text, controls, tables, charts, alarms, and overlays, and it is machine-checked: every themed foreground/surface pairing carries a WCAG contrast contract in `AOI_Monitor/Services/HmiThemePalette.cs`, enforced by `HmiThemePaletteTests` (the palette and the XAML token definitions are also drift-locked against each other). Theme colors live only in the `Hmi*` tokens of `AOI_Monitor/Styles/FactoryHmiLayout.xaml` and the App.xaml brushes; per-view color literals are reserved for self-contained status chips and constant-dark image wells. Status vocabulary:

Status colors versus interactive controls (decided 2026-08-23, GUI audit finding #6): the semantic table below binds **status indicators** - chips, verdicts, alarms, badges, readiness rows. Action buttons use tinted variants as emphasis and follow the machine-panel run-control convention operators already know: green Start, red Stop. This is a deliberate exception, not an oversight - repainting run controls against factory muscle memory would trade a stylistic purity for an operational hazard. Two rules keep the exception safe: color is never the only signal on any control (text label + position + hotkey), and amber/purple button tints are reserved for actions that genuinely warrant caution (Save Result) or touch simulated boundaries. New tinted buttons must justify their tint against this clause.

| Meaning | Preferred words | Color |
| --- | --- | --- |
| Pass/ready/connected/normal | `OK`, `GO`, `READY`, `CONNECTED`, `PASS` | Green |
| Fail/stop/critical error | `NG`, `NO-GO`, `ERROR`, `FAIL`, `STOPPED`, `CRITICAL` | Red |
| Review/conditional | `REVIEW`, `CONDITIONAL`, `WARNING`, `PENDING`, `NOT TESTED` | Amber/yellow |
| Unavailable/not configured | `NOT CONNECTED`, `NOT VALIDATED`, `DISABLED`, `UNAVAILABLE` | Gray/blue-gray |
| Non-production evidence | `SIMULATED`, `MOCK`, `DEMO`, `CSV SAMPLE`, `FOLDER SIMULATION` | Purple, clearly labeled |
| Candidate production | `PRODUCTION CANDIDATE`, `DEPLOYED` | Only with acceptance evidence |

Purple is reserved for simulated/mock/demo/non-production evidence. Never green for Demo mode, mock services, simulated hardware, placeholders, or unvalidated acceptance evidence. Color is never the only signal — pair with text, severity, icon, label, pattern, or value. Disabled, unavailable, simulated, and real-hardware states stay visually distinct so simulated evidence cannot pass for real factory validation.

Forbidden overclaims — never write: "Production ready" for simulated/mock/demo/unvalidated flows; "Certified safe" for robot/PLC/E-stop simulations; "MES connected"/"MES integrated" for Mock REST, placeholder, or not-connected modes; "Factory accepted" on partial or generated-only evidence; "Validated model"/"validated AI" without an approved registry and acceptance state; "real camera" for folder simulation; "deployed model" without lifecycle evidence.

Preferred wording: "Evidence collected for review."; "Simulated source active."; "Factory readiness: REVIEW."; "Acceptance package exported; approval still required."; "Not connected / not validated."; "Stage 1 evidence only."; "Requires real hardware acceptance."; "Model runtime test passed; production acceptance still required."; "Export verified; approval still required." (plus variants like `Folder Camera Simulation active`, `Mock MES payload generated`, `3D CSV Sample Mode`).

Plain language (operator-readable labels first; no unexplained acronyms): `MES` = factory traceability/result-upload system; `ONNX` = supported ML model format; `ROI` = board image area checked for a defect; `Threshold` = score limit deciding OK/REVIEW/NG; `False call` = OK board flagged review/NG; `Possible escape` = known NG board that may be missed; `Acceptance` = evidence review against criteria, not automatic production release.

## Typography, Controls, And Accessibility

- Plain production terms before technical terms; short control/table labels; long explanations in details, tooltips, dialogs, or expandable panels. Engineering diagnostics stay separated from operator recovery guidance.
- Disabled controls explain why via nearby text or tooltip; long commands show progress and support cancellation where safe; view changes never discard unsaved operator decisions without confirmation.
- Keyboard focus order follows the visible workflow; important controls have accessible names; modal dialogs state the action required; tooltips are never the only path to a critical state. Repeated actions keep consistent placement, names, colors, and input behavior.

## Performance And Responsiveness

- Page constructors are lightweight: no heavy file I/O, database scans, network calls, model loading, image decoding, hardware checks, sleeps, or long loops. Heavy work goes to async refresh, background services, or explicit commands with progress and cancellation tokens. Never run model loading, inference, exports, report/package generation, hardware checks, benchmarks, syncs, or soak tests on the UI thread.
- Navigation gives fast feedback and never leaves stuck loading overlays; repeated menu switching is smoke-tested.
- Inspection visualization targets 1 second or less from input to operator-visible result where feasible; performance evidence includes timing for image load, preprocessing, inference, overlay rendering, persistence; slow paths produce warnings, not silent degradation.
- Freeze `BitmapSource` after decoding to reduce UI-thread pressure; thumbnails/bounded image caches for lists; design for large images and result sets (lazy loading, tiling, caching, bounded queues, virtualization, deterministic cleanup).
- Anything that cannot meet these rules shows explicit progress, timeout behavior, cancel/retry options, operator-readable failure messages.

## Reliability, Alarms, And Error Design

- Alarms/warnings are readable, prioritized, timestamped, recoverable, never hidden or clipped; severity explicit (alarm, warning, review, info); UTC timestamps where persisted, local time where useful. Recovery guidance for camera, robot, lighting, MES, export, model, and database failures. Active alarms never buried behind modals, collapsed panels, or scroll-only areas; critical alarms stay visible until acknowledged or resolved. Acknowledge/reset/retry/waiver/signoff actions are auditable when they affect production or client evidence.
- Expected failures recover without app restart; unhandled failures create crash reports and operator-safe messages (what happened, current state, next action). Logs and support bundles redact secrets. Alarms, audit events, crash reports, readiness blockers are first-class UI states.
- A failed export, hardware check, model run, or readiness gate is never silently treated as success; fail-safe prefers review, stop, retry, or degraded mode over misleading OK/NG output.
- No release/client-demo packages with known uninvestigated crashes; an 8-hour stable PoC run precedes claims implying extended operation; pilot/production profiles carry stage-appropriate stability evidence; crash reports, support bundles, and audit records are release evidence.

## Security And Accountability

Capture operator identity, role, station ID, timestamp, and action category for production-relevant actions. Secrets, API keys, passwords, tokens, and endpoint credentials never appear in UI, config examples, exports, logs, screenshots, support bundles, or client packages. Role-based authorization (Operator, Engineer, Admin) protects administrative setup, model approval, recipe changes, production-mode confirmations, and waivers. Audit exports are verifiable without exposing secrets; simulation versus production mode is explicit in evidence.

## Evidence, Exports, And Simulation Truthfulness

- Evidence exports (CSV, PNG, PDF, JSON, HTML, TXT, packages) are verified: existence, non-empty content, format signature/parseability where practical, checksum, required fields. Evidence/release rows include generated timestamp, operator or automation identity, station/machine identity, status, source/profile, artifact path/checksum — enough to reproduce the decision.
- Export failures are operator-visible, auditable, and block release when required for the selected profile; client packages include manifest evidence (file list vs actual contents). Client-demo readiness is blocked when required evidence is missing or failing; runtime Standards & Quality status stays visible in the Readiness & QA window (Factory Readiness / Standards & Quality Checklist tabs); release blockers never hide in logs only.
- Simulated, mock, demo, null, sample-data, CSV-sample, folder-simulation, boundary-only, not-connected, and not-validated modes are labeled in UI and exported evidence; mock MES stays labeled mock/local interface evidence only. Camera, lighting, 3D profile, robot, safety, and MES evidence identifies whether it is simulated, mock, boundary-only, pilot, or real hardware.
- Staged evidence counts only for its stage: Stage 1 = image validation and export; Stage 2 = camera and lighting pilot; Stage 3 = robot and safety pilot; Stage 4 = MES or central sync pilot; Full Factory Automation = real hardware, MES/central sync, release package, stability, accountability. Stage 1 prototype evidence never satisfies gates requiring live camera, lighting, robot, safety, MES, or full factory integration.
- MES/central sync evidence includes payload mapping, queue/retry behavior, endpoint mode, redaction rules, signoff status. Reports distinguish Stage 1 from Stage 2/3/4/full-factory evidence; if partial, say partial; if simulated, say simulated; if not connected, say not connected.
- Release rule: a release or client-demo package is not represented as ready for production unless the selected profile has passing or accepted evidence for HMI layout, performance, reliability, security/accountability, export verification, hardware readiness, MES/central sync where applicable, and release packaging.

## Backend Alignment

- UI may reorganize controls but never duplicates inspection, export, authentication, readiness, MES, camera, lighting, robot, or database rules in XAML/code-behind when a service owns them; UI state derives from services/models.
- Services, persisted records, audit events, export verification, quality gates, and readiness outcomes are the source of truth; UI states (Ready, Blocked, Simulated, Mock, Not Connected, Not Validated, Failed) derive from the same backend state used by reports and gates; visual grouping never hides release blockers, critical alarms, failed exports, crash reports, open critical issues, or simulated-hardware limitations.
- Renaming/moving a control updates HMI audit definitions, navigation smoke tests, and backend-facing tests that locate it by name. A design change is incomplete if backend evidence, exports, logs, or gates stay inconsistent; unimplemented backend capability shows a boundary state, never implied production readiness.

## Information Architecture

One obvious home per feature — elsewhere only links or summaries, never parallel workflow copies.

`Home`: module map, entry point, noncritical readiness. `Board & Images`: imported images, folder sources, inventory, golden references. `Run Inspection`: execution, live progress, board verdicts. `Golden Compare`: golden comparison, difference scoring. `Defect Review`: defect queue, evidence, classification, disposition. `Recipe Rules`: recipe, ROI, mask, rule, threshold, tolerance setup. `AI / Models`: model validation, Stage 1 evidence, ONNX/test-set review, false-call feedback. `Yield Analytics`: SPC, Pareto, yield/trend analytics. `Export & Trace`: history, review/disposition and audit logs, verified exports, MES queues, database/image index, central evidence sync. `Readiness & QA`: pilot issues, UI stability, management dashboard, factory/Stage 1 readiness, standards checklist, evidence completion, factory acceptance, client packages and quality gates. `Calibration`: setup, Stage 2 preparation. `3D Profile`: 3D CSV/profile visualization, sample-mode evidence. `Hardware Readiness`: customer pilot evidence; camera/lighting/robot/MES readiness walkthroughs. `System Settings`: display, language, theme, storage, security, integration, support notes, guide; tabs: Basics, QOL, AI, Hardware, Traceability, Evidence.

## Patterns Adopted From Commercial AOI Systems

Distilled from commercial AOI/SPI/3D/smart-factory/AI-AOI consoles and internal PoC requirements; inspiration only — no parity claims; no copied vendor UI, branding, screenshots, logos, or layouts; no real-3D-camera, warpage-accuracy, remote-factory-control, or smart-factory claims while the app runs on local SQLite/files, mock REST, or sample data; no validated-AI claims without acceptance evidence. Full vendor analysis: git history (`Docs/AOI_Competitive_HMI_Reference_Guide.md` at commit b2c4616).

Page-type rules:

- **Run Inspection (Main Inspection)**: context banner (station, user/role, board model, lot, engine/model, mode, simulation warning, alarms); large stable `Start`/`Stop`/`Next Board`/`Save Result`; large central image with defect overlay (bounding/ROI boxes, labels, score/severity, selected-defect highlight) synced with a sortable defect list (Type, Score, Side, X/Y, ROI, RefDes, severity, coordinates validated or labeled approximate); Top/Side/Bottom view switching where supported; event/alarm log; large `OK`/`NG`/`REVIEW` indicator. No AI internals needed; plain status text (`No camera connected`, `Folder simulation active`, `Review required`, `Save result failed`). Excludes recipe editing, model training, export packaging, readiness signoff.
- **Recipe Rules**: Engineer/Admin only; operator denial auditable and operator-safe. Large image area (zoom/pan/fit, ROI focus); ROI list/parameters unclipped. ROI colors: active=yellow, saved=green, unsaved/editing=blue, invalid=red, always with text. Threshold fields (AI score, height, volume, tolerance) state units and meaning. Save records user, role, timestamp, revision, reason/notes — keeping revision history and exportable change evidence; unsaved changes visible before navigation; test runs are not production validation; generated thresholds stay candidate evidence until approved.
- **AI / Models**: Engineer/Admin validation. Dataset preflight first (missing labels, weak ground truth, missing golden references, bad files, class imbalance); metrics (accuracy, precision, recall, false-call rate, possible-escape count, review burden, TP/TN/FP/FN, category counts); failed samples red; weak/unknown samples `CONDITIONAL`/`REVIEW`, never `OK`; previews (sample, golden, overlay, selected defect, result); timing vs the 1-second target. Runtime readiness separate from dataset acceptance; no accuracy overclaims on weak datasets; threshold recommendations produce drafts or controlled deployments only; customer image paths out of reports unless allowed. First view: dataset path/manifest, engine/model source, preflight status, ground-truth coverage, validation status (`Ready`/`Review`/`Conditional`/`Blocked`), next action.
- **3D Profile**: separate from 2D inspection, linked by selected sample/defect. Height map + legend, slice/profile graph, synchronized defect/region/marker, units on height/volume. Persistent source banner (CSV Sample / Simulated / Pilot Hardware / Real Hardware Evidence); `3D Camera Not Connected` until a real adapter and Stage 2 acceptance evidence exist; CSV visuals never labeled live 3D. Acceptance/export actions stay separate from viewing controls.
- **Management surfaces (Export & Trace / Readiness & QA / Yield Analytics / Hardware Readiness)**: logs filterable by date, model, operator, role, result, event type, action category; export history shows timestamp, operator, status, artifact path, verification status/checksum, failure reason; MES/central sync shows pending/failed/sent/abandoned/retry counts. Dashboard: OK/NG/REVIEW counts, yield trend, false-call rate, possible-escape rate, review burden, top defect classes, top locations/RefDes, latency/over-1-second counts, readiness, export verification.

## Forbidden Design Patterns

Redesign on sight: clipped button text, tab/table headers, alarms, verdicts, or menu labels; missing scrollbars on dense pages; fonts below 14 pt to fit content; duplicated workflows/status; critical alarms or release blockers hidden below scroll-only content; constructors that load files, sleep, scan directories, call hardware, or block the UI thread; raw stack traces in operator messages; decorative gradients, hero sections, marketing-style cards, one-off color systems, tiny controls, cramped all-in-one pages; green status for simulated or unvalidated success; "production ready" wording on simulated, mock, or not-validated hardware paths; hard-coded secrets in UI, config examples, logs, screenshots, or exports; planned/disabled/boundary-only features styled as implemented production capability.

## Design Change Process

Before changing a screen: identify the operator task and affected deployment profile; pick the owning workflow window; sketch against the standard composition; decide existing tab vs structural split; use shared styles first; plan empty/loading/error/simulated/not-validated/success states; update layout/performance tests when structure changes; update docs and evidence wording when the workflow or readiness claim changes.

## Verification And Gates

UI/layout changes must pass the HMI layout audit (last command) before handoff; design-affecting PRs run all four or provide CI evidence:

```powershell
dotnet build AOI_PCB_Database.slnx --configuration Release
dotnet test AOI_PCB_Database.slnx --configuration Release
pwsh Scripts/run-quality-gates.ps1 -Configuration Release
dotnet test AOI_Monitor.UiTests\AOI_Monitor.UiTests.csproj --configuration Release --filter FullyQualifiedName~HmiLayoutAuditTests
```

plus artifacts `hmi_layout_audit.json` / `hmi_layout_audit.html` and `ui_navigation_performance.json`, export verification evidence when reports/packages change, and client-demo gate evidence when packaging/readiness changes. If a check cannot run locally, state why and point to CI evidence; for small changes a targeted build or documented manual check is proportional. Never claim a gate passed unless it ran.

Gate rules:

- Unapproved text/button/input/table-header clipping is blocking, including warning-severity findings. Secondary IDs/paths may trim only with a tooltip; critical verdicts, alarm counts, role/mode labels, and primary actions wrap, resize, or move instead. Review audit artifacts after dense page changes; screenshot or manual review for new workflows, major shell changes, or GUI-reported issues.
- Target regression bar: constructor-crash, crash-report, secret-redaction, and simulation-overclaim checks; UI smoke at 1920x1080 with 125% DPI review; `ScrollViewer` check on dense pages; long-string stress (model IDs, paths, endpoint URLs, recipes, station IDs, operator names, defect labels); empty-data, error/retry, and Engineer/Admin role checks; Stage 1/2/3/4 evidence wording checks.
- PR quality checks fail new XAML with sub-14 pt font sizes or tiny minimum heights, warn on wide fixed widths (`PR-HMI-WIDTH-001`), and fail if this contract loses its required clauses (`Scripts/check-pr-quality.ps1`, `PR-DESIGN-001`).
- Quality gates stay machine-readable and testable; CI parses the gate config and tests severity decisions; gate changes update this document and `Tools/quality-gates/industrial_quality_gates.json` together. Docs distinguish implemented, simulated, planned, and real-hardware capability.

UI work is done only when the rules above hold, the relevant gates pass, and tests or manual verification notes are recorded (`AGENTS.md` has the full Definition of Done). Design quality is part of the application's control surface, not decoration.

## Design Review Status And Open Rework

The last full frontend review (full text: git history, `Docs/Frontend_Design_Review_and_Rework_Plan.md` at commit b2c4616) found a credible industrial HMI foundation; remaining risk: enforcement and follow-through. Its applied decisions are binding and folded into the sections above.

Open rework (detail in git history):

- Tokens/components: replace page-local `FontSize` with shared text styles; classify every command (primary, secondary, destructive, evidence/export, diagnostic) and use only matching shared styles; build shared status badge, evidence gate card, readiness row, export action band, page header, and error panel; extend the HMI audit to shell, dialogs, secondary pages.
- Status truth: one source-of-truth status summary component for mode/profile/source/readiness; state XAML (empty/loading/error/simulated/not-validated) on every page; one exact state at every adapter boundary and evidence panel — Real Hardware Evidence, Pilot Evidence, Boundary Only, Simulated, Mock, Demo, Not Connected, or Not Validated.
- Adaptive layout: Main Inspection, Recipe Editor, Calibration, Profile Viewer, Pilot Wizard need adaptive sections for long strings and 150/200% DPI; add visual regression snapshots at 1920x1080 / 100/125/150% DPI as CI artifacts with diffable HTML summaries.
- Shell: move local user management from the interim Utilities / Access panel to a System Settings Users & Auth section once the shared auth control is reusable; keep only user/role/session summary and Access/Login in the shell.
- Per page: Run Inspection — robot simulation controls to a secondary tab, current-board state strip (sample, recipe, engine, verdict, next action). Recipe Rules — adaptive two-row header, scrollable ROI inspector, image canvas dominant. AI / Models — Run Test/Results/Acceptance Evidence/Export tabs, persistent model/source/test-set state. Export & Trace — wrapping filter chips over fixed grids, audited subviews, consistent export band. System Settings — readiness-impact summary, new tabs only when they reduce density. Hardware Readiness — gate-driven stepper (profile, evidence, checks, export, signoff). Calibration — "Stage 2 preparation only" band, image-to-board planning separated from real-hardware calibration. Golden Compare / Defect Review / Board & Images — mock PCB visuals replaced by evidence from actual image/ROI data, shared risk/verdict chips; setup wizard output feeds the readiness dashboard.

## Related Documents

- `AGENTS.md` — working agreement, architecture contract, Definition of Done.
- `Docs/standard/00_Index.md` — canonical engineering standard (e.g. Docs/standard VOL01 §3).
- `Docs/ARCHITECTURE.md` (layers, service boundaries); `Docs/VALIDATION.md` (evidence gates, verification).
- `Docs/Standards_Traceability_Matrix.md` (standards rows citing this file); `Docs/Requirements_Traceability_Matrix.md` and `Docs/Customer_Spec_Gap_Audit.md` (requirement IDs, gap status).
- `Docs/USER_MANUAL.md` — operator documentation.

Consolidates the former `DESIGN.md`, `Docs/HMI_Style_Guide.md`, `Docs/Industrial_HMI_and_Software_Quality_Baseline.md`, `Docs/HMI_Page_Scroll_Audit.md`, `Docs/AOI_Competitive_HMI_Reference_Guide.md`, and `Docs/Frontend_Design_Review_and_Rework_Plan.md`; full pre-consolidation text in git history (each path at commit b2c4616).

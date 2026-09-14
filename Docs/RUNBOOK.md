OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor Runbook

This runbook is for on-site IT and support engineers diagnosing AOI Monitor problems on an evaluation, customer, or factory PC, and for executing configuration backup, restore, and rollback. Install and provisioning steps are in `Docs/DEPLOYMENT.md`; operator workflows are in `Docs/USER_MANUAL.md`.

## Expected Stage 1 States (Not Faults)

AOI Monitor is currently a Stage 1 proof of concept with visibly labeled simulation/mock boundaries. The following states are expected and are not incidents:

- Camera status `Simulated`, `Not Connected`, or `Error` — expected unless Folder Camera Simulation is configured. Real GigE/USB3 camera SDK integration is planned for Stage 2.
- MES/ERP `Not Connected` — expected in the current PoC. MES authentication and production traceability are planned for Stage 4. The Mock MES upload action is not production MES/ERP integration: in `Mock REST` mode the app attempts to POST to the configured mock endpoint; if no endpoint is configured, it writes the payload to local JSON. Each attempt is recorded in SQLite.
- 3D Profile Viewer in Sample Data Mode with `3D Camera Not Connected` — it does not connect to a real 3D camera.
- The default engine is the Pixel Difference Prototype Engine, not a trained production ML model. ONNX inference reports a safe `REVIEW` verdict with clear evidence when model loading or inference fails.

## Key Paths

| Item | Path |
| --- | --- |
| Recommended install path | `C:\Program Files\AOI Monitor\` |
| Pilot install path (no installer) | `C:\AOI\AOI_Monitor\` |
| Recommended storage root | `C:\AOI\Data\AOI_Monitor\` |
| Default PoC data root | `%LOCALAPPDATA%\AOI_Monitor\` |
| Default SQLite database | `%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite` |
| Managed image vault | `%LOCALAPPDATA%\AOI_Monitor\image_vault\` |
| Training-set candidates | `%LOCALAPPDATA%\AOI_Monitor\image_vault\training\` |
| Debug-build exports | `AOI_Monitor\bin\Debug\net10.0-windows\exports\` |
| Vendor plugin folder | `C:\AOI\Plugins\` |

## Symptom → Check → Fix

### Build fails with WPF or Windows targeting errors

- Build on Windows.
- Confirm the installed .NET SDK supports Windows desktop/WPF (the project targets `net10.0-windows`).
- Run `dotnet --info` and verify the expected SDK is available.

### App will not start from a deployed package

- A framework-dependent package requires a .NET Desktop Runtime matching the app target framework. Self-contained packages include the runtime.
- Use `-FrameworkDependent` packages only when the client PC already has the matching .NET Desktop Runtime/SDK; otherwise deploy the default self-contained package (see `Docs/DEPLOYMENT.md`).

### App starts but database is unavailable

- Confirm the user account can write to `%LOCALAPPDATA%`.
- If a custom storage root was selected, confirm that folder exists and is writable.
- Use `Export & Trace > DB Integrity` to generate a local health report.

### Imported images do not appear

- Use PNG, JPG, or JPEG for Image Library imports.
- Confirm the file is not locked by another process.
- Check `%LOCALAPPDATA%\AOI_Monitor\image_vault\`.
- Duplicate images are detected by SHA-256 hash and are not imported again — a re-import of an existing file is skipped by design.

### Batch validation or batch import skips files

- Stage 1 validation accepts PNG/JPG/JPEG images.
- Unsupported or unreadable files are logged and skipped; import issues are logged as local review events.
- Invalid CSV manifests produce warnings instead of crashing the app. Bad files, missing images, invalid CSV rows, and database write failures during AI Model Test are logged and skipped where possible.

### Export fails

- Confirm the export folder is writable.
- Close any open CSV, HTML, Markdown, or image files that may be locked by another program.
- Try exporting to a simple local folder such as `C:\Temp\AOI_Exports`.
- Note: when optional evidence is missing during Stage 1 customer package creation, the app writes a warning instead of failing the package — a warning entry is not an export failure.

### Camera shows Not Connected or Error

- This is expected unless folder simulation is configured; setup steps are in `Docs/DEPLOYMENT.md` (Optional Folder Camera Simulation setup).
- Real GigE/USB3 camera SDK integration is planned for Stage 2.

### MES/ERP shows Not Connected

- This is expected in the current PoC; MES authentication and production traceability are planned for Stage 4.
- With Mock REST mode and no configured endpoint, uploads write local JSON payload evidence only; attempts are recorded in SQLite.

### ONNX model does not run, or results come back REVIEW

- The app reports `REVIEW` with clear evidence when the model is missing, invalid, or the runtime fails — this is the safe fallback, not a crash.
- In Settings, run `Test Model Configuration` to verify model file availability, label-map validity, tensor names, ONNX Runtime session creation, and generic detection output compatibility.
- `Ready` is shown only after the current configuration passes the readiness check; review the last model-check result and timestamp.

### Action is blocked with a permission-denied message

- Restricted actions show permission-denied messages and are recorded in the local event log.
- Confirm the selected local role: exporting and deleting logs, Mock MES upload, and Soak Test are Admin-only; Operator and Engineer roles review `Export & Trace` and `Readiness & QA` read-only.

## Configuration Backup

Use `Settings > Backup Configuration` as Admin before changing models, thresholds, hardware adapters, MES settings, central sync settings, or storage paths.

The configuration backup includes: app settings and first-run state; inspection model configuration and model registry metadata; active and historical threshold profiles; recipe revisions; camera source, lighting, MES, central sync, and deployment profile settings.

It excludes customer images and raw production images by default. Keep dataset folders, image vaults, and generated exports in a separate operational backup plan if the customer requires full runtime-data retention.

## Restore Configuration

Use `Settings > Restore Configuration Preview` as Admin. The app validates the backup schema and database schema before allowing restore. Restore preview checks schema compatibility, target storage path, settings changes, existing model/threshold conflicts, missing model files, and missing plugin folders. Do not apply a restore until blocking issues are cleared and warnings are understood.

After restore:

1. Restart AOI Monitor before production use.
2. Confirm storage root, active model, threshold profile, camera source, lighting, MES, and central sync settings.
3. Run the relevant acceptance tests before using restored hardware or model settings for customer/factory evidence.

## Rollback

Before upgrading:

1. Export a configuration backup.
2. Record the current release folder name, app version, active model ID, active threshold profile, storage root, and plugin folder.
3. Copy the current release folder to an archive location or keep the previous zip package.
4. Run the publish/package validation script for the new release.

To roll back:

1. Stop AOI Monitor.
2. Restore the previous release folder or unzip the previous package.
3. Restore the last known-good configuration using `Restore Configuration Preview`.
4. Confirm plugin paths still point to the intended adapter build.
5. Re-run model, camera, lighting, robot, traceability, and factory readiness checks that apply to the deployment profile.

Rollback is complete only when the restored app produces the expected audit events, active model/threshold settings, and acceptance evidence for the target deployment stage. Rotate MES/API credentials after rollback exercises.

## Collecting Evidence For Support

`Export & Trace` is the audit review surface and `Readiness & QA` is the stage-gate/acceptance evidence surface. Operator and Engineer roles can review Inspection History, Review/Disposition Events, Export History, and the Audit Trail in read-only mode; export, delete, Mock MES upload, and Soak Test actions are Admin-only. An Admin can:

- Apply filters by date, board/model, operator, or result; the Audit Trail tab also filters by date, user, role, and action type (`Export & Trace`).
- Export inspection history CSV, review log CSV, and audit trail CSV (`Export & Trace`). The audit CSV includes UTC timestamp, local timestamp, user ID, user role, station ID, action category, action detail, and related record/image/path fields where available.
- Export annotated overlays (`Export & Trace`).
- Run `DB Integrity` for a local database health report, and `Rebuild image index` (`Export & Trace`).
- Create a Stage 1 Customer Package (`Readiness & QA`): a timestamped folder with HTML and Markdown reports, batch/history/review/audit CSVs, annotated images and overlays, engine/model configuration, database health, recipe revision, and calibration profile summaries, a README, and warnings (full contents in `Docs/USER_MANUAL.md`).
- Run a local Soak Test (`Readiness & QA`): repeatedly inspects images from a selected folder through Folder Camera Simulation for the requested duration, supports cancellation, and exports an HTML report with cycle counts, success/failure counts, timing, memory estimates, start/end time, and errors. Use a short duration such as 2 minutes before running an 8-hour evidence soak.

The alarm/event log on the operator screen updates as inspection starts, stops, advances to the next board, completes analysis, saves results, or encounters errors, and records every simulated robot/handler event with cycle time.

## Data Retention And Archive

By default, AOI Monitor keeps live log rows for 30 days. At startup, rows older than the retention window are first copied into a recoverable local archive with their full row payload, then purged from the live tables. When the pre-purge warning is enabled, `Export & Trace` shows an advisory a configurable number of days before the affected rows are archived and purged (default 7 days).

Retention is configured in `System Settings > Data Retention` by an Engineer or Admin: enable or disable automatic purge, set the retention window in days (default 30), and enable or disable the pre-purge warning and its lead time. Disabling purge keeps all live rows in place. The recoverable archive itself is retained indefinitely, so it can be used to reconstruct purged history for audits.

## When To Escalate

- Restore Configuration Preview reports blocking issues that cannot be cleared — do not force a restore.
- Rollback does not reproduce the expected audit events, active model/threshold settings, and acceptance evidence for the target deployment stage.
- Database problems persist after the storage-root and `DB Integrity` checks above.
- A readiness or acceptance claim would rest on simulated, mock, CSV sample, fake adapter, or not-connected evidence. That evidence must not be treated as real production readiness; real-hardware commissioning is covered by the checklist in `Docs/DEPLOYMENT.md`.

When escalating, attach the Admin exports above (inspection history, review log, audit trail CSVs, DB Integrity report) so the decision is reproducible.

## Related Documents

- `Docs/DEPLOYMENT.md` — install, packaging, first-run provisioning, hardware-in-the-loop commissioning checklist.
- `Docs/USER_MANUAL.md` — operator workflows, roles, and prototype boundaries.
- `Docs/standard/00_Index.md` — engineering standard.
- `README.md` — project overview.

Full pre-consolidation text: git history (`Docs/Installation_Guide.md`, `Docs/Deployment_Package_Guide.md`, `Docs/User_Manual.md` at commit b2c4616).

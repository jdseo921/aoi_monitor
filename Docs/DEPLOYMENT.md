OpenAI/Codex and numerous other coding agents will review your output once you are done.

# AOI Monitor Deployment Guide

This guide is for deployment engineers and factory IT staff who install, build, package, provision, or commission AOI Monitor on an evaluation, customer, or factory PC. It is intended for client evaluation and factory-demo preparation. Operator workflows are in `Docs/USER_MANUAL.md`; on-site troubleshooting, backup/restore, and rollback procedures are in `Docs/RUNBOOK.md`.

## Scope

AOI Monitor is a local Windows WPF desktop application for PCBA AOI review workflows. The current build uses local files, a local SQLite database, a managed image vault, Folder Camera Simulation, and the Pixel Difference Prototype Engine by default. It does not install real camera SDKs, robot/PLC drivers, or MES/ERP connectors. Stage 1 image-validation evidence does not satisfy real camera, lighting, robot, safety, or MES readiness gates; claiming Stage 2 or later hardware readiness requires the Hardware-In-The-Loop Commissioning Checklist in this document, executed with real hardware.

## System Requirements

Development / evaluation PC:

- Windows 10 or Windows 11.
- A .NET SDK or runtime that supports Windows desktop/WPF for the project target framework. The project currently targets `net10.0-windows`. For development and evaluation, install the .NET SDK, not only the runtime.
- Local filesystem access for the app data folder, image vault, and export folders.

Customer / factory PC (additional):

- Windows 10/11 Pro or Enterprise, 64-bit.
- Local administrator rights for first install, firewall rules, and hardware driver/plugin installation.
- .NET Desktop Runtime matching the app target framework when using a framework-dependent package. Self-contained packages include the runtime.
- 8 GB RAM minimum; 16 GB recommended for customer dataset validation.
- SSD storage for the app, local SQLite database, generated overlays, reports, and customer validation packages.
- Vendor camera, lighting, robot, PLC, and MES drivers installed only on factory PCs that will use real hardware.
- A time-synchronized PC clock so audit records, acceptance evidence, and readiness packages have reliable timestamps.

Confirm the .NET toolchain with:

```powershell
dotnet --info
```

## Build, Run, And Test From Source

Open PowerShell at the repository root:

```powershell
dotnet build AOI_PCB_Database.slnx
```

Expected result: `AOI_Monitor` and `AOI_Monitor.Tests` build successfully. No customer images or production databases are required.

Run the application:

```powershell
dotnet run --project AOI_Monitor\AOI_Monitor.csproj
```

or from the app folder:

```powershell
cd AOI_Monitor
dotnet run
```

If the application was already built, the debug executable is normally located at:

```text
AOI_Monitor\bin\Debug\net10.0-windows\AOI_Monitor.exe
```

Run tests:

```powershell
dotnet test AOI_PCB_Database.slnx
```

The test project uses isolated temporary folders and generated tiny images. It does not write into the real `%LOCALAPPDATA%\AOI_Monitor` runtime folder.

## Preparing A Deployment / Client Package

Client/evaluator handoff packages should be generated with `Scripts/prepare-client-test-package.ps1`, which wraps `Scripts/publish.ps1` with the correct client-facing docs, sample manifest template, handoff README, and optional zip output.

Recommended client package command:

```powershell
pwsh Scripts/prepare-client-test-package.ps1 -Zip
```

This defaults to a self-contained Windows x64 package and runs the client-demo quality gate before packaging. Use `-FrameworkDependent` only when the client PC already has the matching .NET Desktop Runtime/SDK. Use `-SkipClientDemoGate` only for internal smoke packages that will not be sent to a client.

Lower-level package commands:

```powershell
pwsh Scripts/publish.ps1 -Configuration Release
pwsh Scripts/publish.ps1 -Configuration Release -IncludeTemplates
pwsh Scripts/publish.ps1 -Configuration Release -IncludeSampleManifestTemplate
pwsh Scripts/publish.ps1 -Configuration Release -IncludeTemplates -IncludeSampleManifestTemplate
```

Package contents:

- `app/` contains the published WPF application.
- `CLIENT_HANDOFF_README.md` gives the shortest launch-and-test path.
- `Docs/` is included when documentation is packaged.
- `Templates/` is included only when adapter templates are requested for developer handoff.
- `SampleData/customer_validation_manifest_template.csv` is included only when `-IncludeSampleManifestTemplate` is requested.
- `RUN_RELEASE.md` records package generation settings.

Runtime data is intentionally excluded from release packages: local SQLite databases, image vaults, training images, customer images, generated exports, overlays, and machine-interface JSON.

## Install Paths

Recommended install path:

`C:\Program Files\AOI Monitor\`

For pilot/customer validation PCs without installer infrastructure, unzip the release package to:

`C:\AOI\AOI_Monitor\`

Run `AOI_Monitor.exe` from the `app` folder. Keep the release folder read-only for operators after configuration is complete.

## First-Run Provisioning

### Storage root

Recommended local storage root:

`C:\AOI\Data\AOI_Monitor\`

The storage root contains settings, the SQLite database, image vault, exports, acceptance evidence, and generated readiness/customer packages. Configure it in `Settings > Storage Path` using an Admin role. Do not place production storage inside the application install folder.

For customer dataset validation, keep customer images in a separate dataset folder and reference them from manifests. Do not copy customer datasets into release packages.

### Default local data paths

When no custom storage root is configured, the application creates local PoC data under `%LOCALAPPDATA%\AOI_Monitor\`:

- Default SQLite database: `%LOCALAPPDATA%\AOI_Monitor\aoi_monitor.sqlite`
- Managed image vault: `%LOCALAPPDATA%\AOI_Monitor\image_vault\`
- Training-set candidate images: `%LOCALAPPDATA%\AOI_Monitor\image_vault\training\`

When launched from the Debug build, local export files are commonly written under `AOI_Monitor\bin\Debug\net10.0-windows\exports\`. Customer validation packages are written to the output folder chosen by the user in `Readiness & QA`.

Admin users can change selected local paths in Settings. In this PoC, those settings are local only and are not synchronized with MES or a central configuration server.

### First launch checklist

1. Start the application.
2. Confirm the readiness panel shows local database and image vault availability.
3. Confirm Inspection Engine status is clearly marked as either Pixel Difference Prototype Engine or an ONNX ML Model configuration status.
4. Confirm Camera status is either Simulated, Not Connected, or Error. The UI should not imply real camera hardware is connected.
5. Select a local user and role from the shell.
6. Use small non-confidential PNG/JPG/JPEG images for evaluation.

### Optional Folder Camera Simulation setup

The Stage 2 camera hardware connection is not implemented. For demo use, configure folder simulation:

1. Open `System Settings`.
2. Use an Admin role.
3. Set Camera Source to `Folder Simulation`.
4. Select folders for Top, Side, and Bottom views.
5. Save settings.
6. Return to `Run Inspection` and use Start, Stop, Next Board, and the view selector.

## Firewall And Network

Allow outbound network traffic only for explicitly configured integrations:

- MES/ERP REST endpoints.
- Central sync REST or file-drop destinations.
- Vendor camera, lighting, robot, or PLC network interfaces.

Inbound firewall rules should remain closed unless a vendor adapter or factory integration explicitly requires them. Record customer IT approvals, ports, hostnames, and VLAN/subnet constraints in the factory acceptance checklist.

Secrets are stored only in local configuration files and should be protected by Windows account permissions. Rotate MES/API credentials during commissioning and after rollback exercises.

## Hardware / Vendor Plugin Folders

Hardware/vendor SDK dependencies must remain outside the main application binaries. Place customer or vendor adapter plugins in a dedicated folder such as:

`C:\AOI\Plugins\`

Configure adapter paths in Settings. Keep simulated/fake template adapters labeled as simulated evidence. A fake adapter does not satisfy real camera, lighting, robot, PLC, or MES readiness gates. Adapter starting points ship under `Templates/` (see each template's README).

Plugin folders should contain:

- Adapter assembly and dependencies.
- Adapter manifest JSON.
- Vendor SDK runtime DLLs when allowed by the vendor license.
- Version notes and acceptance-test evidence for the exact plugin build.

## Post-Install Readiness Verification

After installing or restoring configuration on a customer/factory PC (restore and rollback steps are in `Docs/RUNBOOK.md`):

1. Start AOI Monitor as an Admin user.
2. Open `Settings` and confirm the deployment target, storage path, model registry, threshold profile, camera/plugin folders, MES, and central sync settings.
3. Run the applicable validation actions for the deployment profile:
   - Stage 1: Dataset Preflight, AI Model Test, false-call reduction, model acceptance when using ONNX.
   - Stage 2: Camera, lighting, 3D profile, and latency trace evidence.
   - Stage 3: Robot cell and PLC/safety acceptance evidence.
   - Stage 4: MES traceability signoff and MES queue review.
4. Open `Readiness & QA`.
5. Export the Factory Readiness Go/No-Go package.
6. Review the HTML/JSON summary and confirm simulated, mock, CSV sample, fake adapter, and not-connected evidence is not treated as real production readiness.

## Hardware-In-The-Loop Commissioning Checklist

Use this checklist before claiming Stage 2 or later hardware readiness. Template adapters and simulated evidence are not real hardware validation.

### Camera Discovery

- Confirm each vendor camera appears in the vendor SDK discovery tool.
- Record vendor, model, serial number, interface type, IP address or USB path, firmware version, and driver version.
- Confirm the AOI adapter discovers the same device identifiers.
- Evidence required: discovery screenshot, adapter discovery JSON/log, network/USB configuration screenshot.

Pass criteria: every required camera is discovered by both vendor tooling and the AOI adapter with stable identifiers.

### Top / Side / Bottom Assignment

- Assign each physical camera to `Top`, `Side`, or `Bottom`.
- Capture a labeled test frame for every configured view.
- Confirm view labels are persisted in the camera settings and acceptance report.
- Evidence required: frame screenshots for each view, settings export, camera acceptance JSON/HTML.

Pass criteria: no view is missing, duplicated, or assigned to the wrong physical camera.

### Lighting Program Validation

- Map lighting program names for every view.
- Trigger each lighting program from the AOI lighting adapter.
- Verify intensity, channel, strobe timing, and program ID on the lighting controller.
- Evidence required: controller screenshot/photo, lighting acceptance report, per-view command log.

Pass criteria: every required view selects the expected lighting program and reports controller acknowledgement.

### Trigger-To-Frame Timing

- Run software or hardware trigger tests for each camera/view.
- Measure trigger command, strobe acknowledgement, frame timestamp, and frame received timestamp.
- Check timeout behavior by disconnecting or disabling one device in a controlled test.
- Evidence required: timing CSV/log, frame metadata, timeout/fault screenshot.

Pass criteria: trigger-to-frame latency stays within the customer cycle-time budget, and timeout failures are reported safely.

### 3D Profile Acquisition

- Acquire real 3D height/profile data from the configured sensor.
- Verify dimensions, unit, X/Y pitch, invalid-height count, and source kind.
- Confirm sample CSV profiles are labeled as simulation/sample evidence only.
- Evidence required: 3D profile acceptance JSON/HTML, height-map screenshot, sensor configuration screenshot.

Pass criteria: real 3D frames are acquired with valid dimensions, calibrated units, and acceptable invalid-height counts.

### Robot Load / Inspect / Unload

- Run load, move-to-inspect, inspection hold, unload, and reset steps.
- Verify board ID, lot, station, gripper/clamp status, and cycle timestamps.
- Confirm invalid transitions are rejected.
- Evidence required: robot acceptance report, robot controller log, cycle video or screenshots, audit log export.

Pass criteria: the robot completes the sequence within cycle-time limits, rejects invalid transitions, and records audit evidence.

### PLC Safety Interlock Tests

- Verify guard door, light curtain, board clamp, air pressure, servo ready, and safety fault inputs.
- Confirm robot motion is blocked when any required interlock is unsafe.
- Confirm reset requires the approved operator/safety sequence.
- Evidence required: PLC input screenshot, safety acceptance report, blocked-motion log.

Pass criteria: every unsafe interlock blocks motion and is visible in AOI safety status.

### E-Stop Test

- Trigger the emergency stop during a controlled robot cycle.
- Confirm motion stops, AOI records the e-stop state, and reset is required before motion resumes.
- Verify the event is logged with timestamp and operator.
- Evidence required: e-stop test video/screenshot, robot acceptance report, audit log export.

Pass criteria: e-stop blocks motion immediately and cannot be cleared without the documented reset sequence.

### Final Pass / Fail Criteria

Pass requires all of the following:

- Real camera discovery and frame acquisition completed for required views.
- Lighting program validation completed for required views.
- Trigger-to-frame timing is within the approved budget.
- Real 3D profile acquisition completed when in scope.
- Robot load/inspect/unload cycle completed when in scope.
- PLC safety and e-stop tests passed when in scope.
- Factory readiness package and factory acceptance checklist exported.
- All evidence clearly separates real hardware from simulated/template evidence.

Fail if any required real device is missing, mislabeled, simulated, timing out, unsafe, or unverified.

### Evidence Package

Attach or export:

- Camera discovery screenshots and adapter logs.
- Top/Side/Bottom frame screenshots.
- Lighting controller screenshots and command logs.
- Trigger-to-frame timing CSV/log.
- 3D profile report and screenshot.
- Robot acceptance report and cycle evidence.
- PLC safety and e-stop evidence.
- Factory readiness Go/No-Go package.
- Factory acceptance checklist.
- Audit trail covering operator, engineer, and admin actions.

## Related Documents

- `Docs/RUNBOOK.md` — on-site troubleshooting, configuration backup/restore, rollback, evidence collection.
- `Docs/USER_MANUAL.md` — operator workflows and prototype boundaries.
- `Docs/VALIDATION.md` — acceptance and validation procedures.
- `Docs/standard/00_Index.md` — engineering standard (build, deploy, and field controls).
- `README.md` — project overview.

Full pre-consolidation text: git history (`Docs/Installation_Guide.md`, `Docs/Deployment_Package_Guide.md`, `Docs/Hardware_In_The_Loop_Checklist.md` at commit b2c4616).

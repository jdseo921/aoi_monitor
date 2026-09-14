OpenAI/Codex and numerous other coding agents will review your output once you are done.

# ADR-0002 — Wire the lighting adapter plugin loader into the runtime factory (external mode)
- **Status:** Proposed
- **Date:** 2026-09-09T00:00:00Z
- **Author / role-hat:** Claude Code (AI change author) for the repository owner / Software Architect role-hat. Solo-team rule §57.1-5: acceptance requires the Software Architect's recorded self-review with the §7 (VOL01) CC-1/CC-3 cooling-period compensating control; the cooling interval must be recorded at acceptance.
- **Decision Register impact:** none
- **Requirement IDs affected:** DR-03, DR-12 (ARCHITECTURE.md follow-up register), CAM-003, CAM-004, VOL10 §32; VOL03 §15 (module boundaries)

## Context
The lighting seam has a complete, test-pinned plugin loader that no application code can reach: `LightingAdapterPluginService` (`AOI_Monitor/Services/LightingControllerFactory.cs:57-150`) performs manifest discovery, identity validation, and `Assembly.LoadFrom`, but `LightingControllerFactory.Create` (`:10-17`) switches only over `none|simulated|tcp-text|serial-text`, and `LightingSettings` (`AOI_Monitor/Models/LightingSettings.cs:3-26`) has no `AdapterFolder`. The loader's only caller is `AOI_Monitor.Tests/VendorAdapterTemplateTests.cs:139`. Meanwhile `Templates/LightingAdapterTemplate/README.md:11` instructs vendors to "load it through the lighting adapter plugin service" — an impossible path — and the template's `template-fake` mode string normalizes to `None` (`LightingSettingsService.cs:110-119`). The camera seam provides an exact working mirror: `CameraSourceSettings.AdapterFolder` (`Models/CameraSourceSettings.cs:14`), a Settings folder field (`Views/SettingsView.xaml:679`), loader consultation in the factory (`Services/VisionCameraAdapters.cs:158-168`), and backup coverage (`Services/ConfigurationBackupService.cs:461-462`, camera only). VOL10 flags both loaders as unsigned `Assembly.LoadFrom` arbitrary-code-execution paths (CAM-003, P0; nonconformity N-32-1).

## Options considered
| # | Option | Rejected because |
|---|---|---|
| 1 | Retitle the loader as validation-tooling-only and correct the guide/template so vendors target the TCP/serial text protocol | Orphans a fully test-pinned loader cited as a verified seam strength (ARCHITECTURE.md); requires rewriting VOL03/VOL10 references; blocks vendors whose lighting controllers expose SDK-only APIs (no text protocol); diverges the lighting seam from the camera seam pattern for no capability gain. |
| 2 | Wire plugin mode through `LightingControllerFactory` + Settings, mirroring the camera seam | — (chosen) |

## Decision
`LightingSettings` gains `AdapterFolder` and an `external` mode (with `NormalizeMode` alias); `LightingControllerFactory.Create` branches to `LightingAdapterPluginService.LoadFactory` for `external`, degrading to a diagnostic `NullLightingController` on any load failure (camera precedent `VisionCameraAdapters.cs:158-168`); the Settings page gains the folder field mirroring `CameraAdapterFolderText`; `ConfigurationBackupService` covers the lighting adapter folder alongside the camera one; the template README is corrected to describe the now-real path. Conditions bound to this decision: (1) the manifest `contractVersion` handshake (DR-12) lands in the same change for **both** loaders; (2) manifest `assemblySha256` verification (CAM-004) lands before any real vendor lighting package is accepted; (3) full plugin-intake hardening (CAM-003 signature/allowlist verification, CAM-010 native-DLL search-path control, CAM-012 directory-ACL/Authenticode startup checks) is scheduled as its own security wave, and real-vendor package acceptance is blocked until it lands. Revisit trigger: a customer mandates a lighting protocol family (e.g. pure text-over-TCP) that makes the plugin path dead weight.

## Consequences
Positive: vendors get one honest, documented integration path; lighting reaches parity with the camera seam; DR-12's handshake protects both loaders at once. Negative: the runtime plugin surface grows before the security wave lands — mitigated by conditions (2)/(3) above and by `external` mode requiring explicit Admin configuration. Migration obligations: correct `Templates/LightingAdapterTemplate/README.md` and stamp `contractVersion` into both template manifests — owner: repository owner (solo); deadline: the lighting vendor-path wave in the Stage 2 code-readiness plan (ARCHITECTURE.md).

## Verification
`VendorAdapterTemplateTests`: template lighting factory loads through the runtime `LightingControllerFactory.Create("external")` path and a mismatched `contractVersion` manifest is rejected with the actionable rebuild message; Settings round-trip test for `AdapterFolder`; backup/restore test covering the folder. Registered as test-class rows in the §52 catalogue at implementation.

## Open decisions raised
none

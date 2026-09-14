OpenAI/Codex and numerous other coding agents will review your output once you are done.

# ADR-0001 — Require on-disk frames from camera adapters; hard-fail acceptance on missing SourcePath
- **Status:** Proposed
- **Date:** 2026-09-09T00:00:00Z
- **Author / role-hat:** Claude Code (AI change author) for the repository owner / Software Architect role-hat. Solo-team rule §57.1-5: acceptance requires the Software Architect's recorded self-review with the §7 (VOL01) CC-1/CC-3 cooling-period compensating control; the cooling interval must be recorded at acceptance.
- **Decision Register impact:** none
- **Requirement IDs affected:** DR-01 (ARCHITECTURE.md follow-up register), CAM-030, CAM-034 (interaction only), VOL10 §32

## Context
The inspection pipeline is file-based end to end: `MonitorView.LoadNextBoardAsync` rejects any board context whose `ImagePath` fails `File.Exists` (`AOI_Monitor/Views/MonitorView.xaml.cs:646`), and `BenchmarkInspectionService` applies the same gate (`AOI_Monitor/Services/BenchmarkInspectionService.cs:189,219`). Every engine consumes frames as image files. Camera acceptance currently only WARNs when an adapter returns frames without a readable on-disk `SourcePath` (`AOI_Monitor/Services/CameraAcceptanceTestService.cs:240-241,278-279`, pinned as WARN by `AOI_Monitor.Tests/Stage2DeriskingSeamTests.cs:70-81`). A vendor adapter that streams buffer-only frames would therefore pass acceptance with a warning while every frame it produces is uninspectable at run time — a pilot-day failure discovered only in front of the customer. The camera adapter template already demonstrates the persistence obligation by writing each frame to disk (`Templates/CameraAdapterTemplate/FakeVisionCameraAdapter.cs:97-127`). `CameraFrame` (`AOI_Monitor/Services/CameraFrame.cs:18-36`) carries no pixel buffer, so the alternative — bridging buffer frames into the image vault inside `GenericVisionCameraSource` — would extend the adapter contract with buffer-lifetime semantics (VOL10 CAM-035) that no current adapter carries.

## Options considered
| # | Option | Rejected because |
|---|---|---|
| 1 | Bridge buffer frames to the image vault inside `GenericVisionCameraSource` | Architecture-bending: adds pixel-buffer members and buffer-lifetime rules to `CameraFrame`/`IVisionCameraAdapter` that no implementation carries; duplicates the persistence obligation the template already places on adapters; enlarges the P0 buffer-safety surface (CAM-035) before any vendor SDK is selected. |
| 2 | Keep the WARN stopgap | A buffer-only adapter passes acceptance yet produces zero inspectable frames at `MonitorView.xaml.cs:646`; the gap surfaces on pilot day, not in acceptance. |
| 3 | Criteria-driven hard-fail: adapters must persist frames; acceptance fails on missing/unreadable `SourcePath` | — (chosen) |

## Decision
Camera adapters SHALL persist every accepted frame (or a bounded rolling buffer of frames) to disk and set `CameraFrame.SourcePath` to the readable file. `CameraAcceptanceCriteria` gains `RequireOnDiskFrames` (default `true` when the source key is `generic-vision-adapter`); a frame with a blank or unreadable `SourcePath` counts as a **Failure**, not a Warning, when the criterion is set. The vendor guide (ARCHITECTURE.md "Camera Frame Metadata Requirements") is updated from "acceptance warns" to "acceptance fails". Revisit triggers: (a) a customer-selected vendor SDK that cannot persist frames at the required line rate, (b) introduction of free-running acquisition (ADR-0004 revisit), where file I/O per frame may become the bottleneck.

## Consequences
Positive: acceptance evidence and runtime behavior agree; a vendor package that cannot feed the pipeline fails at acceptance with an actionable message. Negative: vendors must budget disk I/O per frame; a rolling-buffer retention rule becomes vendor guidance. Migration obligations: update `Stage2DeriskingSeamTests` (WARN pin → FAIL case) and the ARCHITECTURE vendor guide — owner: repository owner (solo); deadline: Wave 1 of the Stage 2 code-readiness plan (ARCHITECTURE.md).

## Verification
`Stage2DeriskingSeamTests`: acceptance run with a buffer-only fake adapter and `RequireOnDiskFrames=true` asserts run Failure (and WARN when the criterion is off, preserving folder/null demo paths); `VendorAdapterTemplateTests` continues to pin that the template adapter persists frames. Registered as a test-class row in the §52 catalogue at implementation.

## Open decisions raised
none

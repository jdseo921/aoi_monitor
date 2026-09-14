OpenAI/Codex and numerous other coding agents will review your output once you are done.

# ADR-0004 — Keep operator-paced synchronous pull acquisition for the Stage 2 camera pilot
- **Status:** Proposed
- **Date:** 2026-09-09T00:00:00Z
- **Author / role-hat:** Claude Code (AI change author) for the repository owner / Software Architect role-hat. Solo-team rule §57.1-5: acceptance requires the Software Architect's recorded self-review with the §7 (VOL01) CC-1/CC-3 cooling-period compensating control; the cooling interval must be recorded at acceptance.
- **Decision Register impact:** none
- **Requirement IDs affected:** CAM-034, CAM-018, CAM-019, CAM-020, CAM-021, CAM-023, CAM-030 (VOL10 §32)

## Context
The live pipeline is single-frame and operator-paced by design: Start does not begin a loop; each Next Board or simulated-robot inspect step pulls exactly one frame through `ICameraSource.GetNextFrame` and runs one analysis (`AOI_Monitor/Views/MonitorView.xaml.cs:257,318,698`). There is no producer/consumer decoupling anywhere between acquisition and engine. VOL10 CAM-034 requires frames to flow acquisition→engine through a bounded queue (default depth 8) with a declared drop policy, and prohibits unbounded buffering; its hazard is buffer growth when a producer outruns a consumer. In the current pull model no buffering exists at all — the "queue" is depth zero by construction — so the hazard CAM-034 guards against cannot occur. Introducing a queue now would add concurrency machinery with no producer to feed it, untestable without real hardware, ahead of the camera-lifecycle work (CAM-018 state machine, CAM-019 reconnection, CAM-023 timeouts) that must land on the pull path regardless of acquisition model.

## Options considered
| # | Option | Rejected because |
|---|---|---|
| 1 | Implement the CAM-034 bounded queue now | No free-running producer exists; the queue would be a synchronous depth-1 pass-through adding thread-safety surface and test burden without exercising a real drop policy; it would be rewritten anyway when a real acquisition loop's threading model is known. |
| 2 | Keep the synchronous pull model for the Stage 2 pilot; implement CAM-034 in the wave that introduces free-running or hardware-triggered continuous acquisition | — (chosen) |

## Decision
The Stage 2 camera pilot retains the operator-paced synchronous pull model. CAM-018/019/020/021 (connection state machine, bounded reconnection, reset sequence, Faulted gating), CAM-023 (trigger/frame timeouts with production counters), and CAM-030 (explicit acquisition-failure results, no stale-frame re-delivery) are implemented on the pull path. CAM-034's bounded queue with declared drop policy is implemented in the same change set that introduces free-running or continuous hardware-triggered acquisition — before any such loop is merged, never after. Until then no code may introduce any frame buffering between acquisition and engine (the CAM-034 prohibition on unbounded buffering binds immediately). Revisit triggers: (a) introduction of a free-running acquisition loop or continuous hardware-trigger mode, (b) Stage 3 robot-cycle automation pacing inspections faster than the operator, (c) a customer line-rate requirement that the pull model cannot meet.

## Consequences
Positive: the camera-lifecycle hardening lands on the code that actually runs in the pilot; no speculative concurrency. Negative: the acquisition→engine threading model changes again when free-running acquisition arrives — accepted, since the queue design should be made against the real producer's threading, buffer, and drop-policy needs. Migration obligation: the free-running-acquisition change set carries the CAM-034 queue, its drop-policy declaration, and its bounded-buffer tests as entry criteria — owner: repository owner (solo); deadline: bound to that future change set, tracked by this ADR's revisit triggers.

## Verification
The camera-lifecycle wave's contract tests assert the pull path holds no frame buffer (adapter `TryGetFrame` results are consumed or dropped, never queued); review item on any future acquisition-loop PR: CAM-034 queue present with declared drop policy, else reject (auto-reject AR-12 covers the unbounded case). Registered as review-item rows in the §52 catalogue at implementation.

## Open decisions raised
none

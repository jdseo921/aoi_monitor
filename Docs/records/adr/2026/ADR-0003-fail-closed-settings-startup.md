OpenAI/Codex and numerous other coding agents will review your output once you are done.

# ADR-0003 — Fail closed on corrupt security-posture settings; quarantine, audit, and require an operator decision
- **Status:** Proposed
- **Date:** 2026-09-09T00:00:00Z
- **Author / role-hat:** Claude Code (AI change author) for the repository owner / Software Architect role-hat. Solo-team rule §57.1-5: acceptance requires the Software Architect's recorded self-review with the §7 (VOL01) CC-1/CC-3 cooling-period compensating control; the cooling interval must be recorded at acceptance.
- **Decision Register impact:** none
- **Requirement IDs affected:** DR-09, DR-11 (ARCHITECTURE.md follow-up register), VOL07 (identity/authorization posture), VOL12 (operator-safe failure)

## Context
Twelve settings services share a copy-pasted load pattern whose catch block does `Trace.WriteLine` and substitutes defaults — no quarantine, no audit event, no operator visibility. For three security-posture files the silent default is a downgrade: a corrupt `storage_root_settings.json` silently redirects the database, image vault, and evidence to the default root (`AOI_Monitor/Services/StorageRootSettingsService.cs:32-37`); a corrupt `operating_mode_settings.json` falls back to Demo mode, re-enabling demo data and the password-less role selector (`OperatingModeSettingsService.cs:48-56`, `OperatingModePolicyService.cs:153-160`); a corrupt `authentication_settings.json` falls back to `DemoLocalRoleSelector` (password-less), and a corrupt `local_users.json` yields an empty user store that the next save permanently overwrites (`AuthenticationSettingsService.cs:65-73,257-275`). The intended fail-visible pattern already exists in-repo: `AlarmEventService` quarantines a corrupt snapshot, raises a Critical self-alarm, and writes atomically via temp-then-replace (`AlarmEventService.cs:381-444`). The register asks for the product decision on lockout UX: blocking startup on a corrupt auth store risks locking the operator out; continuing silently risks running a factory console password-less without anyone deciding that.

## Options considered
| # | Option | Rejected because |
|---|---|---|
| 1 | Keep silent defaults (status quo) | A security-posture downgrade happens with no audit trail and no operator awareness; on a pilot machine a corrupt file turns authentication off invisibly. |
| 2 | Hard lockout: refuse to start on any corrupt security file | A single corrupt file bricks the station with no recovery path an operator can execute; disproportionate for `storage_root` where a safe labeled default exists. |
| 3 | Fail closed with quarantine + audit + explicit operator decision; safe defaults are the most restrictive posture, never Demo/password-less | — (chosen) |

## Decision
On a deserialize/IO failure of a security-posture settings file, the app quarantines the corrupt file (`<name>.corrupt-<UTC timestamp>`, mirroring `AlarmEventService.QuarantineCorruptSnapshot`), records a `SETTINGS_LOAD_FALLBACK` audit event naming the file (deferred until `AoiDatabase.Initialize` succeeds for the storage-root file, whose load precedes DB init), and blocks startup with an operator-safe dialog requiring an explicit decision: **Shut down** (default) or **Continue with safe default**, where the safe default is the most restrictive posture — storage root: default root, clearly stated in the dialog; operating mode: Production-restrictions posture, not Demo; authentication: the configured authentication mode is never silently replaced by the password-less selector — continuing runs a clearly labeled recovery state whose entry is itself an audited event. A corrupt `local_users.json` is never overwritten by a subsequent save while quarantine-recovery is unresolved. Non-security settings files (camera, lighting, MES, etc.) keep default-substitution but gain the same quarantine + audit-event treatment via the shared settings store (DR-11). Revisit trigger: MES-based authentication (Stage 4) replacing the local role model.

## Consequences
Positive: no invisible posture downgrade; every fallback is auditable; the operator decision is recorded. Negative: a corrupt file now interrupts startup — acceptable for a factory console where silent policy change is the worse failure; recovery is a labeled, audited path instead of an accident. Migration obligations: implement via the shared `SettingsFileStore` (DR-11) so all 13 services inherit quarantine + audit in one pattern — owner: repository owner (solo); deadline: the configuration-robustness wave in the Stage 2 code-readiness plan (ARCHITECTURE.md).

## Verification
Per-service tests: a truncated settings file produces a quarantine file plus an audit row and (for the three security files) the blocking-decision path; a test pins that `local_users.json` corruption cannot be silently overwritten; `Stage2DeriskingSeamTests`-style pin that Demo/password-less is never the corrupt-file fallback. Registered as test-class rows in the §52 catalogue at implementation.

## Open decisions raised
none

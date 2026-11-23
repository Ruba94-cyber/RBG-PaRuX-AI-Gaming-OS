# CLAUDE.md — PaRuX Development Contract

## Mission
Finish RBG PaRuX AI Studio (PaRuX) into a best-in-class source-available local-AI operating environment. Claude/Codex are development tools only; shipped core AI operation must not depend on proprietary cloud inference.

## Mainline consolidation and test policy (owner direction, 2026-11-23)
- **Canonical development branch: `main`.** Prefer reviewed, coherent changes committed there when safe. Create a short-lived feature branch/PR only for conflict isolation, security review, or unfinished work; merge back promptly and do not accumulate stacked draft PRs.
- **No GitHub Actions.** Never add, run, restore or reintroduce `.github/workflows/*`. Use the opt-in local Windows acceptance runner `tools/validation-runs/verify-main-local.ps1`, keeping TRX reports private and local.
- PR #17 was **squash-merged** to `main` at `2e2d630ff1e52f8cb7281f215a7d45e47b2de750`. This merges *source only*, not a certified release. Do not claim post-merge full xUnit, real Playnite/physical pad/HDR/eGPU, Corporate-clean-PC, AppContainer isolation, signing or installer acceptance without new real evidence.
- Historic PRs #8/#9/#12–#16 have been superseded/closed; do not reopen them to resume work. Remaining `worktree-agent-*` branches contain unique WIP and must be compared/reviewed before integration, never force-reset or blindly deleted.
- Branch pruning uses the manual dry-run-first `tools/prune-integrated-branches.ps1` on an authorized checkout. It is **not** a GitHub Actions workflow, and deliberately excludes worktrees/remote checkpoints.
- The 2026-11-22 product pivot holds: Playnite is optional and owner-managed; new native Gaming Home redesign is paused. Keep controller/resource handoff and explicit owner consent gates.

## ONE canonical execution plan
Read and maintain exactly one operational plan:

`PARUX_EXECUTION_PLAN.md`

Do not use archived PaRuX/vNext/early-PaRuX plans, release checklists or execution logs to choose the next task. They are historical evidence only in Git history or retained legacy evidence files.

Truth order:
1. CURRENT WORKING CODE
2. CURRENT TESTS
3. REAL RUNTIME / LIVE EVIDENCE
4. `PARUX_EXECUTION_PLAN.md`
5. HISTORICAL DOCS

Before substantial work:
- inspect branch, HEAD, git status, recent log and local diff;
- read only relevant sections of `PARUX_EXECUTION_PLAN.md`;
- verify the first actionable incomplete item against current code;
- preserve unrelated user/agent changes;
- if another writer is active, isolate work deliberately.

## Core product constraints
- Local-first/local-only core AI inference.
- Chat is the default/home workspace.
- Advanced capabilities become first-class workspaces with Normal vs Advanced/Studio disclosure.
- PRX Orbit is the approved identity direction.
- Component setup is Detect → Locate existing / Install with PaRuX / Skip.
- Ownership: PARUX_MANAGED / USER_MANAGED / SYSTEM_DETECTED.
- Never silently mutate USER_MANAGED software.
- Runtime Manager owns components/runtimes; Model Hub owns models; Update Manager coordinates PaRuX-managed updates.
- Missing internal capability is implementation work, not an external blocker.

## Engineering behavior
- Audit before coding; reuse existing services/contracts/stores.
- Preserve auth, pairing, LAN security, Gaming hard-off, cancellation, backup safety and user data.
- No fake success. Compile-only/UI-only/mock-only is not DONE.
- Autonomous work must have bounded retries, cancellation, observable state and honest failures.
- Prefer small coherent end-to-end slices over broad speculative refactors.
- Keep the active Execution Plan concise; long evidence belongs in commit messages or historical execution logs.

## Definition of Done
A capability is DONE only when applicable implementation, focused tests, regression tests, real integration, failure/recovery paths, platform wiring, security/resource review and status reconciliation are complete.

Allowed status: DONE / PARTIAL / MISSING / BLOCKED-EXTERNAL.

## Long-run rule
After each verified batch: update `PARUX_EXECUTION_PLAN.md`, commit coherently, then continue to the next highest-priority actionable gap. Do not stop merely because the roadmap is large or an internal capability is missing.

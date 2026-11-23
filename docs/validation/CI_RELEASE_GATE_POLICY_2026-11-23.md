# GitHub Actions and release-gate policy — 2026-11-23

Status: **validation-only exception; shipping is not authorized**.

The historical release gate rejected every file under `.github/workflows`. Current development deliberately uses GitHub-hosted ephemeral Windows runners for **read-only, synthetic** C# compilation and tests. Rejecting all workflows therefore caused `ValidationScriptSelfTests.Release_gate_on_a_non_Windows_host_fails_instead_of_reporting_PASS_without_the_asset_harness` to fail before it reached the intended non-Windows branch.

## Guardrails

`tools/release-gate.ps1` now allows only the exact six Git blobs enumerated in its `$approvedValidationWorkflows` allowlist. The gate fails closed if any workflow is new, moved, a reparse point, has an unexpected staged Git blob SHA, or has a dirty working-tree/index copy. This is a **frozen source review**, not broad permission to run arbitrary new workflows.

The approved workflows only compile and run synthetic tests on GitHub-hosted Windows runners; they have `permissions: contents: read`, no persistent checkout credentials and no signing, packaging, uploading to Store, or release publication. Any workflow edit requires independent review of triggers, jobs, permissions, commands, third-party actions and network/secret exposure before updating the pinned SHA.

The owner still requires the project's full release acceptance gates, real Windows UX, physical controllers/Playnite, clean-machine installs, Corporate locked-down tests, signing authorization and runtime evidence. A green synthetic runner is not a substitute.

## Validation

- Focused `ValidationScriptSelfTests.Release_gate_on_a_non_Windows_host_fails...` runs in `ci-gate-validation.yml` with a deliberate non-Windows seam and must return nonzero for the expected reason.
- Full xUnit on the integration branch remains separately required. The 2026-11-23 initial run had 15,925 passed, 13 failed, 9 skipped; the browser-route and test fixture errors are addressed by PR #19. The AppContainer runner failures are **not** waived.
- `tools/release-gate.ps1` must still fail for an unknown workflow or a modified pinned workflow. These deny cases must be checked again before a real signed release.

No external secrets, user data, signing certificates or business documents belong in GitHub Actions.

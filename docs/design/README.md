# Design notes

This folder holds the design notes, plans, spike results, and plan review logs written while
building this project, copied from the private planning folder with machine paths, hostnames, and
personal identifiers removed. Each file is dated and was not revised after the fact; where a later
decision reversed an earlier one, both are here. For how these fit into the way the project was
built, see [How this was built](../how-this-was-built.md).

| Date | File | What it is |
|---|---|---|
| 2026-09-06 | [mvp-design.md](mvp-design.md) | Decisions from the first design interview; the starting scope for the MVP. |
| 2026-09-06 to 2026-09-13 | [mvp-plan.md](mvp-plan.md) | The phased MVP plan, with status notes added as phases finished. |
| 2026-09-06 | [mvp-plan-review-log.md](mvp-plan-review-log.md) | Codex review of the MVP plan, six rounds. |
| 2026-09-06 to 2026-09-19 | [spike-results.md](spike-results.md) | What throwaway code found out about a real ASA server, plus the later ban-list spike. |
| 2026-09-07 to 2026-09-08 | [ui-design.md](ui-design.md) | Notes on the look of the web UI, written before the components were built. |
| 2026-09-13 to 2026-09-14 | [release-plan.md](release-plan.md) | Plan for the first public release: packaging, installer, CI, repository policy, smoke test. |
| 2026-09-13 | [release-plan-review-log.md](release-plan-review-log.md) | Codex review of the release plan. |
| 2026-09-13 to 2026-10-02 | [post-1.0-plan.md](post-1.0-plan.md) | The work after the first release (B0 to B10 and the rest), with an implementation status table kept up to date. |
| 2026-09-13 | [post-1.0-plan-review-log.md](post-1.0-plan-review-log.md) | Codex review of the post-1.0 plan. |
| 2026-09-18 | [b3-scheduled-actions.md](b3-scheduled-actions.md) | Brief for the two scheduled-actions pull requests. |
| 2026-10-01 | [tweaks-2026-10-01.md](tweaks-2026-10-01.md) | Plan for a batch of tweaks: live SteamCMD output, editor and console keys, CurseForge links. |
| 2026-10-01 | [tweaks-2026-10-01-review-log.md](tweaks-2026-10-01-review-log.md) | Codex review of the tweaks plan. |
| 2026-10-01 | [b4-crash-restart.md](b4-crash-restart.md) | Plan for crash detection and automatic restart. |
| 2026-10-01 | [b4-crash-restart-review-log.md](b4-crash-restart-review-log.md) | Codex review of the crash-restart plan. |
| 2026-10-02 | [pre-release-review.md](pre-release-review.md) | The whole-solution review before 1.0.0, with 30 findings. |
| 2026-10-02 | [pre-release-review-decisions.md](pre-release-review-decisions.md) | The decision on each finding: fix, defer, or close, and what was done. |
| 2026-10-02 | [pre-release-fix-batch.md](pre-release-fix-batch.md) | Brief for the batch of small fixes from the review. |
| 2026-10-02 | [pre-release-fix-2-background-loops.md](pre-release-fix-2-background-loops.md) | Plan for finding 2: background loops must not stop the host. |
| 2026-10-02 | [pre-release-fix-2-background-loops-review-log.md](pre-release-fix-2-background-loops-review-log.md) | Codex review of the finding 2 plan. |
| 2026-10-02 | [pre-release-fix-6-login-lockout.md](pre-release-fix-6-login-lockout.md) | Plan for finding 6: login lockout under parallel requests. |
| 2026-10-02 | [pre-release-fix-6-login-lockout-review-log.md](pre-release-fix-6-login-lockout-review-log.md) | Codex review of the finding 6 plan. |
| 2026-10-02 | [pre-release-fix-9-firewall-names.md](pre-release-fix-9-firewall-names.md) | Plan for finding 9: firewall rule names that do not collide between installs. |
| 2026-10-02 | [pre-release-fix-9-firewall-names-review-log.md](pre-release-fix-9-firewall-names-review-log.md) | Codex review of the finding 9 plan. |

Paths and names in angle brackets, such as `<repo>` or `<DataRoot>`, stand in for the removed
machine-specific values. References to `docs/.untracked/...` and to files such as `HANDOVER.md`
point at the private planning folder and are not part of the repository.

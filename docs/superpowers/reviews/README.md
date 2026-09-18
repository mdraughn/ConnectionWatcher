# Architecture reviews

Read-only once-overs of the codebase, one file per review, pinned to the commit
they examined. Findings carry ids (E = engine, P = persistence, H = host/API/
integrations, S = structure) that the follow-up plan under `../plans/` cites.
Each review ends with the owner's decisions, dated, so the plan can start from
them without re-litigating.

| Date | Commit | Report | Follow-up plan |
|---|---|---|---|
| 2026-09-16 | the tree just before `52314f3` (the plan commit) | chat-only, not recoverable; its decisions are recorded at the top of the plan | `../plans/2026-09-17-cleanup-and-concurrency.md` |
| 2026-09-18 | `6e8bc13` | `2026-09-18-codebase-review.md` | (next) |

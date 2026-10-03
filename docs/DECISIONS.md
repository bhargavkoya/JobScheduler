# Decisions Log

ADR-style log of non-obvious choices made during the build, beyond what's already spelled out in `docs/PRD.md`. Appended to at the end of every phase. See [`ARCHITECTURE.md`](ARCHITECTURE.md) for how these land in the actual code.

## From the approved implementation plan (2026-10-03)

| # | Question | Decision |
|---|---|---|
| 1 | Quartz persistence | RAMJobStore (in-memory) + DB-rehydration-on-boot, rather than Quartz's own ADO.NET job store — our Postgres `Job`/`JobRun` tables are the source of truth, so a second persistence layer inside Quartz would be redundant for a POC. |
| 2 | Rule engine library | NCalc (small, MIT-licensed expression evaluator) rather than hand-rolling an expression parser for the pluggable calculation engine. |
| 3 | Team modeling | `Team` is a real, extensible table (not a fixed Business/Technical enum) — PRD §5 implies teams double as the multi-tenancy unit, so the model needs to support more than two fixed values. |
| 4 | Claims storage | A lightweight custom `UserClaim` table instead of full ASP.NET Core Identity, consistent with "no SSO yet" and POC scope. |
| 5 | Follow-up cadence | PRD specifies 24h; made configurable per job template so a live demo can shorten it (e.g. to 1 minute) rather than hardcoding 24h. |
| 6 | Audit export (PRD §4.8) | CSV only for the POC; PDF generation deferred to the Phase 10 stretch list given its low demo value relative to the added dependency. |
| 7 | Notification-preference enforcement (PRD §4.6) | Treated as Phase 10 stretch — PRD mentions it but §7's MVP slice doesn't call it out explicitly. |
| 8 | Trigger-based webhook auth | A simple per-job shared-secret header, rather than a full signature-verification scheme, given POC scope. |

## Phase 0

| # | Question | Decision |
|---|---|---|
| 9 | PostgreSQL host port | Mapped to **5433**, not the default 5432 — the dev machine already has a native PostgreSQL install bound to 5432, and remapping the container avoids touching that pre-existing, unrelated install. |
| 10 | API project style | ASP.NET Core Web API scaffolded with MVC controllers (`--use-controllers true`), not minimal APIs — matches `CLAUDE.md`'s "keep controllers thin; logic lives in Application services." |
| 11 | DbContext seed in Phase 0 | A trivial, non-domain `SystemInfo` marker table/row — added only to prove the EF Core migration pipeline works before any real domain entities exist (Phase 1). Will be deleted once real entities land. |
| 12 | Frontend location | Kept nested at `job-scheduler/web/` inside this one repo (not split into a sibling folder or a separate git repository) — confirmed with the user during Phase 0. |
| 13 | Tailwind version | Tailwind CSS v4 via the `@tailwindcss/vite` plugin (no separate PostCSS/tailwind.config.js needed) rather than v3's config-file setup. |

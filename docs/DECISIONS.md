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

## Phase 7

Items 6, 7 and 8 above (PDF, notification preferences, webhook auth) were built here, outside the PRD section 7 MVP slice, at the owner's request.

| # | Question | Decision |
|---|---|---|
| 14 | Webhook credential | Random 256-bit token per job, shown once, stored as a SHA-256 hash and compared in constant time (a leaked database does not leak usable tokens). Unknown job, wrong job type and wrong token all return the same 401 so job ids cannot be probed. |
| 15 | Webhook idempotency | Only an explicit `Idempotency-Key` header deduplicates. Hashing the body was rejected: an external system legitimately sends identical payloads repeatedly (for example an empty "report ready" ping) and those must each start a run. |
| 16 | Trigger-based job lifecycle | Behaves like Recurrent: ready again after a completed run. A Failed trigger job needs a manual Retry first, so a flood of webhooks cannot pile runs on a broken job. |
| 17 | Notification preference enforcement | One decorator around `IEmailSender` keyed off an optional event tag on `EmailMessage`, instead of a check at every call site. Missing preference rows mean enabled, so existing behaviour is unchanged. Per-event only (no per-job or per-team overrides). |
| 18 | Owner completion email | Added so `JobCompleted` has something to mute. Skipped when the job's result email already goes to the owner, to avoid two emails for one completion. |
| 19 | PDF library | QuestPDF under its Community license (free for non-commercial and small-business use, not MIT) rather than a hand-rolled PDF writer; fine for a POC, revisit the license before any commercial use. |
| 20 | Gmail verification | An admin-only `POST /admin/email/test` that mails only the calling admin's own address and bypasses the sent-email record, so it is repeatable and cannot be used to mail anyone else. |

## Phase 8

Built outside the PRD section 7 MVP slice, at the owner's request (the stub finance data could not demonstrate real reconciliation). Sample data: `docs/samples/`.

| # | Question | Decision |
|---|---|---|
| 21 | Where reconciliation rules live | Per job, in `ReconciliationRules`, not in the global admin-only `CalculationRules`. Business users must define their own rules, and a rule refers to that job's file aliases and columns. The existing `IRule`/finance flow is untouched. |
| 22 | Rule shape | A record-by-record check: join key in each file, a left and a right arithmetic expression, an operator and a tolerance. It covers "col1 - col3 of file 1 equals col2 of file 2". Aggregates, conditions and text/date comparison are not supported yet and would be new rule types. |
| 23 | Expression language | A hand-written parser over `alias.Column` references, numbers and `+ - * /`. No Roslyn, scripting or SQL: rules are data and cannot execute code. Money uses `decimal`. |
| 24 | Matching and unmatched records | Join on a chosen key (trimmed, case-insensitive). Unmatched records on either side, duplicate keys and blank keys are reported as failures with a reason, never silently dropped or picked arbitrarily. |
| 25 | Bad data | A non-numeric or empty cell, or a divide-by-zero, fails only that record. A rule that no longer fits its file (for example a replaced file lost a column) fails the run loudly instead of producing a misleading result. |
| 26 | File format and limits | CSV only (comma, semicolon, tab or pipe detected from the header), 5 MB and 50,000 rows per file, 5 files per job. Numbers accept thousands separators (`1,234.50`, `1,23,456.00`); currency symbols and `(12.00)` negatives are rejected. Excel is a possible follow-up. |
| 27 | Storage | File bytes live in Postgres (`InputFileContents`, separate from the metadata table so listings never load them); behind `IReconciliationStore`, so object storage can replace it. Per-record results are rows, not JSON, so they can be paged, filtered and exported. |
| 28 | Idempotency | The download step pins file ids and SHA-256 hashes; the calculate step reads exactly those and refuses a changed file. Results are replaced per run in one transaction. |
| 29 | Replacing files | The old file is superseded, not deleted, and the alias is reused. The new file must still contain every column the rules use. There is deliberately no unique index on (job, alias): the replacement takes the alias in the same save that supersedes the old row, and Postgres checks uniqueness row by row; the services guarantee one active file per alias. |
| 30 | Manual jobs | Unchanged: a Manual job runs once. Reconciling new files means a new job, or a Recurrent / Trigger-based job whose files are replaced between runs. Making Manual file jobs re-runnable is an open question. |
| 31 | PDF robustness | PDF export no longer throws when text contains characters the font lacks (user-uploaded keys can hold any script); a placeholder glyph is drawn. The CSV keeps the exact text. Applied to the run-history PDF too. |
| 32 | Web tests | The pure rule-builder logic is separate from React and tested with Node's built-in test runner (`npm test`), avoiding a new test framework for the POC. |

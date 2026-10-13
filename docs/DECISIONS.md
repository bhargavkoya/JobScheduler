# Decisions Log

ADR-style log of non-obvious choices made during the build, beyond what's already spelled out in `docs/PRD.md`. Appended to at the end of every phase. See [`ARCHITECTURE.md`](ARCHITECTURE.md) for how these land in the actual code.

Entries 1 to 13 were written before the code existed. Where the build went a different way, the entry keeps its original text and gets a **Status** note saying what actually happened. Entries 33 onward record choices that were made in code but never written down, added when the docs were reviewed (2026-10-13).

## From the approved implementation plan (2026-10-03)

| # | Question | Decision |
|---|---|---|
| 1 | Quartz persistence | RAMJobStore (in-memory) + DB-rehydration-on-boot, rather than Quartz's own ADO.NET job store — our Postgres `Job`/`JobRun` tables are the source of truth, so a second persistence layer inside Quartz would be redundant for a POC. |
| 2 | Rule engine library | NCalc (small, MIT-licensed expression evaluator) rather than hand-rolling an expression parser for the pluggable calculation engine. **Status: not adopted.** NCalc is not a dependency. The finance rules engine is pluggable through `IRule` types (SumAmount, Threshold, Variance) registered in DI, with each rule's parameters stored as data. The file-reconciliation expressions (#23) use a small hand-written parser. |
| 3 | Team modeling | `Team` is a real, extensible table (not a fixed Business/Technical enum) — PRD §5 implies teams double as the multi-tenancy unit, so the model needs to support more than two fixed values. **Status: not built as written.** `Team` is a fixed enum (Business, Technical). A user has one primary team plus observer teams (`UserTeamAccess` rows); see #33. Supporting more than two teams or tenants would mean replacing the enum with a table. |
| 4 | Claims storage | A lightweight custom `UserClaim` table instead of full ASP.NET Core Identity, consistent with "no SSO yet" and POC scope. |
| 5 | Follow-up cadence | PRD specifies 24h; made configurable per job template so a live demo can shorten it (e.g. to 1 minute) rather than hardcoding 24h. **Status: built per job, not per template.** The approval template has an optional `followUpAfterMinutes` field filled in when the job is created; empty means no reminder, and nothing defaults to 24h. One reminder is sent (see #40). |
| 6 | Audit export (PRD §4.8) | CSV only for the POC; PDF generation deferred to the Phase 10 stretch list given its low demo value relative to the added dependency. **Status: superseded by #19** (PDF was built in Phase 7). |
| 7 | Notification-preference enforcement (PRD §4.6) | Treated as Phase 10 stretch — PRD mentions it but §7's MVP slice doesn't call it out explicitly. **Status: built in Phase 7** (#17). |
| 8 | Trigger-based webhook auth | A simple per-job shared-secret header, rather than a full signature-verification scheme, given POC scope. **Status: built in Phase 7** (#14). |

## Phase 0

| # | Question | Decision |
|---|---|---|
| 9 | PostgreSQL host port | Mapped to **5433**, not the default 5432 — the dev machine already has a native PostgreSQL install bound to 5432, and remapping the container avoids touching that pre-existing, unrelated install. |
| 10 | API project style | ASP.NET Core Web API scaffolded with MVC controllers (`--use-controllers true`), not minimal APIs — matches `CLAUDE.md`'s "keep controllers thin; logic lives in Application services." |
| 11 | DbContext seed in Phase 0 | A trivial, non-domain `SystemInfo` marker table/row — added only to prove the EF Core migration pipeline works before any real domain entities exist (Phase 1). Will be deleted once real entities land. **Status: still present.** The marker table and its row were never removed; it is harmless and unused. |
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

## Phases 1 to 6, recorded retroactively

These were decided while building Phases 1 to 6 and are visible in the code, but were not in this log. Each was checked against the code when it was written down here.

| # | Question | Decision |
|---|---|---|
| 33 | Team and permission model | Role (Employee or Admin), one primary team, and optional observer teams (`UserTeamAccess`). Fine-grained actions are permission strings in a custom `UserClaim` table (`jobs.approve`, `jobs.retry`, `jobs.view.otherTeams`); admins implicitly hold all of them. A job is visible to its own team and the user's observer teams, or to everyone for an admin or a holder of `jobs.view.otherTeams`. A job you may not see answers 404, exactly like a missing one, so ids cannot be probed. |
| 34 | Registration, passwords and tokens | Self-registration always creates an Employee on the Business team with no permissions; only an admin changes team, role or permissions. Passwords: PBKDF2-SHA256, 100,000 iterations, per-password salt. Tokens: JWT (HS256), 60 minutes, carrying id, e-mail, role, team, observer teams and permissions. Login gives the same error for an unknown e-mail and a wrong password. Outside Development the signing key must be configured (32+ characters); in Development a random key is generated per start, so every API restart logs users out. |
| 35 | Run idempotency keys | Every run has a unique key built by its trigger: `{job}:manual:{Idempotency-Key}`, `:fixed:{run time}`, `:recurrent:{fire time}`, `:chain:{upstream run}`, `:trigger:{Idempotency-Key}`. A call without a key gets a random one, so it is never deduplicated. The key is looked up **before** the job's readiness is checked, so repeating a trigger returns the existing run even after the job has completed. |
| 36 | Which jobs can run again | Manual, Fixed and Event-based jobs run once. Recurrent and Trigger-based jobs are ready again after a Completed run (the next trigger starts a new run). A repeating job whose last run Failed needs a Retry first, so triggers cannot pile runs on a broken job; a recurrent firing that finds the job busy or Failed is skipped and logged. |
| 37 | In-memory scheduler and queue, and what a restart loses | Quartz (RAM store) and the Channels queue hold no durable state; `StartupRecovery` rebuilds from the database: Fixed jobs get a new trigger (a time that already passed fires immediately), Recurrent jobs get a new cron trigger, approval reminders are re-scheduled, and Pending or Running runs are re-queued. **A Recurrent firing missed while the API is down is not replayed**: the rebuilt trigger starts from "now" (verified with a probe: after a fresh start the next fire time is the next future occurrence and there is no previous fire). The Quartz misfire setting only covers a firing delayed while the process is up. This is the main gap against the PRD's "missed runs must be visible". |
| 38 | Retry semantics | Auto-retry waits `BackoffSeconds * 2^(n-1)`, capped at 3,600 s, up to the job's retry count. A manual Retry (needs `jobs.retry` or admin, job must be Failed) resumes the **same run**: it resets the auto-retry counter and re-queues it, and steps that already succeeded are skipped, so nothing is re-downloaded, recalculated or re-sent. |
| 39 | A failing rule versus a failing step | A rule that does not pass is a reported result and the job still completes (finance rules and file reconciliation alike). A step fails only for real errors: a rule name that does not exist or is inactive, a finance app error, a changed or missing input file, a rule that no longer fits its file. |
| 40 | Approval rules | The approver is fixed at job creation (a registered user holding `jobs.approve`, or an admin). Only the named approver or an admin can decide, only while the job is Needs manual action, and only once (a second attempt is 409). A reject needs a comment and marks the job Failed so the normal Retry re-submits it. Approve completes the job and starts event-based dependents. Either decision cancels the pending reminder. The reminder is one e-mail to the approver, sent only if the job is still waiting when it falls due. |
| 41 | Who is e-mailed | The owner is told of a terminal failure (once per failed attempt, so a manual retry that fails again notifies again) and of a completion (skipped when the result e-mail already went to the owner). The approver gets the approval request and the reminder. Failure notices go to the owner only, not to the team. Every e-mail carries an event tag so per-user preferences can mute it (#17). |
| 42 | At-risk flag | Computed when a job is read, never stored, so it cannot go stale. A job is at risk when a Fixed run time passed more than a minute ago without the job starting, or when it has been In progress or Needs manual action for longer than `AtRisk:ThresholdMinutes` (default 15). A missed Recurrent firing is not covered (#37). |
| 43 | The finance app in the POC | A mock inside the API (`/mock-finance`), reached over real HTTP through `IFinanceAppClient` so the seam is genuine. It answers only in Development, needs no login, returns the same three rows every time (total 1048.75), and keeps its call counters in memory. `FinanceStub:FailFirstAttempts` makes the first N calls per idempotency key answer 503 so retries can be demonstrated. |
| 44 | Errors and time | One middleware maps application exceptions to ProblemDetails: validation 400, bad credentials 401, forbidden 403, not found 404, conflict 409. Everything is stored in UTC; scheduling input and all display are IST, converted at the edges. |

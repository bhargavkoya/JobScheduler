# Build Plan

What was built, phase by phase. Each phase was a branch and a PR to `main`, and is independently demoable. See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the current-state design and [`DECISIONS.md`](DECISIONS.md) for the reasoning behind specific choices. The phase numbering follows what actually shipped; it replaces the original ten-phase outline from Phase 0.

| Phase | Scope | Demo criterion | Status |
|---|---|---|---|
| 0 | Solution and projects, docker-compose Postgres, EF Core, health endpoint, Swagger, Vite + TS + Tailwind placeholder | `dotnet build` and `npm run dev` work; placeholder page shows API health | **Done** |
| 1 | Auth (email + password, JWT), roles, Business/Technical teams, claims and policies, web login | Log in, receive a JWT, call a protected endpoint; claims gate fine-grained actions | **Done** (PR #1) |
| 2 | Job templates (admin-approved catalog) and jobs (create, list, cancel), seeded demo data, web catalog and create form | Create a Fixed and a Manual job from a template and see them in the dashboard | **Done** (PR #2) |
| 3 | Execution engine: Quartz (in-memory) + `StartupRecovery`, Channels queue and worker, `JobRun` / `JobRunStep`, idempotency keys, auto-retry with exponential backoff, manual retry, finance REST mock | A job runs download -> calculate -> email through the queue; a failure retries and a manual Retry resumes without double effects | **Done** (PR #3) |
| 4 | Rules-based calculation engine (rules stored as data, admin rules API) and real Gmail via MailKit with idempotent sending and owner failure emails | Rules change a calculation outcome without a code change; a retried run does not send twice | **Done** (PR #4); live Gmail send needs your app password (see README) |
| 5 | Single-approver manual-action workflow, manual queue, at-risk flag, follow-up chaser, dashboard summary and filters | An approval job parks in Needs manual action; the named approver decides once | **Done** (PR #5) |
| 6 | Recurrent schedules (IST cron), event-chained jobs, CSV export of run history, demo seed chain, `DEMO.md` | Completing one job starts its dependent; a daily job fires by itself | **Done** (PR #6) |
| 7 | Trigger-based (webhook) jobs, per-user notification preferences with an owner completion email, PDF export, Gmail test endpoint, docs refresh | `curl` a webhook to fire a job twice with the same key and get one run; mute an event and see the email skipped; download a PDF | **Done** — see note below |

| 8 | File reconciliation (requested after Phase 7): upload CSVs, per-job record-by-record rules over their columns, per-record pass/fail results, CSV/PDF export, replace-file, web UI | Upload the sample files, define rules, run, see every record's result and export it; a different set of files with different columns works the same way | **Done** - see note below |

## Phase 7 note

- **Webhook trigger**: `POST /webhooks/jobs/{id}` with `X-Webhook-Token`. The token is returned once at create time and only its SHA-256 is stored. Without an `Idempotency-Key` header every call is a new run; with one, a replay returns the same run.
- **Notification preferences**: four events (`JobCompleted`, `JobFailed`, `ApprovalRequested`, `FollowUpReminder`), default on, enforced in one decorator around `IEmailSender`.
- **PDF export**: `?format=pdf` on the existing export endpoint, using QuestPDF under its Community license.
- Tests were added after the code (`tests/JobScheduler.Tests/Runs/Phase7Tests.cs`): webhook token and trigger service, triggered-run orchestration, preference enforcement and service, event tagging, owner completion notice, CSV/PDF exporters and the export service. The suite is 312 tests.

## Phase 8 note

- Backend: `CsvTable`, `Expression`, `RuleEvaluator`, `ReconciliationSetupService`, `ReconciliationRunner` (wired into the existing download and calculate steps), EF migration `Phase8FileReconciliation`, API (`/job-files`, `/jobs/{id}/reconciliation`, `/runs/{id}/reconciliation/*`).
- Frontend: upload, rule builder, results view, files-and-rules panel.
- Sample data and expected results: [`samples/README.md`](samples/README.md). The tests run the documented rules over those files and assert the README's lists, so the two cannot drift.
- Verification: unit tests (xUnit + Moq), opt-in Postgres tests (`JOBSCHEDULER_TEST_PG`), web logic tests (`npm test`), a scripted HTTP walkthrough and a scripted Chrome walkthrough, both against a throwaway database.

## PRD coverage

Checked against the code on 2026-10-13. **Done** = built and exercised; **Partial** = built with a gap that is named; **Not built** = absent. Section numbers refer to [`PRD.md`](PRD.md).

| PRD item | Status | Notes |
|---|---|---|
| §3 Auto-triggered notifications | Partial | Job completed, job failed, approval requested and approval reminder are sent. "Report ready" is covered by the webhook trigger starting a job, not by a separate notification. |
| §3 Trigger and download reports (REST) | Done | `IFinanceAppClient` over HTTP; the finance app is a stub in the POC. |
| §3 / §4.5 Configurable calculation rules | Done | Finance rules are data and pluggable (`IRule`); file reconciliation rules are data per job. |
| §3 / §4.6 Result e-mails (Gmail) | Done | Real MailKit path; delivery not yet verified with a real account (see below). |
| §3 / §4.6 Follow-up e-mails, "remind again in 24h" | Partial | The approval reminder works (`followUpAfterMinutes`, one e-mail). The Follow-up Chaser template's message and "remind again" fields are saved but unused. |
| §4.1 Auth, teams, claims | Done | E-mail and password, JWT, roles, primary and observer teams, permission claims. SSO (stretch) not built. |
| §4.2 Dashboard: list, filters, detail | Done | Status, team, type, date range, owner ("My jobs"), at-risk. Detail shows definition, schedule, run history, step output, retry count, state, owner and team. |
| §4.2 "Linked reports / e-mails" in job detail | Partial | Step output is shown; sent e-mails are recorded in `SentEmails` but not shown in the UI. |
| §4.2 Distinct action buttons by state | Partial | Run now, Approve, Reject, Retry and Cancel appear only when valid. A "Provide input" action is not built. |
| §4.2 Manual action queue view | Done | Separate tab with a count badge. |
| §4.3 Five schedule types, template catalog, IST | Done | Fixed, Recurrent, Event-based (job chaining), Trigger-based (webhook), Manual; admin-approved templates; IST only. |
| §4.4 Failure handling | Partial | Failed jobs are flagged and the owner is e-mailed; auto-retry with backoff, manual Retry, failed step and error shown. The team is not notified, only the owner. |
| §4.7 Single approver | Done | |
| §4.8 CSV / PDF export of run history | Done | Plus record-level exports for file reconciliation. |
| §5 Reliability: scheduled jobs must not silently drop | Partial | Fixed jobs survive a restart (a past time fires at once). A Recurrent firing missed while the API is down is **not** replayed or reported. |
| §5 At-risk flagging | Partial | Late Fixed jobs, runs and approvals taking too long. Not a missed Recurrent firing. |
| §5 Auditability | Partial | Each run keeps who triggered it, every step's output and attempt, and approvals are recorded. Sent e-mails are stored but not visible in the UI. |
| §5 Extensibility by configuration | Done | New rules, templates (admin API) and per-job reconciliation rules need no code change. |
| §5 Idempotency | Done | See `ARCHITECTURE.md` section 7. |
| §5 Multi-tenancy by team and claims | Done (basic) | Team scoping and observer teams; no separate tenants. |
| §2 Technical team sees job internals | Not built | Not differentiated: anyone who can see a job sees its run history, retry settings and errors. |

## Still open

- Live-verify Gmail delivery with a real app password (use `POST /admin/email/test`).
- A manual run-through against your own Postgres of the Phase 7 and Phase 8 migrations (Phase 8 was verified against a throwaway database; migrations apply automatically when the API starts in Development).
- Recurrent firings missed while the API is down are skipped and invisible (see PRD coverage). Options: record a "missed" run when start-up finds one, or persist Quartz.
- Follow-up Chaser template: its **Message** and **Remind again after (hours)** fields are saved but not used (the assignee gets the generic "completed" email). Decide whether to wire them in (message in the email body, one re-send after N hours if nothing has happened).
- Swagger has no bearer-token setup, so protected endpoints cannot be called from it; adding a security definition would fix that.
- File reconciliation limits to decide on later: text/date comparisons, aggregate rules, Excel input, re-running a Manual job after replacing its files.
- Out of scope by design: business-day calendars, SSO, multi-approver sign-off, retention policy.

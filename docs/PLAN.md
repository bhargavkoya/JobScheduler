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

## Phase 7 note

- **Webhook trigger**: `POST /webhooks/jobs/{id}` with `X-Webhook-Token`. The token is returned once at create time and only its SHA-256 is stored. Without an `Idempotency-Key` header every call is a new run; with one, a replay returns the same run.
- **Notification preferences**: four events (`JobCompleted`, `JobFailed`, `ApprovalRequested`, `FollowUpReminder`), default on, enforced in one decorator around `IEmailSender`.
- **PDF export**: `?format=pdf` on the existing export endpoint, using QuestPDF under its Community license.
- Tests were added after the code (`tests/JobScheduler.Tests/Runs/Phase7Tests.cs`): webhook token and trigger service, triggered-run orchestration, preference enforcement and service, event tagging, owner completion notice, CSV/PDF exporters and the export service. The suite is 312 tests.

## Still open

- Live-verify Gmail delivery with a real app password (use `POST /admin/email/test`).
- A manual run-through against Postgres (the tests mock the database, so the two new migrations are untested).
- Out of scope by design: business-day calendars, SSO, multi-approver sign-off, retention policy.

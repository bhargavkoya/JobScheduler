# PRD: Job Scheduler Application (P1 POC)

**Author:** Bhargava Koya
**Status:** Final (v2)
**Date:** 26 Sept 2026

**Scope note:** This is a base-level interview POC, not a production system — the goal is a demoable skeleton that supports a fluent conversation about scheduling, job orchestration, retries and reconciliation workflows, not full banking-grade hardening.

## 1. Overview

Business teams in banking capital allocation and investment operations run daily sets of processes that are largely manual and repetitive. This application automates the automatable subset of that work — triggering downloads, running calculations, sending notifications and reconciliation emails, and chasing follow-ups — while giving operations staff a dashboard to schedule, monitor, and intervene on jobs.

### Goals

- Save manual hours and reduce operational/reputational risk from missed or late manual steps.
- Give business and technical teams visibility into what ran, what's running, what's due, and what needs a human.
- Support multiple scheduling styles (fixed, recurring, event-driven, trigger-based, manual) for the variety of processes involved.

### Non-goals (for the POC)

- Real integration with actual internal finance systems or a real Outlook tenant (POC uses Gmail for email send/receive and mocked/stubbed finance-app connectors — code demonstrates the integration seam, not a live production connection).
- Business-day/holiday-calendar-aware scheduling (out of scope — see §4.3).
- Production-grade security, multi-region deployment, or full audit/compliance certification.

## 2. Users & Roles

| Role | Description |
|---|---|
| Employee (Business team) | Registers/logs in, gets assigned to a team, schedules and monitors jobs relevant to their process, takes manual actions when required. |
| Employee (Technical team) | Same base access, plus visibility into job internals (logs, retry configuration, error detail) for troubleshooting. |
| Admin | Manages team assignments, approves job templates, has visibility across all teams. |

Access is role-based + team-based + claim-based: a user's team (Business/Technical) and role (Employee/Admin) determine broad access, while fine-grained claims control specific actions (e.g., who can approve a manual-action job, who can view another team's jobs). This same claims model is what supports multiple business units sharing one app instance (see §5, Multi-tenancy).

## 3. Core Use Cases (from source requirements)

- Auto-triggered notifications — send email on specific events (e.g., job completed, job failed, report ready).
- Triggering report downloads — kick off report generation/download on internal finance apps (REST APIs).
- Downloading triggered reports — pull the completed report once the internal app signals it's ready.
- Financial calculations — run configurable calculation rules as part of a process (e.g., reconciliation math).
- Sending result emails — email downloaded reports or reconciliation output to stakeholders.
- Follow-up emails — chase a business team/employee who owes an action on a process.

## 4. Functional Requirements

### 4.1 Auth & Team Assignment

- Register/login for employees (email + password to start; SSO is a stretch goal).
- Each employee is assigned to a primary team: Business or Technical, plus optional secondary/observer access to other teams.
- Access is governed by team + role + claims (see §2) — this is also the mechanism that lets multiple business units use the same app instance in isolation from each other (assign units as teams/claim scopes rather than separate deployments).

### 4.2 Job Dashboard

- List view of jobs with status: Scheduled / In Progress / Completed / Cancelled / Failed / Needs Manual Action.
- Filters by status, team, job type, date range, owner.
- Manual-action jobs surface a distinct action button (e.g., "Approve", "Retry", "Provide input") that's only active when the job is actually in that state.
- Manual action queue view — a dedicated view (separate from the general dashboard filter) listing every job currently waiting on a human, since this is a first-class need.
- Job detail view on click: definition, schedule type, run history, logs/output, linked reports/emails, retry count, current state, owner/team.

### 4.3 Scheduling

When adding a job, the type determines the form shown:

- **Fixed** — one-off date/time.
- **Recurrent** — cron-style or friendly recurrence (daily/weekly/monthly + custom).
- **Event-based** — triggered by an internal event (e.g., "upstream report ready"), including job-to-job dependency chaining (a job's trigger can be "job X completed") — several of the six use cases are naturally sequential (download → calculate → email → follow-up).
- **Trigger-based** — triggered by an external signal/webhook.
- **Manual kickoff** — sits ready, run on demand by a user.

Each job type has its own config (target system/report, calculation rule(s) to run, notification recipients, retry policy).

Job templates/catalog — jobs are created from a predefined list of job "types" with their required fields, rather than a fully free-form creator, so business users can't misconfigure a job. Admins approve/manage the template catalog.

Timezone: single timezone (IST) for all scheduling. Business-day/holiday-calendar-aware scheduling is explicitly out of scope for this POC.

### 4.4 Failure Handling

- Failed jobs are flagged and notify the owner/team (dashboard + email).
- Retry button on failed jobs; configurable auto-retry count/backoff before requiring manual retry.
- Failure detail (error message, step that failed) visible in job detail view.

### 4.5 Financial Calculations

Calculation logic is user-configurable, rules-based — not a fixed set of hardcoded formulas. The calculation engine is pluggable: new rules can be authored/attached to a job type without a code change to the core scheduler.

### 4.6 Notifications & Reconciliation

- Email integration (via Gmail, for a working code path without needing a real Outlook tenant) for: event notifications, report delivery, reconciliation results, follow-up chasers.
- Follow-up emails are themselves schedulable/trackable (e.g., "remind again in 24h if no action").
- Notification preferences per user — who gets notified on what event, to avoid inbox overload once multiple job types are running.

### 4.7 Manual Action / Approval Workflow

Manual-action jobs (e.g., "Approve") require a single approver — no multi-person sign-off in this POC.

### 4.8 Audit & Reporting

Audit/history export — CSV/PDF export of a job's run history.

## 5. Non-Functional Requirements

- **Reliability:** scheduled jobs must not silently drop; missed/late runs should be visible and alertable.
- **SLA/at-risk flagging:** a job is flagged "at risk" if it hasn't started/finished by its expected time, even before it hits hard failure.
- **Auditability:** every job run keeps a history (who scheduled it, when it ran, what it did, what it sent) — important given the banking context, even at POC scale.
- **Extensibility:** new job types (new report source, new calculation rule) should be addable via configuration/rule authoring, not a code change, where feasible.
- **Idempotency:** re-running a job (e.g., after retry) shouldn't double-send emails or double-count a calculation.
- **Data retention:** no strict retention policy for the POC — job histories, reports, and sent-email records are kept indefinitely (long enough for demo/prototype purposes); a real retention policy is a production concern, not a POC one.
- **Multi-tenancy:** single application instance shared across business units, isolated via role/team/claim-based access rather than separate deployments (see §2, §4.1).

## 6. Tech Stack (as specified)

| Layer | Choice |
|---|---|
| Frontend | React, Tailwind CSS |
| Backend | ASP.NET Core Web API, Quartz.NET, background workers |
| Database | PostgreSQL |
| Messaging | Queue for async processing (e.g., report download → calculation → email as queued steps) |
| Email | Gmail (API/SMTP) — used in place of Outlook so the integration is real, working code without needing a real Outlook tenant or live testing |

POC-scoping approach: internal finance apps are called as REST APIs — mock/stub these behind an interface (e.g., a fake "internal finance app" API) so the demo runs standalone without real credentials, while the code still shows the real integration seam. Gmail integration can be genuinely wired up (not mocked), since it doesn't require enterprise tenant setup.

## 7. Suggested MVP Slice for the POC

1. Login/register + team assignment with role/claim-based access.
2. Job templates/catalog with at least two job types: Fixed and Manual kickoff (add Recurrent and one event-chained job if time allows).
3. Quartz.NET-backed scheduler runs the job (stubbed "download → calculate → email" pipeline through a queue), with the finance-app call as a REST stub and the calculation step reading a simple user-defined rule.
4. Dashboard with status filters, job detail view, and a separate manual-action queue view.
5. Failure + retry path on at least one job type.
6. One end-to-end demo: schedule a job → it runs → mock report "downloaded" via REST stub → rules-based calculation → email sent via Gmail → dashboard reflects Completed → a chained follow-up job fires if no action is taken.

## 8. Decisions Log

| # | Question | Decision |
|---|---|---|
| 1 | Outlook integration method | Not required to be real-time/tested against a live tenant — use Gmail instead so the code path is genuinely working. |
| 2 | Internal finance app integration | REST APIs. |
| 3 | Financial calculation logic | User-configurable, rules-based — pluggable calculation engine. |
| 4 | Recurrence timezone handling | Out of scope — business-day/holiday calendars not modeled; use IST only. |
| 5 | Data retention | No fixed policy needed — retain long enough for prototype/demo purposes. |
| 6 | Multi-tenancy | Single app instance, isolated via role + team + claim-based access, not separate deployments. |
| 7 | Approval workflow | Single approver — no multi-person sign-off. |

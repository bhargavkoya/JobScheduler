# Architecture

Current-state architecture, updated at the end of each phase to reflect what is actually built. See [`PLAN.md`](PLAN.md) for the phase history and [`DECISIONS.md`](DECISIONS.md) for why things are shaped this way.

## Solution layout

```
src/
  JobScheduler.Api/             controllers (thin), Program.cs composition root, exception middleware, Swagger, CORS
  JobScheduler.Application/     services, DTOs and the interfaces for every external seam
  JobScheduler.Domain/          entities and enums (Job, JobRun, JobTemplate, User, ...)
  JobScheduler.Infrastructure/  EF Core + migrations, Quartz, Channels queue, MailKit, QuestPDF, HTTP finance client
tests/JobScheduler.Tests/       xUnit + Moq
web/                            React + Vite + TypeScript + Tailwind
```

Dependency direction: `Api -> Application, Infrastructure` · `Infrastructure -> Application, Domain` · `Application -> Domain`.

## How a run flows

```
trigger ──> RunOrchestrator ──> JobRun (unique idempotency key) ──> IJobQueue ──> JobRunWorker ──> JobRunner
                                                                                                     │
                      DownloadReport (IFinanceAppClient) -> Calculate (rules engine) -> SendEmail (IEmailSender)
                                                                                                     │
                                  success: Completed (or NeedsManualAction) -> chain dependents, notify owner
                                  failure: auto-retry with backoff -> Failed -> notify owner
```

Triggers, all ending in `RunOrchestrator`, each with its own idempotency key:

| Schedule type | Trigger | Key |
|---|---|---|
| Fixed / Recurrent | Quartz (in-memory), rebuilt from the database by `StartupRecovery` | job + scheduled time |
| Manual | `POST /jobs/{id}/run` | job + `Idempotency-Key` header |
| EventBased | `IJobChainer` when the upstream run completes | job + upstream run id |
| TriggerBased | `POST /webhooks/jobs/{id}` | job + `Idempotency-Key` header |

## Idempotency

- `JobRun.IdempotencyKey` is unique, so the same trigger can never create two runs.
- A retry resumes the **same** run: steps that already succeeded are skipped, so nothing is re-downloaded, re-calculated or re-sent.
- Every email has an idempotency key recorded in `SentEmails` (`IdempotentEmailSender`). The send happens before the record, so there is a narrow at-least-once window if the process dies in between.

## Email

`IEmailSender` is layered: `PreferenceAwareEmailSender` (skips events a recipient muted) wraps `IdempotentEmailSender` (dedupe) over `SmtpEmailTransport` (Gmail via MailKit). With no credentials configured the inner sender is `LoggingEmailSender`, which only logs. Emails carry an optional `NotificationEvent` tag; untagged mail always goes out. Recipients who are not registered users have no preferences and always receive mail.

## Access control

JWT with a role (Employee/Admin), a primary team (Business/Technical), optional observer teams, and permission claims (`jobs.retry`, `jobs.approve`, view-other-teams). Job visibility is by team. The webhook endpoint is the only anonymous mutating endpoint: its credential is a per-job secret.

## Persistence

PostgreSQL via EF Core migrations (`Infrastructure/Persistence/Migrations`). The database is the source of truth; Quartz uses its in-memory store and is rebuilt on startup. Key tables: `Jobs`, `JobRuns`, `JobRunSteps`, `JobTemplates`, `CalculationRules`, `JobApprovals`, `SentEmails`, `NotificationPreferences`, `Users`, `UserClaims`, `UserTeamAccess`.

## Export

`IRunHistoryExporter` implementations are selected by `?format=` on `GET /jobs/{id}/runs/export`: `csv` (default, formula-safe cells) and `pdf` (QuestPDF, A4 landscape, with a job header). Times are IST in both.

## Frontend

Tabs: Jobs (filters, detail, run history, approvals, export buttons), Manual actions (queue with a count badge), Catalog, Notifications (preferences), My access. Webhook jobs show their token once after creation.

## Known limits (by design for the POC)

Single timezone (IST), no holiday calendars, single approver, in-memory queue (a restart re-queues unfinished runs from the database), no retention policy.

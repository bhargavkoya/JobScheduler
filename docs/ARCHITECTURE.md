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

## File reconciliation (Phase 8)

Templates flagged `IsFileReconciliation` (seeded: "File Reconciliation") replace the finance report and the rules engine with user-uploaded CSV files and per-job, record-by-record rules. Nothing about the files or columns is known to the code.

```
upload (POST /job-files) --> CsvTable.Parse --> InputFile + InputFileContent (headers, row count, SHA-256)
create job --> ReconciliationSetupService validates files + rules against the real headers, attaches them (one save)
run:  DownloadReport step = ReconciliationRunner.SnapshotInputsAsync  (pins file ids + hashes as the step output)
      Calculate step      = ReconciliationRunner.RunAsync             (parse pinned files, RuleEvaluator per rule,
                                                                       ReplaceResultsAsync: delete + insert in one transaction)
      SendEmail step      = unchanged (summary: "52 of 62 records passed ...")
read: GET /runs/{id}/reconciliation/summary | results | export?format=csv|pdf (&status= &ruleId=)
```

- **Rule** (`ReconciliationRule`, stored as data per job): left file + right file (aliases), a join key column in each, a left and a right arithmetic expression, an operator (`== != < <= > >=`) and a tolerance (absolute or percent). Both files the same means a within-file check with no join.
- **Expressions** are parsed by a small hand-written parser (`Expression`): numbers, `alias.Column` or `alias.[Column Name]`, `+ - * /`, parentheses, unary minus. Decimal arithmetic; nothing is ever compiled or executed as code. A bad cell, an empty cell or a divide-by-zero fails that one record with a reason; it never fails the run.
- **Matching** (`RuleEvaluator`): keys are trimmed and compared case-insensitively. Per record: pass, fail with the numbers and difference, missing in the other file, duplicate key (either side), blank key. Right-file records nobody matched are reported as "missing in the left file".
- **Idempotency**: the download step records each file's id and SHA-256, and the calculate step reads exactly those bytes (a changed file is refused). Results are replaced per run in one transaction, so a retried run ends with one set of results. The completion email is keyed per run as before.
- **Replacing a file** supersedes the old row (kept for audit) and reuses the alias; the new file must still contain every column the rules read.
- **Exports**: `IReconciliationExporter` (`csv`: formula-safe text, plain numeric cells; `pdf`: summary + records, failures first, capped at 2,000 records with a note). Both honour the status and rule filters.
- A failing record does not fail the job: like the finance rules engine, a rule that does not pass is a reported result.

## Idempotency
- `JobRun.IdempotencyKey` is unique, so the same trigger can never create two runs.
- A retry resumes the **same** run: steps that already succeeded are skipped, so nothing is re-downloaded, re-calculated or re-sent.
- Every email has an idempotency key recorded in `SentEmails` (`IdempotentEmailSender`). The send happens before the record, so there is a narrow at-least-once window if the process dies in between.

## Email

`IEmailSender` is layered: `PreferenceAwareEmailSender` (skips events a recipient muted) wraps `IdempotentEmailSender` (dedupe) over `SmtpEmailTransport` (Gmail via MailKit). With no credentials configured the inner sender is `LoggingEmailSender`, which only logs. Emails carry an optional `NotificationEvent` tag; untagged mail always goes out. Recipients who are not registered users have no preferences and always receive mail.

## Access control

JWT with a role (Employee/Admin), a primary team (Business/Technical), optional observer teams, and permission claims (`jobs.retry`, `jobs.approve`, view-other-teams). Job visibility is by team. The webhook endpoint is the only anonymous mutating endpoint: its credential is a per-job secret.

## Persistence

PostgreSQL via EF Core migrations (`Infrastructure/Persistence/Migrations`). The database is the source of truth; Quartz uses its in-memory store and is rebuilt on startup. Key tables: `Jobs`, `JobRuns`, `JobRunSteps`, `JobTemplates`, `CalculationRules`, `InputFiles` / `InputFileContents`, `ReconciliationRules`, `ReconciliationResults` (one row per record per rule per run), `JobApprovals`, `SentEmails`, `NotificationPreferences`, `Users`, `UserClaims`, `UserTeamAccess`.

## Export

Record-level reconciliation exports are described above. `IRunHistoryExporter` implementations are selected by `?format=` on `GET /jobs/{id}/runs/export`: `csv` (default, formula-safe cells) and `pdf` (QuestPDF, A4 landscape, with a job header). Times are IST in both.

## Frontend

Tabs: Jobs (filters, detail, run history, approvals, export buttons), Manual actions (queue with a count badge), Catalog, Notifications (preferences), My access. Webhook jobs show their token once after creation. File reconciliation jobs add an upload step with column preview, a rule builder whose dropdowns come from the uploaded headers (with instant expression checking), a files-and-rules panel with replace-file, and a record results view (summary per rule, Pass/Fail and rule filters, paging, CSV/PDF export of the filtered view). Pure rule-builder logic lives in `web/src/jobs/reconciliation.ts` and is tested with `npm test`.

## Known limits (by design for the POC)

File reconciliation compares numbers only (no text or date comparison, no aggregates such as a sum across a file), reads CSV only (no Excel), and caps files at 5 MB / 50,000 rows. A Manual job runs once, so reconciling new files needs a new job or a Recurrent / Trigger-based one. Single timezone (IST), no holiday calendars, single approver, in-memory queue (a restart re-queues unfinished runs from the database), no retention policy.

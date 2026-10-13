# Architecture

Current-state architecture, updated at the end of each phase to reflect what is actually built. See [`PLAN.md`](PLAN.md) for the phase history and PRD coverage, [`DECISIONS.md`](DECISIONS.md) for why things are shaped this way, and [`TESTING.md`](TESTING.md) for how to exercise each flow by hand.

Contents: [1 Solution layout](#1-solution-layout) · [2 System context](#2-system-context) · [3 API surface](#3-api-surface) · [4 Data model](#4-data-model) · [5 Lifecycles](#5-lifecycles) · [6 Workflows and data flows](#6-workflows-and-data-flows) · [7 Idempotency](#7-idempotency) · [8 Access control](#8-access-control) · [9 Frontend](#9-frontend) · [10 Known limits](#10-known-limits)

All diagrams are plain ASCII. In flow diagrams, `-->` is a call or data hand-off, `[x]` is a stored thing, and `(n)` marks steps in order.

## 1. Solution layout

```
src/
  JobScheduler.Api/             controllers (thin), Program.cs composition root, exception middleware, Swagger, CORS
  JobScheduler.Application/     services, DTOs and the interfaces for every external seam
  JobScheduler.Domain/          entities and enums (Job, JobRun, JobTemplate, User, ...)
  JobScheduler.Infrastructure/  EF Core + migrations, Quartz, Channels queue, MailKit, QuestPDF, HTTP finance client
tests/JobScheduler.Tests/       xUnit + Moq (database mocked; a few opt-in Postgres tests)
web/                            React + Vite + TypeScript + Tailwind
docs/                           PRD, this file, DECISIONS, PLAN, DEMO, TESTING, samples/
```

Dependency direction: `Api -> Application, Infrastructure` · `Infrastructure -> Application, Domain` · `Application -> Domain`. Every external seam is an interface in Application, implemented in Infrastructure: `IJobStore` and the other stores (EF), `IJobScheduler` (Quartz), `IJobQueue` (Channels), `IFinanceAppClient` (HTTP), `IEmailSender` / `IEmailTransport` (MailKit), `IRunHistoryExporter` and `IReconciliationExporter` (CSV and QuestPDF).

## 2. System context

```
  +-----------+   JSON + JWT bearer    +-------------------------------------------------------+
  |  Browser  | ---------------------> |  JobScheduler.Api  (one ASP.NET Core process)          |
  |  React +  | <--------------------- |                                                       |
  |  Vite     |   polls every 2-10 s   |  Controllers --> Application services --> stores/seams |
  +-----------+                        |                                                       |
                                       |  Hosted in the same process:                          |
  +-----------+   POST + token         |    Quartz scheduler  (in-memory trigger store)         |
  | External  | ---------------------> |    JobRunWorker      (drains the in-memory queue)      |
  | system    |   /webhooks/jobs/{id}  |    ExecutionStartup  (rebuilds triggers + queue)       |
  +-----------+                        |    /mock-finance     (stub finance app, Development)   |
                                       +--------+------------------+-------------------+-------+
                                                |                  |                   |
                                                v                  v                   v
                                        +--------------+   +---------------+   +----------------+
                                        |  PostgreSQL  |   |  Gmail SMTP   |   | Finance app    |
                                        |  source of   |   |  via MailKit, |   | REST. In the   |
                                        |  truth       |   |  or log-only  |   | POC = the stub |
                                        +--------------+   +---------------+   +----------------+
```

State that lives **only in memory** (and is rebuilt from the database on start-up, see 6.8): the Quartz triggers, the run queue and its retry delays, the mock finance call counters, and, when Gmail is not configured, the email dedupe set. Everything else is in PostgreSQL.

## 3. API surface

| Area | Endpoints | Who |
|---|---|---|
| Auth | `POST /auth/register`, `POST /auth/login`, `GET /auth/me` | register/login anonymous |
| Templates | `GET /templates` | any user (admins may list unapproved) |
| | `POST/PUT /admin/templates`, `POST /admin/templates/{id}/approve` | admin |
| Jobs | `POST /jobs`, `GET /jobs`, `GET /jobs/{id}`, `POST /jobs/{id}/cancel`, `GET /jobs/{id}/runs`, `GET /jobs/{id}/runs/export` | any user, team-scoped |
| | `POST /jobs/{id}/run` (header `Idempotency-Key`) | owner or admin; Manual jobs only |
| | `POST /jobs/{id}/retry` | `jobs.retry` or admin |
| Approvals | `GET /jobs/manual-queue`, `GET /jobs/{id}/approvals` | any user, team-scoped |
| | `POST /jobs/{id}/approve`, `POST /jobs/{id}/reject` | `jobs.approve` or admin, and the named approver (or admin) |
| Webhook | `POST /webhooks/jobs/{id}` (headers `X-Webhook-Token`, optional `Idempotency-Key`) | anonymous; the per-job token is the credential |
| File reconciliation | `POST /job-files`, `GET /jobs/{id}/reconciliation`, `PUT /jobs/{id}/reconciliation/files`, `GET /runs/{id}/reconciliation/summary`, `.../results`, `.../export` | any user, team-scoped; replace-file owner or admin |
| Preferences | `GET/PUT /me/notification-preferences` | the user |
| Admin | `/admin/rules` (GET, POST, PUT, `GET types`), `/admin/users` (GET, `PUT {id}/team`, `PUT {id}/claims`), `POST /admin/email/test` | admin |
| Other | `GET /health`; `/mock-finance/reports` (POST, `GET {id}`), Development only and anonymous | |

Errors are mapped in one middleware: validation 400, unauthenticated 401, forbidden 403, not found 404 (also used for jobs hidden by team), conflict 409.

## 4. Data model

```
 Users 1---* UserClaims                  (permission strings, e.g. jobs.approve)
   |   1---* UserTeamAccess              (observer teams)
   |   1---* NotificationPreferences     (one row per muted/unmuted event)
   |
   |  owner / approver
   +----------------------*  Jobs  *---------1  JobTemplates
                              |                 (fields as JSON, schedule types,
                              |                  RequiresApproval, IsFileReconciliation)
                              |
                              +--1---*  JobRuns  1---*  JobRunSteps        (output per step, attempt number)
                              |           |
                              |           +--1---*  ReconciliationResults  (one row per record per rule)
                              |                              *
                              |                              |
                              +--1---*  ReconciliationRules  1   (rule = data, per job)
                              +--1---*  InputFiles  1---1  InputFileContents   (bytes kept apart from metadata)
                              +--1---*  JobApprovals  (decision, comment, approver -> Users)

 CalculationRules   global named rules for the finance flow (referenced by name from a job's "rules" field)
 SentEmails         idempotency key (primary key), recipient, subject, sent time
 SystemInfo         one marker row from Phase 0 (kept)
```

Notes: `Jobs.TriggerJobId` (the upstream job of an event-based job) is a plain column, not a foreign key. A job's `ConfigJson` holds the values of its template's fields. Deleting a job cascades to its runs, steps, approvals, files, rules and results; users and templates are `Restrict`.

## 5. Lifecycles

### Job status

```
   create
     |
     v
 SCHEDULED ---------------------------- cancel ----------------------------> CANCELLED
     |
     |  a run starts: scheduler fires, Run now, webhook, upstream job completed
     v
 IN PROGRESS ---- all steps ok, no approval needed -------------------------> COMPLETED
     |   |
     |   +------- all steps ok, template needs approval --> NEEDS MANUAL ACTION
     |                                                        |            |
     |                                          approve ------+            +------ reject (comment required)
     |                                             |                                    |
     |                                             v                                    v
     |                                         COMPLETED                             FAILED
     |                                                                                  ^
     +-------- a step fails and no auto-retries are left ------------------------------+
                                                                                        |
 FAILED --- Retry (needs jobs.retry or admin; resumes the same run) --> IN PROGRESS ----+ (back to the top)

 Recurrent and Trigger-based jobs only:  COMPLETED --the next trigger starts a run--> IN PROGRESS
```

- Cancel is allowed from Scheduled, Needs manual action, and (Recurrent / Trigger-based only) Completed or Failed. A running job cannot be cancelled.
- **Manual, Fixed and Event-based jobs run once**: after Completed they cannot start again. Recurrent and Trigger-based jobs are ready again after a Completed run; if the last run Failed they need a Retry first.
- `StatusChangedAtUtc` is set on every change and drives the at-risk flag.

### Run status and steps

```
  PENDING --worker picks it up--> RUNNING --all steps ok--------------------------------> SUCCEEDED
     ^                               |
     |      step throws, auto-retries left: AutoRetriesUsed++, wait backoff, re-queued
     +-------------------------------+
                                     |  step throws, no retries left
                                     +--------------------------------------------------> FAILED

  Steps run in this fixed order, each saved as it finishes:  DownloadReport -> Calculate -> SendEmail
  A step already Succeeded is skipped on any later attempt (auto-retry, manual Retry, or restart recovery).
  Backoff before auto-retry n = BackoffSeconds * 2^(n-1), capped at 3600 s.
```

## 6. Workflows and data flows

### 6.1 A run, end to end

```
 trigger       RunOrchestrator              PostgreSQL           queue        JobRunWorker -> JobRunner
   |                  |                          |                 |                    |
   |--(1) key ------->|--(2) run with this key?->|                 |                    |
   |                  |<-- yes: return it, stop  |                 |                    |
   |                  |--(3) job ready? else 409 |                 |                    |
   |                  |--(4) INSERT JobRun(Pending, key); job -> IN PROGRESS             |
   |                  |--(5) enqueue(runId) ---------------------->|---(6) runId ------->|
   |                                                                                     |
   |                                (7) load run + job + template; run RUNNING; attempt++
   |                                (8) for each step in order:
   |                                      already Succeeded?  skip
   |                                      execute it -> INSERT JobRunStep(output) -> save
   |                                      throws?  -> JobRunStep(Failed) saved, then:
   |                                         retries left: run PENDING, enqueue(runId, delay)   [stop here]
   |                                         none left:    run FAILED, job FAILED, e-mail the owner
   |                                (9) all steps ok: run SUCCEEDED, then
   |                                      template needs approval -> job NEEDS MANUAL ACTION, schedule chaser (6.5)
   |                                      otherwise -> job COMPLETED, owner notice (6.6), start chained jobs (6.2d)
```

What each step does depends on the template:

```
                   finance templates (Daily Reconciliation, Approval, Chaser, Draft)    File Reconciliation
 DownloadReport    call the finance app, output = the report rows (6.4)                 pin the uploaded files (6.7)
 Calculate         run the named rules over the report (6.4)                            evaluate every record (6.7)
 SendEmail         e-mail the first e-mail field of the template, with the summary      same
```

### 6.2 The five ways a run starts

Each trigger ends in `RunOrchestrator`, which builds an **idempotency key**, returns the existing run if that key was seen, and otherwise creates one (6.1 steps 2 to 5).

**a) Manual** - `POST /jobs/{id}/run`

```
 Run now button / API --> JobRunService (owner or admin, job is Manual) --> key = "{job}:manual:{Idempotency-Key header}"
                          no header -> a fresh random key each call (never deduplicated)
```

**b) Fixed** - one time, IST

```
 create job --> Quartz one-shot trigger at RunAtUtc  ..........(time arrives)..........> FireJobRunJob
                                                        key = "{job}:fixed:{RunAtUtc}" --> RunOrchestrator
 Time already past (API was down) -> trigger "starts now": fires on the next start-up.
 Cancelled or already started in between -> the firing is skipped and logged.
```

**c) Recurrent** - Daily / Weekly / Monthly, IST

```
 create job --> friendly recurrence -> Quartz cron (evaluated in IST) ..(each occurrence).. FireJobRunJob
                                                key = "{job}:recurrent:{fire time}" --> RunOrchestrator
 Previous run still going, waiting for approval, or Failed -> that firing is skipped and logged.
 First firing is the NEXT occurrence of the time, never immediately.
 A firing missed while the API is down is NOT replayed: on start-up the trigger is rebuilt from "now".
```

**d) Event-based** - runs after another job

```
 upstream run succeeds, no approval needed --> JobRunner --> IJobChainer.OnJobCompletedAsync(job, run)
 upstream approved (approval jobs)         --> ApprovalService ------^
        for each Scheduled event-based job waiting on this upstream:
            key = "{dependent}:chain:{upstream run id}" --> RunOrchestrator   (a replay maps to the same run)
 One failing dependent is logged and skipped; it never fails the upstream job or the other dependents.
 A failed or rejected upstream starts nothing.
```

**e) Trigger-based** - external webhook

```
 external system --POST /webhooks/jobs/{id}  X-Webhook-Token, [Idempotency-Key], body <= 8 KB-->  TriggerService
     job unknown, not trigger-based, or token wrong  --> the same 401 for all three (ids cannot be probed)
     token check: SHA-256(token) compared in constant time with Jobs.WebhookTokenHash (the token itself is never stored)
     key = "{job}:trigger:{Idempotency-Key}"  (no header -> fresh key, every call starts a run)
     body stored on the run as TriggerPayload and shown in run history
```

### 6.3 Retry

```
 auto-retry   (inside JobRunner)   step fails -> run PENDING -> enqueue(runId, delay) -> same run, same key
 manual Retry (POST /jobs/{id}/retry, needs jobs.retry or admin, job must be FAILED)
              latest run: Pending, AutoRetriesUsed = 0, error cleared -> job IN PROGRESS -> enqueue(runId)

 Both resume the SAME run: Succeeded steps are skipped, so a report is not downloaded twice, a calculation
 is not repeated and an e-mail that was sent is not sent again.
```

### 6.4 Finance report and rules flow (Daily Reconciliation and the other finance templates)

```
 DownloadReportStep
   POST {FinanceApp:BaseUrl}reports  { idempotencyKey: "<run key>:download", reportName }  --> { reportId }
   GET  {FinanceApp:BaseUrl}reports/{reportId}                                              --> rows [(account, amount) ...]
   step output (JSON) = the report                      same key -> same reportId (a retry never creates a second report)

 CalculateStep   reads the DownloadReport output
   job config "rules": "Total Position, Exposure Cap"
        --> CalculationRules by name (must exist and be active, else the step fails)
        --> IRuleRegistry.Find(rule.Type)  [SumAmount | Threshold | Variance]
        --> Evaluate(report, rule parameters)  -->  RuleResult(name, value, passed, message)
   step output = summary "2 of 3 rules passed ..." + value per rule
   A rule that does not pass is a RESULT, not an error: the job still completes.

 In the POC the "finance app" is /mock-finance inside this API (Development only): it always returns the
 same three rows (total 1048.75). FinanceStub:FailFirstAttempts makes the first N calls per key answer 503.
```

### 6.5 Approval workflow (Capital Allocation Approval)

```
 Run now --> pipeline runs --> SendEmail sends the APPROVAL REQUEST to the approver  (key "<run>:email")
          |
          +--> job NEEDS MANUAL ACTION (StatusChangedAt = now); appears in GET /jobs/manual-queue
          +--> if the job has followUpAfterMinutes: Quartz one-shot "follow-up" due StatusChangedAt + N minutes

 approver opens "Manual actions" --> Approve / Reject
     only if job is still NEEDS MANUAL ACTION (else 409: a second click or second approver never decides twice)
     only the named approver (with jobs.approve) or an admin (else 403)
     Reject needs a comment (400 otherwise)

 Approve:  JobApproval row, job COMPLETED, chaser cancelled, chain event-based dependents (6.2d)
 Reject:   JobApproval row, job FAILED,    chaser cancelled;  Retry (tech/admin) re-runs it and it waits again

 Follow-up chaser, if nobody has decided when it is due (FollowUpJob -> ApprovalFollowUp.FireAsync):
     job still NEEDS MANUAL ACTION?  no -> nothing sent
     e-mail the approver "still waiting for your approval"   key "<run>:followup:<attempt>", sent once
 After a restart the chaser is re-scheduled from the job's StatusChangedAt (a late one fires at once, once).
```

The approver is fixed when the job is created: the `approverEmail` field must be a registered user who holds `jobs.approve` or is an admin.

### 6.6 E-mail

```
 caller (step, notifier, chaser)  --EmailMessage{to, subject, body, idempotencyKey, event?}-->
     PreferenceAwareEmailSender   event muted by the recipient (registered users only)?  yes -> log "skipped", stop
        IdempotentEmailSender     key already in SentEmails?  yes -> log "suppressed", stop
           SmtpEmailTransport     send via Gmail (MailKit)   -- then INSERT SentEmails(key)
 With no Gmail credentials the innermost sender is LoggingEmailSender instead: it logs
 "STUB EMAIL to <to>: <subject>" (never the body) and dedupes by key in memory.
```

| E-mail | Sent by | Event tag | Key | To |
|---|---|---|---|---|
| Result / approval request | the SendEmail step | `JobCompleted`, or `ApprovalRequested` for approval templates | `<run>:email` | first e-mail field of the template |
| Completion notice | JobRunner after a non-approval run | `JobCompleted` | `<run>:completed` | job owner; skipped when the result e-mail already went to the owner |
| Failure | JobRunner when retries run out | `JobFailed` | `<run>:failure:<attempt>` | job owner; a manual retry that fails again notifies again |
| Approval reminder | the follow-up chaser | `FollowUpReminder` | `<run>:followup:<attempt>` | the approver |

At-least-once window: the send happens before the record, so if the process dies between the two, a retry sends again.

### 6.7 File reconciliation (File Reconciliation template)

```
 (A) upload      browser --multipart--> POST /job-files --> InputFileService
                    each file: extension .csv, <= 5 MB, then CsvTable.Parse
                    (header row, delimiter detection, row-level errors, <= 50,000 rows)
                    --> [InputFiles: alias suggested from the name, headers JSON, row count, SHA-256, unattached]
                        [InputFileContents: the bytes]
                    <-- headers + row count + 5-row preview  (the rule builder's dropdowns come from these headers)

 (B) create job  POST /jobs {templateId, config, reconciliation: {files:[{fileId, alias}], rules:[...]}}
                    JobService --> ReconciliationSetupService.AttachAsync  (validates everything first, saves nothing)
                       files: alias valid and unique, file is yours and not already used
                       rules: files exist, key columns exist (both sides, unless a within-file rule), expressions parse,
                              every referenced column exists in the right file, tolerance sane
                    then ONE save: Job + ReconciliationRules + files attached to the job   (invalid setup -> nothing saved)

 (C) run         (6.1) pipeline with the file branch
        DownloadReport  ReconciliationRunner.SnapshotInputsAsync
                            current active files of the job --> output {files:[{alias, fileId, fileName, sha256, rows}]}
        Calculate       ReconciliationRunner.RunAsync(snapshot)
                            read exactly the snapshot's bytes (hash mismatch -> the step fails)
                            parse each file once; for each rule (in order):  RuleEvaluator.Evaluate(rule, left, right)
                            ReplaceResultsAsync: in ONE transaction delete this run's results, insert the new ones
                            output = summary text ("52 of 62 records passed ...") + failures per rule
        SendEmail       unchanged (the summary goes in the e-mail)

 (D) per record  RuleEvaluator, per rule
        index the right file by key (trimmed, case-insensitive)
        for each left record:  blank key -> FAIL | no partner -> FAIL | duplicate key either side -> FAIL
                               else  L = leftExpr(row), R = rightExpr(row), diff = L - R
                                     ==  pass if |diff| <= tolerance (an amount, or a % of |R|)
                                     !=  the opposite;   < <= > >=  compare L and R
                               a non-numeric or empty cell, or divide by zero -> FAIL that record only, with the reason
        then every right record nobody matched -> FAIL "no matching record in the left file"
        within-file rule (same file both sides): no join, both sides read the same row

 (E) read        GET /runs/{id}/reconciliation/summary   per-rule totals (rules with no results show zero)
                 GET /runs/{id}/reconciliation/results   ?status= ?ruleId= ?page= ?pageSize=  (max 500)
                 GET /runs/{id}/reconciliation/export    ?format=csv|pdf plus the same filters
                    EVERY matching record, not just one page
                    csv: formula-safe text, plain numbers
                    pdf: summary + records, failures first, capped at 2,000 records with a note

 (F) replace     PUT /jobs/{id}/reconciliation/files {alias, fileId}   (owner or admin, job not running, not cancelled)
                    the new file must still contain every column the rules read
                    old file: SupersededAtUtc set (kept for audit)   new file takes the alias, same save
                    the NEXT run pins the new file; earlier runs keep their own results
```

A rule that fails records is a result, not an error: the job still completes. A rule that no longer fits its file (for example a replaced file lost a column) fails the step loudly instead of giving a misleading result.

### 6.8 Start-up recovery

```
 API start --> ExecutionStartupService --> StartupRecovery.RecoverAsync   (the database is the source of truth)
    Scheduled Fixed jobs        --> new one-shot trigger (a past time fires immediately)
    active Recurrent jobs       --> new cron trigger from RecurrenceCron   (no catch-up: missed firings are skipped)
    jobs NEEDS MANUAL ACTION    --> follow-up chaser re-scheduled from StatusChangedAt
    runs still Pending/Running  --> enqueue(runId) again; finished steps are skipped
    log:  "Startup recovery: N job(s) rescheduled, M run(s) re-queued."
 Event-based and webhook jobs need no trigger rebuild: they start from an event (an upstream completion, an external call).
 A run that was waiting out an auto-retry delay is Pending, so it is re-queued at once instead of after its delay.
```

### 6.9 Sign-in and access checks

```
 POST /auth/login --> verify password (PBKDF2-SHA256, 100,000 iterations)  [same error for unknown email or wrong password]
      --> JWT (HS256, 60 min) with claims: user id, e-mail, role, primary team, observer teams, permissions
 every request --> JWT bearer validation --> controller policy (AdminOnly / CanApprove / CanRetry) -->
      Application service re-checks what the policy cannot:
          job visibility:  admin or jobs.view.otherTeams -> all teams;  otherwise own team + observer teams
                           a job you may not see answers 404, exactly like a missing one
          Run now:         owner or admin, job must be Manual
          Retry:           jobs.retry or admin
          decide approval: named approver with jobs.approve, or admin
          replace file:    owner or admin
 Self-registration creates an Employee on the Business team with no permissions;
 only an admin changes team, role or permissions.
 Demo accounts: admin (all permissions; Technical, observes Business), business (jobs.approve only),
                tech (jobs.retry; Technical, observes Business).
```

## 7. Idempotency

```
 what                          key                                              effect of a repeat
 ----------------------------  -----------------------------------------------  -------------------------------------------
 Run (any trigger)             JobRuns.IdempotencyKey (unique index)           returns the existing run, enqueues nothing
   manual / fixed / recurrent  {job}:manual:{key} | :fixed:{runAt} | :recurrent:{fire time}
   chained / webhook           {job}:chain:{upstream run} | :trigger:{key}
 Retry of a run                the same run                                     finished steps are skipped
 Finance report                "{run key}:download"                             same report id, never a second report
 E-mail                        SentEmails.IdempotencyKey (see 6.6)              not sent twice
 Reconciliation results        run id                                           old results replaced in one transaction
 Reconciliation input          file ids + SHA-256 pinned in the DownloadReport  a changed file is refused, not used silently
                               step output
 Approval decision             job status must be NEEDS MANUAL ACTION          second decision answers 409
```

The key is checked **before** the job's readiness, so repeating a trigger returns the existing run even if the job has since completed.

## 8. Access control

JWT with a role (Employee/Admin), a primary team (Business/Technical), optional observer teams, and permission claims (`jobs.retry`, `jobs.approve`, `jobs.view.otherTeams`); admins implicitly hold every permission. Details and the per-action checks are in 6.9.

Anonymous endpoints: `/auth/register`, `/auth/login`, `/webhooks/jobs/{id}` (per-job secret), `/health`, and `/mock-finance/*` (Development only; it answers 404 elsewhere).

## 9. Frontend

```
 App (tabs)                                  data it loads                                   refresh
 ------------------------------------------  ----------------------------------------------  ---------------------
 Jobs      JobsPage                          GET /jobs (filtered, and all for counts)        every 2 s while a job runs
             CreateJobForm                   GET /templates, GET /jobs (event-based picker)
               ReconciliationSetup           POST /job-files  (upload; columns come back)
             job detail: ReconciliationSetupView   GET /jobs/{id}/reconciliation, PUT .../files
                         ApprovalHistory     GET /jobs/{id}/approvals
                         RunHistory          GET /jobs/{id}/runs                             every 2 s while a run is active
                           ReconciliationResults   GET /runs/{id}/reconciliation/summary | results | export
 Manual actions   ManualQueuePage            GET /jobs/manual-queue, POST approve / reject   every 5 s
                  (tab badge: count)         GET /jobs/manual-queue                          every 10 s
 Catalog          CatalogPage                GET /templates, POST /admin/templates/{id}/approve (admin)
 Notifications    NotificationsPage          GET/PUT /me/notification-preferences
 My access        your profile (a stored token is checked with GET /auth/me on load) and an API health panel
```

The JWT is kept in `localStorage` and sent as a bearer token. Server times are UTC and shown in IST. Exports are fetched with the token and saved from a blob (a plain link could not send the header). The rule-builder logic (alias renaming, expression checking mirroring the server's grammar, payload building) is pure TypeScript in `web/src/jobs/reconciliation.ts`, tested with `npm test`.

## 10. Known limits

By design for the POC:

- Single timezone (IST), no holiday calendars, single approver, no retention policy, passwords and JWT only (no SSO).
- Queue, scheduler and retry delays are in memory. A restart re-queues unfinished runs from the database (6.8), but a **Recurrent firing missed while the API is down is not replayed**, so the PRD's "missed runs must be visible" is only met for Fixed jobs (flagged at risk when late) and for runs that did start.
- E-mail has a narrow at-least-once window (6.6). Only the job owner is notified of failures, not the team.
- File reconciliation compares numbers only (no text or date comparison, no aggregates such as a sum across a file), reads CSV only, and caps files at 5 MB / 50,000 rows.
- A Manual job runs once, so reconciling new files needs a new job or a Recurrent / Trigger-based one.
- Follow-up Chaser template: its message and "remind again after" fields are saved but not used.
- Swagger has no bearer-token setup, so protected endpoints cannot be called from it.
- Technical-team "visibility into job internals" (PRD section 2) is not differentiated: everyone who can see a job sees its run history, retry settings and errors.
- Development uses a random JWT signing key per start unless `Jwt:Key` is configured (users are logged out on every API restart); outside Development the key is required.

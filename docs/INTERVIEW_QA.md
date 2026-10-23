# Interview Q&A

Likely interview questions about this project, with answers grounded in the code and docs. Part A is project and engineering questions ("normal"). Part B is system design: how this POC would change at production scale. Where the POC has a known gap, the answer says so plainly; owning a limit is stronger than hiding it.

Sources: [`PRD.md`](PRD.md), [`ARCHITECTURE.md`](ARCHITECTURE.md) (section numbers like 6.1 refer to it), [`DECISIONS.md`](DECISIONS.md) (`#n` refers to its numbered entries), [`PLAN.md`](PLAN.md), and the code under `src/`.

Contents: [30-second pitch](#30-second-pitch) · [Part A: project and engineering](#part-a-project-and-engineering) · [Part B: system design](#part-b-system-design) · [Quick facts](#quick-facts-to-have-ready) · [Questions to ask back](#questions-to-ask-back)

## 30-second pitch

A job scheduling and orchestration app for banking operations teams. Users create jobs from an admin-approved template catalog and pick one of five schedule types: Fixed, Recurrent (IST cron), Event-based (job chaining), Trigger-based (webhook) and Manual. Every run goes through a queued pipeline, download report, calculate with pluggable rules, send email, with automatic retry and backoff, a manual retry that resumes after the last good step, and an idempotency key so nothing is sent or counted twice. There is a single-approver workflow with a chaser email, role/team/claim-based access, and a CSV file-reconciliation feature. Stack: ASP.NET Core 8, Quartz.NET, Channels, EF Core and PostgreSQL, React/Vite/TypeScript/Tailwind, Gmail via MailKit. It is a POC, and I can tell you exactly where it is not production-grade.

---

## Part A: Project and engineering

### A1. Walk me through the architecture.

Four .NET projects with a strict dependency direction: `Api -> Application, Infrastructure`, `Infrastructure -> Application, Domain`, `Application -> Domain`. Controllers are thin; logic lives in Application services. Every external seam is an interface defined in Application and implemented in Infrastructure: `IJobStore` and the other stores (EF Core), `IJobScheduler` (Quartz), `IJobQueue` (Channels), `IFinanceAppClient` (HTTP), `IEmailSender`/`IEmailTransport` (MailKit), `IRunHistoryExporter` (CSV/QuestPDF). It is one process: Quartz and the queue worker run as hosted services next to the API, with PostgreSQL as the source of truth. (ARCHITECTURE.md sections 1 and 2.)

**Follow-up: why interfaces for everything?** Swapping the in-memory queue for a broker, or the stub finance app for a real one, touches one Infrastructure class and no business logic. It also lets the unit tests mock every seam (xUnit + Moq; 384 `[Fact]`/`[Theory]` tests).

### A2. Explain the five schedule types and how each one starts a run.

All five end in `RunOrchestrator`, which builds an idempotency key and either returns the existing run or creates one.

| Type | Mechanism | Key |
|---|---|---|
| Fixed | Quartz one-shot trigger at the IST time | `{job}:fixed:{runAt}` |
| Recurrent | Quartz cron evaluated in IST (daily/weekly/monthly turned into cron) | `{job}:recurrent:{fire time}` |
| Event-based | `JobRunner` calls `IJobChainer` when the upstream run succeeds (or is approved) | `{job}:chain:{upstream run id}` |
| Trigger-based | `POST /webhooks/jobs/{id}` with a per-job token | `{job}:trigger:{Idempotency-Key}` |
| Manual | `POST /jobs/{id}/run` by the owner or an admin | `{job}:manual:{Idempotency-Key}` |

### A3. What happens, step by step, when a run executes?

(ARCHITECTURE 6.1.) The orchestrator looks up the key, checks the job is ready, inserts a `JobRun(Pending)`, marks the job In Progress and enqueues the run id. `JobRunWorker` reads the queue, creates a DI scope and calls `JobRunner.ExecuteAsync`. The runner marks the run Running, then executes the steps in a fixed order (DownloadReport, Calculate, SendEmail). Each step's output is saved as a `JobRunStep` row the moment it finishes. If a step throws, the failure is saved, then either an auto-retry is queued or the run and job become Failed. If all steps pass, the job becomes Completed, or Needs Manual Action for an approval template.

### A4. How is idempotency implemented, and where exactly?

Four layers (ARCHITECTURE 7):

1. **Runs**: `JobRuns.IdempotencyKey` has a unique index. `RunOrchestrator.EnqueueAsync` looks the key up first and returns the existing run without enqueuing anything. The key is checked *before* job readiness, so repeating a trigger returns the same run even after the job completed.
2. **Steps**: `run.HasSucceeded(step)` makes the runner skip finished steps on any later attempt (auto-retry, manual retry, restart recovery).
3. **Finance report**: the download call sends `"{run key}:download"` as its own idempotency key, so a retry gets the same report id and never creates a second report.
4. **Email**: `SentEmails.IdempotencyKey` is the primary key. `IdempotentEmailSender` checks it, `SmtpEmailTransport` sends, then the key is recorded. A repeat is suppressed.

**Follow-up: what is the weakness?** Email send happens *before* the record is written, so a crash between the two re-sends on retry. That is at-least-once, documented as a known limit (ARCHITECTURE 6.6). Making it exactly-once would need either an outbox (record "intent to send" first, then a sender that marks done) or a provider-side idempotency key, which SMTP does not offer.

### A5. How do retries work? Explain auto-retry versus manual retry.

**Auto-retry** is inside `JobRunner`: on a step exception, if `AutoRetriesUsed < MaxAutoRetries` the run goes back to Pending and is re-enqueued with a delay of `BackoffSeconds * 2^(n-1)`, capped at 3,600 s. **Manual retry** (`POST /jobs/{id}/retry`, needs `jobs.retry` or admin, job must be Failed) resets the counter and re-queues the **same run**. Both resume the same run with the same key, so succeeded steps are skipped: the report is not re-downloaded, the calculation is not repeated and an email that went out is not sent again. A step failure is saved as a `Failed` step row with the attempt number, so the history shows every attempt.

**Follow-up: why not retry forever or retry only the failed step with a new run?** A new run would get a new key and lose the "already done" memory. Capping retries and then requiring a human stops a broken job from hammering a downstream system.

### A6. What does the queue give you here, and why Channels?

The PRD asks for async processing, so the pipeline is decoupled from the trigger: a request or a Quartz firing only creates a run and enqueues an id, and a worker executes it. `System.Threading.Channels` is the zero-infrastructure implementation behind `IJobQueue` (unbounded channel, single reader). Delayed enqueue for retry backoff is a `Task.Delay` then write. The trade-off is that it is in-memory and in-process: a restart loses queued messages and pending retry delays, which is why `StartupRecovery` re-queues every Pending/Running run from the database (see B2).

### A7. What happens if the API crashes mid-run?

`ExecutionStartupService` runs `StartupRecovery` on start. It rebuilds Fixed triggers (a past time fires immediately), Recurrent cron triggers and approval chasers, and re-enqueues every run still Pending or Running. Because finished steps are persisted, the re-queued run resumes from the last good step. If shutdown is graceful, the runner rethrows `OperationCanceledException` and leaves the run Running so recovery picks it up. A queue message delivered twice is harmless: a finished run is a no-op, and a step that already succeeded is skipped.

### A8. What is the biggest known gap in the scheduler?

A **Recurrent firing missed while the API is down is not replayed**. Quartz uses the in-memory RAMJobStore, and recovery rebuilds the trigger from "now". The Quartz misfire policy only covers a firing delayed while the process is up. This partly misses the PRD line "missed runs must be visible": Fixed jobs are flagged at risk when late, but a skipped recurrent tick leaves no trace (DECISIONS #37, ARCHITECTURE 10). The fix is either Quartz's ADO.NET persistent store with clustering, or recording an expected-run ledger and reconciling it against `JobRuns` on start.

### A9. Why did you keep Quartz in memory instead of its persistent store?

DECISIONS #1: our Postgres `Job`/`JobRun` tables are already the source of truth, so a second persistence layer inside Quartz is redundant for a POC. The cost is the gap in A8. In production I would take the persistent store with clustering for HA, or move to a design where the database drives the schedule (A8, B3).

### A10. How does the pluggable calculation engine work?

Rules are data. A `CalculationRule` row has a name, a type (`SumAmount`, `Threshold`, `Variance`) and JSON parameters. `IRule` implementations are registered in DI and found through `IRuleRegistry.Find(type)`. A job's config has a `rules` field with names; `CalculateStep` loads those rows (must exist and be active), runs each over the downloaded report and produces a `RuleResult(name, value, passed, message)`. Admins add or edit rules through `/admin/rules` with no deploy; a new rule *type* is one new class. A rule that does not pass is a **result**, not an error: the job still completes (DECISIONS #39). A step fails only for real problems such as a missing rule or a finance app error.

### A11. How does the file-reconciliation feature work, and what design decisions are in it?

(ARCHITECTURE 6.7.) Upload CSVs (<= 5 MB, <= 50,000 rows, delimiter detected). A rule is data: a join key per file, a left and right arithmetic expression, an operator and a tolerance. The expression language is a small hand-written parser over `alias.Column`, numbers and `+ - * /`, with `decimal` math, so rules cannot execute code (no Roslyn, scripting or SQL; DECISIONS #23). Key decisions:

- Unmatched records, duplicate keys and blank keys are reported as failures with a reason, never silently dropped (#24).
- A bad cell fails only that record; a rule that no longer fits its file fails the step loudly (#25).
- The download step pins file ids and SHA-256 hashes; the calculate step reads exactly those and refuses a changed file (#28).
- Results are stored as rows (not JSON) so they can be paged, filtered and exported, and replaced per run in one transaction.
- Replacing a file supersedes the old one rather than deleting it, preserving the audit trail (#29).

### A12. Explain the auth and access-control model.

Email + password (PBKDF2-SHA256, 100,000 iterations, per-password salt), JWT (HS256, 60 minutes) carrying user id, email, role, primary team, observer teams and permissions. Three layers: **role** (Employee/Admin), **team** (Business/Technical, plus observer teams via `UserTeamAccess`) and **claims** as permission strings (`jobs.approve`, `jobs.retry`, `jobs.view.otherTeams`). Admins implicitly hold every permission. Controllers apply coarse policies (`AdminOnly`, `CanApprove`, `CanRetry`); application services re-check what a policy cannot express (job visibility by team, owner-or-admin for Run now). Self-registration always yields an Employee on the Business team with no permissions.

**Follow-up: why a custom claims table, not ASP.NET Core Identity?** DECISIONS #4: no SSO in scope, and a small `UserClaim` table is easier to explain and demo. Production would use Identity or an external IdP (OIDC) and keep the permission strings.

### A13. Why does a job you cannot see return 404 instead of 403?

So job ids cannot be probed: a forbidden job and a non-existent one look identical. The same idea applies to the webhook: an unknown job, a non-trigger job and a wrong token all return the same 401. It is a small information-leak defence.

### A14. How is the webhook secured?

A random 256-bit token per job, shown once at creation. Only its SHA-256 is stored (`Jobs.WebhookTokenHash`), and the comparison is constant-time, so a leaked database does not leak usable tokens and timing does not help an attacker. Body is capped at 8 KB and stored on the run as `TriggerPayload`. Deduplication is by an explicit `Idempotency-Key` header only; hashing the body was rejected because an external system legitimately sends identical payloads, such as an empty "report ready" ping, and each must start a run (DECISIONS #14, #15). A production upgrade would be HMAC signatures over the body with a timestamp to block replay.

### A15. How does the approval workflow guarantee a single decision?

The approver is named when the job is created (must hold `jobs.approve` or be admin). A decision is only accepted while the job is *Needs Manual Action*; the second click, or a second approver, gets 409. Only the named approver or an admin can decide (else 403), and Reject requires a comment. Approve completes the job and chains event-based dependents; Reject marks it Failed so the normal Retry re-submits. A Quartz one-shot sends one chaser email if nobody has decided by the follow-up time, and the chaser itself re-checks the status so it never emails after a decision.

**Follow-up: is there a race between two approvers?** The status check and write are in one save; a stricter guarantee would use optimistic concurrency (a row version) so the second writer fails. That is a reasonable hardening step.

### A16. How do notifications avoid inbox overload and double sends?

A decorator chain around `IEmailSender`: `PreferenceAwareEmailSender` (user muted this event? skip) wraps `IdempotentEmailSender` (key already sent? suppress) wraps the real sender (`SmtpEmailTransport` via MailKit, or `LoggingEmailSender` when Gmail is not configured). Each message carries an event tag (`JobCompleted`, `JobFailed`, `ApprovalRequested`, `FollowUpReminder`). Preference rows default to enabled, so adding a decorator changed no existing behaviour (DECISIONS #17). The decorator pattern means preferences and dedupe are not scattered across call sites.

### A17. How did you handle time zones?

Everything is stored in UTC; scheduling input and all display are IST, converted at the edges. Quartz cron triggers are created with a custom IST `TimeZoneInfo`. There is no holiday or business-day calendar (PRD non-goal). A caveat: IST has no daylight saving, which is why a fixed-offset zone is safe here; a multi-zone product would need IANA zones and care around DST.

### A18. How did you test it, and what would you add?

xUnit + Moq: 384 test methods covering scheduling, retry, idempotency, the rule engine, auth, approvals, the CSV parser, the expression parser and the reconciliation evaluator. The reconciliation tests run the documented sample files and assert the README's lists so docs and code cannot drift. A few opt-in Postgres tests run when `JOBSCHEDULER_TEST_PG` is set; the web rule-builder logic is tested with Node's built-in runner. I would add: Testcontainers-based integration tests for the full pipeline against real Postgres in CI, a concurrency test for duplicate triggers (A19), and contract tests for the finance client.

### A19. What race conditions exist, and how would you close them?

`RunOrchestrator.EnqueueAsync` does check-then-insert: find by key, then add. Two concurrent identical triggers can both pass the check; the **unique index** on `IdempotencyKey` is the real guarantee, so the loser gets a unique-violation exception rather than a duplicate run. Today that exception is not translated into "return the existing run"; the fix is to catch the unique violation and re-read by key (the same `TryRecordAsync` pattern already used for sent emails). Similarly, `JobRunWorker` has a single reader, so one run is never processed by two workers in this process; with multiple instances you would need a claim (`UPDATE ... WHERE status='Pending' RETURNING`) or `SELECT ... FOR UPDATE SKIP LOCKED`.

### A20. What would you do differently, or what are you least happy with?

Honest list: the in-memory Quartz store (A8); the email at-least-once window (A4); the unhandled duplicate-key race (A19); `Team` is an enum instead of a table, so it cannot grow into real multi-tenancy (DECISIONS #3); Swagger has no bearer-token setup; the Technical team's "visibility into internals" is not differentiated from Business; and the JWT sits in `localStorage` (XSS exposure; an httpOnly cookie plus CSRF protection is safer). I documented these in ARCHITECTURE section 10 rather than leaving them for someone to find.

### A21. Why Postgres bytes for uploaded files instead of blob storage?

POC simplicity, behind `IReconciliationStore` so object storage can replace it. Bytes live in a separate `InputFileContents` table so metadata listings never load them (DECISIONS #27). For production I would use S3/Azure Blob with the SHA-256 kept in the database, which also fits the "pin file by hash" idempotency design.

### A22. How did you work with an AI coding assistant on this, and how did you keep control?

A written `CLAUDE.md` set the architecture rules; each phase had a plan I approved first, then a branch and PR to `main`, small commits, tests, and manual verification steps. Decisions that diverged from the original plan (NCalc dropped, `Team` as enum) were recorded in DECISIONS with a **Status** note instead of being rewritten. I reviewed the docs against the code afterwards, which turned up missing entries (#33 to #44) and inaccuracies. The point to make: the assistant accelerates typing, but the design, the trade-offs and the verification are mine to explain.

---

## Part B: System design

### B1. Design this for production scale: 100k jobs, millions of runs a day, multiple regions. What changes?

Keep the domain model (Job, JobRun, steps, idempotency keys); replace the in-process parts.

```
 API tier (stateless, N replicas)  -->  PostgreSQL (primary + replicas, partitioned runs table)
        |                                        ^
        | create run + outbox row (one txn)      | claim runs
        v                                        |
   Outbox relay  -->  Broker (SQS / RabbitMQ / Kafka)  -->  Worker fleet (autoscaled)
                                                              |-- finance app (circuit breaker, rate limit)
                                                              |-- email service (provider with idempotency)
   Scheduler tier (leader-elected or sharded) --reads next_run_at from DB-->  creates runs
```

- **Stateless API** behind a load balancer; auth via an IdP.
- **Durable queue** instead of Channels, so a restart or a deploy loses nothing.
- **Transactional outbox**: write the run and the "enqueue" intent in one DB transaction, so a crash between "saved" and "enqueued" cannot strand a run (today `StartupRecovery` papers over that gap).
- **Workers** scale horizontally and claim runs with `FOR UPDATE SKIP LOCKED` or visibility timeouts.
- **Runs table** partitioned by time, with archiving to cold storage under a retention policy (the POC has none).

### B2. The queue is in memory. How would you make execution durable and what delivery semantics would you pick?

Move to a broker (SQS, RabbitMQ, or Kafka if ordering/replay matters) with **at-least-once delivery** plus **idempotent consumers**. That is the model the code already assumes: the runner treats a finished run as a no-op and skips succeeded steps, so redelivery is safe. True exactly-once across a database, a broker and an external API does not exist; the honest design is at-least-once plus idempotency keys at every side effect, plus a dead-letter queue for poison messages. Retry backoff moves from `Task.Delay` to broker-native delayed delivery or a `next_attempt_at` column, so delays survive a restart.

### B3. How would you make the scheduler highly available without double-firing?

Options, in order of preference for this system:

1. **Database-driven schedule**: store `next_run_at` per recurring job; every scheduler instance polls `WHERE next_run_at <= now() FOR UPDATE SKIP LOCKED`, creates the run, and advances `next_run_at` in the same transaction. No leader needed, and downtime is naturally caught up (or skipped by policy), which also fixes the "missed run is invisible" gap.
2. **Quartz clustered mode** with the ADO.NET job store: instances coordinate through DB locks; misfire policies are persisted.
3. **Leader election** (lease in DB or Redis/ZooKeeper) so one instance fires.

In all cases the **run idempotency key** (`{job}:recurrent:{fire time}`) is the final safety net: even if two schedulers fire the same tick, the unique index yields one run.

### B4. How do you guarantee a scheduled job is never silently dropped (the PRD's reliability requirement)?

Treat "expected" and "actual" as separate facts. Materialise an **expected-runs ledger** (job, scheduled time, status) ahead of time, and a watchdog compares it to `JobRuns`. Anything past due plus a grace window with no run is flagged *Missed* and alerted. The existing at-risk flag is computed on read for Fixed jobs and long-running jobs; the ledger generalises it to recurrent ticks and survives downtime. Add metrics: schedule lag (fire time minus scheduled time), queue depth, oldest pending run age, with alerts on each.

### B5. How would you scale the workers and prevent one tenant from starving the rest?

Autoscale workers on queue depth and oldest-message age. For fairness: per-tenant (team/business unit) queues or weighted fair scheduling, per-tenant concurrency caps and rate limits, and separate worker pools for heavy job types (large reconciliations) versus quick ones. Priority lanes for approval and notification jobs keep latency-sensitive work from waiting behind a 50,000-row reconciliation. The current code already limits one run per job at a time (`CanStartRun`), which prevents self-overlap.

### B6. Design the multi-tenancy model. The PRD says business units share one instance.

Currently isolation is team + claim based and enforced in application services, with `Team` as an enum. For real tenancy: a `Tenant` table with `TenantId` on every row, enforced by **EF Core global query filters** plus **Postgres row-level security** as defence in depth, the tenant id carried in the JWT, and a per-tenant config for rate limits, quotas and email sender. Decide the isolation level against the risk: shared schema with RLS (cheapest), schema-per-tenant, or database-per-tenant for regulated clients who demand hard separation. For banking I would likely offer shared-schema for most units and dedicated databases for the most sensitive ones.

### B7. How would you integrate a real, unreliable finance API?

Today `IFinanceAppClient` is a real HTTP client against a mock, and the download is idempotent by key. For production: **timeouts** on every call; **retry with jitter** only for idempotent or keyed operations; a **circuit breaker** (Polly) so a down dependency fails fast and does not tie up workers; **bulkheads** (a concurrency limit per dependency); and the **async report pattern** from the PRD (trigger, then poll or receive a callback when the report is ready) instead of holding a worker while waiting. Handle auth with a managed identity or client-credentials flow, and record request ids on each step for tracing.

### B8. How would you make email delivery reliable and exactly-once-ish?

Replace direct SMTP in the step with a **transactional outbox**: the step writes an `OutboundEmail` row (with its idempotency key) in the same transaction as the step result; a separate relay sends and marks it sent. That closes the crash window in A4. Use a provider with an idempotency/message-id feature (SES, SendGrid) for provider-side dedupe, handle bounces and complaints via webhooks, and add rate limiting and per-recipient digesting so a burst of failures becomes one summary email, not fifty.

### B9. Design the audit and compliance story for a bank.

Auditability is a PRD requirement (who scheduled it, when it ran, what it did, what it sent). Today there are run and step rows, approvals with comments, sent-email records and CSV/PDF exports. Production additions: **append-only audit log** with who/what/when/before-after for every admin and security-relevant action (role changes, template approval, rule edits); tamper evidence (hash chaining or WORM storage); retention and legal-hold policies; PII minimisation (log "STUB EMAIL to X: subject", never the body, as the code already does); access logging on reads of sensitive reports; and separation of duties (the requester cannot approve their own job).

### B10. How would you secure it for production?

Move from self-issued JWT to an IdP (OIDC/SSO) with short-lived access tokens and refresh rotation; keep tokens out of `localStorage` (httpOnly, SameSite cookies plus CSRF protection, or the BFF pattern); store secrets (Gmail app password, JWT key, DB credentials) in a vault, never in config; TLS everywhere; webhook HMAC signatures with timestamps; rate limiting and lockout on login; strict input limits (already: 5 MB, 50,000 rows, 8 KB webhook body); formula-injection-safe CSV export (already implemented); dependency scanning and SAST in CI; and least-privilege DB roles. The file parser's no-code-execution expression language is already a good security property to keep.

### B11. How would you monitor and operate it?

Three signals. **Metrics**: run throughput, success/failure/retry rates by job type, p95 step duration, queue depth, oldest pending age, schedule lag, email send failures. **Logs**: structured with the run id, job id and idempotency key on every line so one run can be traced end to end. **Traces**: OpenTelemetry spanning API, queue, worker and the outgoing finance and SMTP calls. Alert on symptoms (jobs missed, queue age, failure ratio) rather than causes. Runbooks for the common cases: stuck Running runs, DLQ growth, finance app outage. Health endpoints split into liveness and readiness (`/health` exists today).

### B12. The runs table will become huge. How do you keep the dashboard fast?

Index for the actual queries: `(JobId, CreatedAtUtc desc)`, status and team filters, and the unique key index that already exists. Partition `JobRuns` and `ReconciliationResults` by month and archive old partitions. Keep large payloads out of hot rows (the design already separates file bytes from metadata, and results are rows so they page). Always paginate (reconciliation results cap at 500 per page, exports stream every matching record). Replace the 2-10 second polling the UI uses with **SSE or WebSockets** (SignalR is the natural .NET fit) so thousands of open dashboards do not hammer the API; a read replica or cached summary serves the counts.

### B13. How would you evolve the rules engine toward a real workflow/DAG engine?

Today the pipeline is a fixed three-step sequence, and chaining is job-to-job. To generalise: model a job as a **DAG of typed steps** with declared inputs/outputs, persisted step state (already done per step), and a scheduler that runs ready nodes in parallel. Each step keeps the same contract used now: idempotent, with its own key (`{run key}:{step}`), so retries and recovery work node by node. Cycle detection at definition time, versioned job definitions (a run pins the version it started with, just like reconciliation pins file hashes), and a rules DSL with validation and a dry-run/preview mode before activation. Or buy rather than build: this is the territory of Temporal, Airflow or Step Functions, and the honest answer is to compare cost of ownership before extending a custom engine.

### B14. Quartz and Postgres versus Temporal, Airflow or a cloud scheduler. When would you switch?

Keep the current approach while jobs are short, the topology is simple, and one team owns it. Switch to **Temporal** when you need durable multi-step workflows with long waits (approvals that last days), built-in retries, timers, and replay: the approval-plus-chaser flow here is exactly what a workflow engine models natively. **Airflow** fits data-pipeline DAGs with backfills. **Cloud schedulers** (EventBridge Scheduler, Cloud Scheduler) remove the HA scheduler problem for plain cron. The reason I did not use them for the POC is the PRD's stack requirement and wanting the mechanics (idempotency, recovery) visible and explainable.

### B15. Design the notification/follow-up system as a generic subsystem.

Separate **events** (job failed, approval requested) from **delivery** (email now; Slack, Teams, SMS later). Events publish to a topic; a notification service resolves recipients (owner, approver, team), applies per-user preferences (the `NotificationPreference` model, defaulting on), renders templates, dedupes by key, and hands off to channel adapters, each behind an interface like `IEmailSender`. Follow-ups become *scheduled, cancellable timers* keyed by (job, attempt): the chaser already re-checks state when it fires, which is the correct pattern, since timers are cheap to leave scheduled but must never trust that the world is unchanged. Add escalation (chase after 24 h, escalate to a manager after 48 h) as a policy table, not code.

### B16. How would you do zero-downtime deploys and schema migrations?

Run API replicas behind a load balancer with rolling deploys. On shutdown, workers stop taking new messages, finish or hand back the current run (the runner already leaves a cancelled run as Running so recovery re-queues it, and with a durable broker an unacked message is redelivered). Use **expand/contract migrations**: add nullable columns or new tables first, deploy code that writes both, backfill, then drop the old. EF Core migrations run as a separate deploy step, not at app start with N replicas racing. Feature flags gate new job types so templates can be approved separately from code release.

---

## Quick facts to have ready

| Topic | Fact |
|---|---|
| Stack | ASP.NET Core 8, Quartz.NET (RAMJobStore), Channels, EF Core + Npgsql, PostgreSQL, React + Vite + TS + Tailwind v4, MailKit (Gmail SMTP), QuestPDF (Community license) |
| Schedule types | Fixed, Recurrent, Event-based, Trigger-based, Manual |
| Run states | Pending, Running, Succeeded, Failed |
| Job states | Scheduled, In Progress, Completed, Cancelled, Failed, Needs Manual Action |
| Pipeline | DownloadReport -> Calculate -> SendEmail, persisted per step |
| Backoff | `BackoffSeconds * 2^(n-1)`, capped at 3,600 s |
| Auth | PBKDF2-SHA256 100,000 iterations; JWT HS256, 60 min; claims `jobs.approve`, `jobs.retry`, `jobs.view.otherTeams` |
| Webhook | 256-bit token, SHA-256 stored, constant-time compare, 8 KB body cap |
| Limits | CSV only, 5 MB, 50,000 rows, 5 files per job; PDF capped at 2,000 records |
| At-risk | Fixed time passed by more than 1 min without starting, or In Progress / Needs Manual Action longer than `AtRisk:ThresholdMinutes` (default 15); computed on read |
| Tests | 384 test methods, xUnit + Moq, plus opt-in Postgres tests and `npm test` for web logic |
| Known gaps | Missed recurrent firings not replayed; email at-least-once window; in-memory queue; `Team` is an enum; failure emails to owner only |

## Questions to ask back

- How do your teams handle missed or late scheduled work today, and how is it surfaced?
- Is the orchestration layer built in-house or on a workflow engine, and what drove that choice?
- How do you balance exactly-once expectations with at-least-once infrastructure in finance workflows?
- What does the approval and audit trail look like for regulated processes?

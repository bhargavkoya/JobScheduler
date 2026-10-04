# Job Scheduler (P1 POC)

A demoable skeleton for a job scheduling/orchestration platform — scheduling, queued pipelines, retries/idempotency, and role/team/claim-based access. See [`docs/PRD.md`](docs/PRD.md) for the full requirements, [`CLAUDE.md`](CLAUDE.md) for working conventions, and the living docs below for current project state.

This is a base-level interview POC, not a production system.

## Project docs

- [`docs/PRD.md`](docs/PRD.md) — product requirements
- [`docs/DEMO.md`](docs/DEMO.md) — 5-minute demo script
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — current-state architecture, updated every phase
- [`docs/PLAN.md`](docs/PLAN.md) — phased build checklist, updated every phase
- [`docs/DECISIONS.md`](docs/DECISIONS.md) — decisions log, appended every phase

## Stack

- Backend: ASP.NET Core Web API (.NET 8), Quartz.NET, BackgroundService workers, EF Core + Npgsql
- DB: PostgreSQL (docker-compose)
- Frontend: React + Vite + TypeScript + Tailwind CSS
- Email: Gmail SMTP (MailKit)

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download) (pinned via `global.json`)
- [Node.js](https://nodejs.org/) 20+
- Docker Desktop

## Running locally

```bash
# 1. Start PostgreSQL
docker compose up -d

# 2. Configure the backend connection string (one-time, local dev secret — not committed)
dotnet user-secrets set "ConnectionStrings:JobSchedulerDb" \
  "Host=localhost;Port=5433;Database=jobscheduler;Username=jobscheduler;Password=jobscheduler_dev_pw" \
  --project src/JobScheduler.Api

# 3. Apply EF Core migrations
dotnet ef database update --project src/JobScheduler.Infrastructure --startup-project src/JobScheduler.Api

# 4. Run the API (Swagger at /swagger, health check at /health)
dotnet run --project src/JobScheduler.Api

# 5. Run the frontend (separate terminal)
cd web && npm install && npm run dev
```

Frontend dev server: http://localhost:5173 — backend API: http://localhost:5031.

## Email (Gmail) and calculation rules

Email is sent through Gmail SMTP with MailKit. Without credentials the app still runs and just logs "STUB EMAIL" lines instead of sending.

1. In your Google account turn on 2-Step Verification, then create an **App password** (Google Account > Security > App passwords).
2. Store it as a local secret (never commit it):

```bash
dotnet user-secrets set "Email:Username" "you@gmail.com" --project src/JobScheduler.Api
dotnet user-secrets set "Email:Password" "<16-char app password>" --project src/JobScheduler.Api
```

(Or set the `Email__Username` / `Email__Password` environment variables.) Restart the API after changing them.

- Each email has an idempotency key and is recorded in `SentEmails`, so a retried run does not send twice. Terminal job failures email the job owner once per failed attempt.
- Calculation rules are data (`/admin/rules`, admin only). A job lists rule names in its `rules` field, comma-separated, for example `Total Position, Exposure Cap, Variance vs Ledger`. Seeded rule types: `SumAmount`, `Threshold`, `Variance`.

## Manual actions and approvals

- A template with `RequiresApproval` (seeded: "Capital Allocation Approval") runs its pipeline, emails the approver, then parks the job in **Needs manual action**. The approver is the `approverEmail` field and must be a registered user with the `jobs.approve` claim (or an Admin).
- Only that approver (or an admin) can approve or reject, once. Reject needs a comment and fails the job, so the normal Retry re-submits it. Decisions are kept as an audit trail on the job.
- Set `followUpAfterMinutes` on the job to send the approver one reminder email if nothing has happened by then. It is rebuilt from the database after a restart.
- The **Manual actions** tab (and `GET /jobs/manual-queue`) lists everything waiting on a human.
- A job is flagged **at risk** when a Fixed time passed without it starting, or when it has been running / waiting on an approval for longer than `AtRisk:ThresholdMinutes` (default 15). It is computed on read, not stored.

## Recurrent and chained jobs, export

- **Recurrent**: Daily / Weekly / Monthly at an IST time (monthly day capped at 28). Stored as a Quartz cron, evaluated in IST, rebuilt from the database on restart. Each firing is one run, keyed by the tick time, so a double fire cannot create two runs. A firing is skipped while the previous run is still going, waiting on approval, or Failed (retry it first). Cancel stops the recurrence.
- **Event-based**: pick an upstream job; when its run completes (including after approval) the dependent starts, once per upstream run. TriggerBased (webhooks) is still not supported.
- `GET /jobs/{id}/runs/export` (or the Export CSV button) returns the run history as CSV, times in IST.

## Commands

- `docker compose up -d` — start Postgres
- `dotnet build` — build the whole solution
- `dotnet run --project src/JobScheduler.Api` — run the API
- `dotnet test` — run the test suite
- `cd web && npm run dev` — run the frontend dev server

## Notes

- PostgreSQL is mapped to host port **5433** (not the default 5432) to avoid clashing with any pre-existing local Postgres installation.
- The backend connection string is a local-dev secret stored via `dotnet user-secrets`, never committed.

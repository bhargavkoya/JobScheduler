# Job Scheduler (P1 POC)

A demoable skeleton for a job scheduling/orchestration platform — scheduling, queued pipelines, retries/idempotency, and role/team/claim-based access. See [`docs/PRD.md`](docs/PRD.md) for the full requirements, [`CLAUDE.md`](CLAUDE.md) for working conventions, and the living docs below for current project state.

This is a base-level interview POC, not a production system.

## Project docs

- [`docs/PRD.md`](docs/PRD.md) — product requirements
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

## Commands

- `docker compose up -d` — start Postgres
- `dotnet build` — build the whole solution
- `dotnet run --project src/JobScheduler.Api` — run the API
- `dotnet test` — run the test suite
- `cd web && npm run dev` — run the frontend dev server

## Notes

- PostgreSQL is mapped to host port **5433** (not the default 5432) to avoid clashing with any pre-existing local Postgres installation.
- The backend connection string is a local-dev secret stored via `dotnet user-secrets`, never committed.

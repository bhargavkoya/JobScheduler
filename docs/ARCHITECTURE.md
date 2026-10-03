# Architecture

Current-state architecture. Updated at the end of every phase to reflect what's actually built — not the aspirational design (see [`PLAN.md`](PLAN.md) for the roadmap and [`DECISIONS.md`](DECISIONS.md) for why things are shaped this way).

## Phase 0 — current state

### Solution layout

```
JobScheduler.sln
global.json                   pins the .NET 8 SDK
docker-compose.yml             PostgreSQL (host port 5433, see DECISIONS.md)
src/
  JobScheduler.Api/            ASP.NET Core Web API (controllers), Program.cs composition root, Swagger, CORS
  JobScheduler.Application/    empty — service interfaces/DTOs land in Phase 1+
  JobScheduler.Domain/         empty — entities/enums land in Phase 1
  JobScheduler.Infrastructure/ JobSchedulerDbContext, EF Core migrations (Npgsql)
tests/
  JobScheduler.Tests/          xUnit scaffold, no tests yet
web/
  src/App.tsx                  placeholder page: calls GET /health, renders status
  vite.config.ts               React + Tailwind v4 (@tailwindcss/vite) plugins
```

### Dependency direction

`Api → Application, Infrastructure` · `Infrastructure → Application, Domain` · `Application → Domain`. `Domain` has no package dependencies. This direction is fixed for all future phases — new code should slot into the project matching its role, not accumulate in `Api`.

### Backend bootstrap (`src/JobScheduler.Api/Program.cs`)

- `JobSchedulerDbContext` registered via `AddDbContext`, connection string read from configuration key `ConnectionStrings:JobSchedulerDb` (local dev value lives in user-secrets, never committed).
- `GET /health` via `AddHealthChecks().AddDbContextCheck<JobSchedulerDbContext>()` — a real DB round-trip, not a static OK.
- Swagger/OpenAPI enabled in Development via Swashbuckle.
- CORS policy `WebClient` allows `http://localhost:5173` (the Vite dev server origin) — this is dev-only and will need revisiting once a real deployment origin exists.

### Persistence

- `JobSchedulerDbContext` currently has one `DbSet<SystemInfo>` — a trivial, non-domain marker table seeded with one row, added only to prove the EF Core migration pipeline end-to-end before real entities exist. It will be removed once Phase 1 domain entities land.
- Migrations live in `src/JobScheduler.Infrastructure/Persistence/Migrations`.

### Frontend bootstrap

- Vite + React 19 + TypeScript, Tailwind CSS v4 via the `@tailwindcss/vite` plugin (no separate PostCSS config needed).
- `App.tsx` fetches `${VITE_API_BASE_URL}/health` on mount and renders loading/healthy/error state. `VITE_API_BASE_URL` defaults to `http://localhost:5031` via `web/.env.development` (committed — not a secret).

### Not yet built

Everything else: domain model, auth, Quartz, the job queue/pipeline, the rules engine, and all REST endpoints beyond `/health`. See [`PLAN.md`](PLAN.md).

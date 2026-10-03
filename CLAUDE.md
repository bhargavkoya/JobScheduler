# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# Job Scheduler (P1 POC)

Base-level interview POC. Full requirements are in `docs/PRD.md`; read it before planning or coding.

Goal: a demoable skeleton I can screen-share in 5 minutes and discuss fluently (scheduling, orchestration, retries, idempotency, role/claim-based access).

It is NOT a production system. Do not over-engineer.

## Stack

- Backend: ASP.NET Core Web API (.NET 8), Quartz.NET, BackgroundService workers, EF Core + Npgsql
- DB: PostgreSQL (docker-compose)
- Frontend: React + Vite + TypeScript + Tailwind CSS
- Email: Gmail SMTP (MailKit), genuinely wired up
- Internal finance app: mocked REST API behind an interface (`IFinanceAppClient`)
- Queue: `IJobQueue` abstraction, with a `System.Threading.Channels` implementation first

## Architecture rules

- Solution layout: `src/JobScheduler.Api`, `.Application`, `.Domain`, `.Infrastructure`, `tests/JobScheduler.Tests`
- Keep controllers thin; logic lives in Application services
- Every external seam (finance app, email, queue, calculation engine) sits behind an interface
- Calculation engine is rules-based and pluggable (`IRule` / rule definitions stored as data); no hardcoded formulas
- Idempotency: every job run has an idempotency key; retries must not double-send emails or double-count calculations
- Single timezone: IST. No holiday/business-day calendars.
- Auth: email+password, JWT, roles (Employee/Admin), teams (Business/Technical), claims for fine-grained actions
- Single approver for manual-action jobs

## Conventions

- C#: nullable enabled, async all the way, `CancellationToken` on all async APIs, SOLID, constructor DI
- Use EF Core migrations; seed demo data (users, teams, 2+ job templates)
- Tests: xUnit + Moq for scheduling, retry, idempotency, and rule-engine logic
- Secrets (Gmail app password, JWT key) via user-secrets / env vars; never commit them
- Small commits, one per logical step, with clear messages

## Commands

- `docker compose up -d` — postgres
- `dotnet run --project src/JobScheduler.Api`
- `dotnet test`
- `cd web && npm run dev`

## Working agreement

- Plan before coding on any new phase; wait for my approval
- After each phase: build, run tests, and tell me exactly how to verify it manually
- If something in the PRD is ambiguous, ask me; don't silently decide
- Don't add features outside the PRD's MVP slice (section 7) unless I ask

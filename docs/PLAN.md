# Build Plan

Phased build order from the approved implementation plan (2026-10-03). Each phase is independently demoable. Marked done with a one-line note on what shipped and any deviation from the original plan. See [`ARCHITECTURE.md`](ARCHITECTURE.md) for current-state design and [`DECISIONS.md`](DECISIONS.md) for the reasoning behind specific choices.

| Phase | Scope | Demo criterion | Status |
|---|---|---|---|
| 0 | Solution/projects, docker-compose Postgres, EF Core + initial migration, health endpoint, Swagger, Vite+TS+Tailwind placeholder hitting `/health`, `.gitignore`, README, living docs, first commit | `dotnet build` + `npm run dev` both work; placeholder page shows health status | **Done** — see note below |
| 1 | Domain entities, DbContext, migrations, seed data (users, teams, 2 templates) | Migrations apply; seeded data visible via a basic read endpoint | Not started |
| 2 | Auth: register/login, JWT, role/team/claims, policies | curl/Postman login → JWT → call a protected endpoint | Not started |
| 3 | Job template + Job CRUD (Fixed + Manual only, no execution) | Create a Fixed and a Manual job via API; see both in dashboard list | Not started |
| 4 | Quartz + `IJobQueue` + worker skeleton, stub (no-op) pipeline | Schedule a Fixed job 1 min out; watch it auto-transition to Completed | Not started |
| 5 | Real step handlers: Download (finance stub), Calculate (rule engine), Email (Gmail/MailKit) | Manual-kickoff job runs full pipeline; real email arrives | Not started |
| 6 | Failure + retry path, idempotency verification | Inject a failure → auto-retry per policy → manual Retry → confirm no double email | Not started |
| 7 | Manual action / approval workflow, manual-action queue view | Approval-required job reaches `NeedsManualAction`; single approver resolves it | Not started |
| 8 | Event-based chaining + follow-up chaser | Job completion fires dependent job; unresolved approval fires a chaser on a demo-shortened timer | Not started |
| 9 | Frontend buildout (trails 3-8 or runs alongside) | Dashboard, job detail, manual-action queue, creation forms usable end-to-end | Not started |
| 10 *(stretch)* | Recurrent type, trigger-based webhook, CSV export, notification-preference enforcement | Only if time allows — not required for PRD §7's bar | Not started |

## Phase 0 note

Shipped as planned. One deviation: PostgreSQL is mapped to host port **5433** instead of 5432 — a pre-existing native Postgres install on the dev machine already owns 5432 (see `DECISIONS.md`). The DbContext currently holds only a trivial `SystemInfo` marker table (no real domain entities yet) to prove the migration pipeline ahead of Phase 1.

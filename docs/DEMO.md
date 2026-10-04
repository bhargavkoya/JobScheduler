# 5-minute demo script

Logins (password `Passw0rd!`): `business@jobscheduler.local` (Business employee, can approve), `tech@jobscheduler.local`, `admin@jobscheduler.local`.

Setup: `docker compose up -d`, `dotnet run --project src/JobScheduler.Api`, `cd web && npm run dev`. The Development start-up migrates and seeds users, templates, rules and a demo chain. Gmail is optional: without credentials emails are logged instead of sent.

## 1. The chain (about 2 min)

1. Log in as business. The dashboard shows **Demo: Daily Reconciliation** (Manual) and **Demo: Follow-up after Reconciliation** (Event-based, waits on the first job).
2. Open the reconciliation job and click **Run now**. Run history shows the queue pipeline: download report (finance REST stub) -> calculate (rules "Total Position" and "Exposure Cap") -> send email.
3. When it completes, the follow-up job starts by itself and also completes. Talking point: the chain run is keyed by the upstream run id, so replays never double-send.

## 2. Idempotency and retries (about 1 min)

- Click **Run now** twice quickly: the `Idempotency-Key` makes the second click return the same run.
- Create a job from a template with rule "Variance vs Ledger" to see a calculation outcome change; failures auto-retry with exponential backoff, then **Retry** resumes after the last good step (nothing re-sent).

## 3. Scheduling (about 1 min)

- New job -> Recurrent -> Daily at a time one minute ahead (IST). It fires on its own and returns to Scheduled/Completed for the next day. Cancel it afterwards.
- Talking points: Quartz in-memory store, database is the source of truth, `StartupRecovery` rebuilds triggers after a restart.

## 4. Approval and access (about 1 min)

- Create **Capital Allocation Approval** (approver `business@jobscheduler.local`), run it, then use the **Manual actions** tab to approve. Only the named approver or an admin can decide, once.
- Log in as tech to show claim-based access (retry yes, approve no) and team visibility.

## 5. Export

- Open any job -> **Export CSV** downloads its run history (IST times, formula-safe cells).

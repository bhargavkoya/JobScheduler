# Manual testing guide

Step-by-step scenarios you can run by hand, with the result to expect for each. For the 5-minute screen-share script see [`DEMO.md`](DEMO.md); for how things work see [`ARCHITECTURE.md`](ARCHITECTURE.md). Sample files for the file reconciliation scenarios are in [`samples/`](samples/README.md).

Contents: [1 Start the app](#1-start-the-app) · [2 Accounts](#2-accounts) · [3 Where to look](#3-where-to-look) · [4 Five-minute smoke test](#4-five-minute-smoke-test) · [5 Daily Reconciliation Report](#5-template-daily-reconciliation-report) · [6 File Reconciliation](#6-template-file-reconciliation) · [7 Capital Allocation Approval](#7-template-capital-allocation-approval) · [8 Follow-up Chaser](#8-template-follow-up-chaser) · [9 Schedule types](#9-schedule-types) · [10 Cross-cutting](#10-cross-cutting-checks) · [11 Automated tests](#11-automated-tests) · [12 Troubleshooting](#12-troubleshooting) · [13 Known gaps](#13-known-gaps)

## 1. Start the app

```powershell
docker compose up -d                              # Postgres on port 5433
dotnet run --project src/JobScheduler.Api         # API on http://localhost:5031
cd web; npm install; npm run dev                  # web on http://localhost:5173
```

- Restart the API after pulling new code. In Development it applies migrations and seeds users, templates, rules and a demo chain on start-up.
- The secrets (connection string, optional Gmail app password) are described in the README.
- **All times in the app are IST (24-hour).**

## 2. Accounts

All seeded accounts use the password **`Passw0rd!`** (capital P, a zero, exclamation mark). They exist in Development only.

| Email | Role / team | Use it for |
|---|---|---|
| `business@jobscheduler.local` | Employee, Business | Main tester: creates and runs jobs, and holds the `jobs.approve` permission. **Cannot retry** a failed job and sees only Business jobs. |
| `tech@jobscheduler.local` | Employee, Technical | Holds `jobs.retry` and observes the Business team (can read its jobs). Cannot approve; cannot change a job it does not own. Use it for **Retry**. |
| `admin@jobscheduler.local` | Admin | Holds every permission: retry, approve, view all teams. Also approves catalog templates and manages rules and users. |

So **Retry** (a Failed job) is done as `tech@` or `admin@`, not `business@`: the button is not shown to a user without `jobs.retry`.

To get a user who truly cannot see Business jobs: register one on the login page (password 8+ characters), then as admin move them to Technical with no observer access (see the PowerShell helper below, `PUT /admin/users/{id}/team` with `{ "primaryTeam": "Technical", "role": "Employee", "observerTeams": [] }`).

## 3. Where to look

- **Web UI** (http://localhost:5173): tabs Jobs, Manual actions, Catalog, Notifications, My access. Click a job row for its detail, run history and exports.
- **API console** (the terminal running `dotnet run`): without Gmail credentials every email is logged as `STUB EMAIL to <address>: <subject>` instead of sent. Only the recipient and subject are logged; the body is not. Other log lines to know: `Email to ... skipped: ... notifications are switched off` (a muted event) and `STUB EMAIL suppressed (already sent ...)` (a duplicate send).
- **Swagger** (http://localhost:5031/swagger) lists the endpoints but has **no Authorize button**, so it cannot call protected endpoints. Use the PowerShell helper below for admin and API calls.
- **Run history** on the job detail shows each step, its attempt number and its output. The output is one truncated line; hover it for the full text.

### PowerShell helper for API calls

```powershell
$api = 'http://localhost:5031'
function Login($email) {
  (Invoke-RestMethod -Method Post -Uri "$api/auth/login" -ContentType 'application/json' `
    -Body (@{ email = $email; password = 'Passw0rd!' } | ConvertTo-Json)).token
}
$admin = @{ Authorization = "Bearer $(Login 'admin@jobscheduler.local')" }
$biz   = @{ Authorization = "Bearer $(Login 'business@jobscheduler.local')" }

Invoke-RestMethod -Uri "$api/admin/rules" -Headers $admin        # list calculation rules
Invoke-RestMethod -Uri "$api/admin/users" -Headers $admin        # list users
Invoke-RestMethod -Method Post -Uri "$api/admin/email/test" -Headers $admin   # needs Gmail; 409 if not configured
```

## 4. Five-minute smoke test

Log in as `business@jobscheduler.local`.

1. **Jobs** tab shows the seeded jobs (the "Demo: ..." pair) and a summary strip of counts.
2. Create a Manual job from **Daily Reconciliation Report** (section 5), **Run now**, and see it go In progress, then Completed, with three green steps.
3. Create a **File Reconciliation** job from the sample files (section 6) and see 52 of 62 records pass on the first rule.
4. Create a **Capital Allocation Approval** job (section 7), run it, and approve it from **Manual actions**.
5. Log in as `tech@jobscheduler.local`: the Business jobs are visible, but approving is not possible.

If these work, the main paths are healthy.

## 5. Template: Daily Reconciliation Report

Pulls a report from the finance app and runs named calculation rules on it. **The finance app is a built-in stub** (`/mock-finance`) that always returns the same three rows (1250.50, -300.00, 98.25), so the total is **1048.75**. The report name you type is ignored by the stub.

Fields: Report name, Result recipient, Tolerance amount, Calculation rules (comma-separated rule names).

Seeded rules (data, editable by an admin):

| Rule | Type | Check | Result on the stub total 1048.75 |
|---|---|---|---|
| Total Position | SumAmount | reports the sum | 1048.75, always passes |
| Exposure Cap | Threshold | total within -5000 .. 5000 | pass |
| Variance vs Ledger | Variance | within 1% of 1000 | **fail** (about 4.9% off) |

### Scenarios

| # | Steps | Expect |
|---|---|---|
| 5.1 Happy path | New job, template Daily Reconciliation Report, Manual kickoff, rules `Total Position, Exposure Cap`, any report name, your email, tolerance 100. **Run now**. | Steps Download report, Calculate, Send email all succeed. Job Completed. Hover the Calculate output: "2 of 2 rules passed". Console: a `STUB EMAIL ... completed` line. |
| 5.2 A rule fails | Same, with rules `Total Position, Variance vs Ledger`. | Still Completed (a failing rule is a result, not an error). Calculate output shows `FAIL Variance vs Ledger`. |
| 5.3 Unknown rule | Rules `Nope`. Run now. | Calculate step fails ("Calculation rule 'Nope' does not exist or is inactive."), auto-retries, then the job is Failed. Fix by creating a new job with a valid rule name. |
| 5.4 Rules are data | As admin, change Exposure Cap's max (snippet below), then create a new Manual job with rules `Exposure Cap` and run it. | Now fails (1048.75 is above 1000). **Restore max to 5000 afterwards.** |
| 5.5 Retries and resume | Stop the API. Set `FinanceStub__FailFirstAttempts=2` (PowerShell: `$env:FinanceStub__FailFirstAttempts = '2'`), start it again. Create a job with **Auto retries 1** and **Backoff 5**, run it. | Attempt 1 fails at Download report (finance app returns 503), one auto-retry after 5 s also fails, the run and job become **Failed** with the 503 message. Log in as **`tech@` or `admin@`** (business has no retry permission) and click **Retry**: the third call succeeds, the run resumes and completes, and the email is sent **once**. Unset the variable afterwards. |

Snippet for 5.4:

```powershell
$cap = Invoke-RestMethod -Uri "$api/admin/rules" -Headers $admin | Where-Object name -eq 'Exposure Cap'
$body = @{ name = $cap.name; description = $cap.description; type = $cap.type
           parameters = @{ min = '-5000'; max = '1000' }; isActive = $true } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri "$api/admin/rules/$($cap.id)" -Headers $admin -ContentType 'application/json' -Body $body
```

## 6. Template: File Reconciliation

Compares CSV files you upload, record by record. Nothing about the files is hardcoded. Full explanation in the README; expected results for the sample data are in [`samples/README.md`](samples/README.md).

### Setup (every scenario)

New job, template **File Reconciliation**, schedule Manual, your email. **Choose CSV files** and select the three files in `docs/samples/`. Rename them (the name field next to each file) to `trades`, `custodian` and `ledger`. The columns are `TradeId`, `GrossCashAmount`, `Commission`, `Tax`, `Quantity`, `Price` in trades; `ClientTradeRef`, `CashAmount`, `SettledQuantity` in custodian; `TradeId`, `DebitAmount`, `CreditAmount` in ledger.

### Rules between two files

| # | Left → right file | Match on | Left side | Must | Right side | Tolerance | Expected |
|---|---|---|---|---|---|---|---|
| 6.1 | trades → custodian | `TradeId` = `ClientTradeRef` | `trades.GrossCashAmount - trades.Commission - trades.Tax` | `==` | `custodian.CashAmount` | 0 | 62 checks, **52 pass**, 10 fail |
| 6.2 | same as 6.1 | same | same | `==` | same | 0.05 amount | **54 pass**, 8 fail |
| 6.3 | same as 6.1 | same | same | `==` | same | 0.1 percent | **56 pass**, 6 fail |
| 6.4 | trades → custodian | `TradeId` = `ClientTradeRef` | `trades.Quantity` | `==` | `custodian.SettledQuantity` | 0 | **55 pass**, 7 fail |
| 6.5 | custodian → ledger | `ClientTradeRef` = `TradeId` | `custodian.CashAmount` | `==` | `ledger.DebitAmount - ledger.CreditAmount` | 0.05 | 63 checks, **48 pass**, 15 fail |
| 6.6 | same as 6.5 | same | same | `==` | same | 0 | **45 pass**, 18 fail |

The failures are the planted problems: amounts that differ, records missing from either file, and ledger T-100020 posted twice (reported as a duplicate key). The exact lists are in `samples/README.md`. The number of checks includes unmatched records, which is why 60 trades give 62 checks.

### Rules inside one file (pick the same file on both sides)

| # | File | Left side | Must | Right side | Expected |
|---|---|---|---|---|---|
| 6.7 | trades | `trades.Commission` | `<` | `trades.Tax` | all 60 pass |
| 6.8 | ledger | `ledger.DebitAmount * ledger.CreditAmount` | `==` | `0` | all 60 pass |
| 6.9 | ledger | `ledger.DebitAmount` | `>` | `0` | 28 pass, 32 fail (the credit lines) |
| 6.10 | trades | `trades.Quantity * trades.Price` | `==` (tolerance 0.01) | `trades.GrossCashAmount` | 28 pass, 32 fail. Expected: BUY trades store cash as negative. |
| 6.11 | trades | `trades.GrossCashAmount / (trades.Quantity - trades.Quantity)` | `==` | `1` | all 60 fail with "Division by zero in the expression." |

### Rules the form must refuse (rule card turns amber, Create stays disabled)

`trades.Gros - trades.Tax` (unknown column, real ones listed) · `amount + 1` (needs `alias.Column`) · `trades.Tax +` (ends too early) · `(trades.Tax` (missing parenthesis) · `trades.Tax ** 2` · `custodian.CashAmount` in a rule that only uses trades.

### A different schema, with spaces in column names

Save as `bank.csv` and `ap.csv`:

```
Narrative Ref,Debit Amt,Bank Charges
INV-1,100.00,1.50
INV-2,50.00,0.00
INV-3,75.25,0.75
```
```
Invoice No,Invoice Total
INV-1,101.50
INV-2,55.00
INV-4,10.00
```

Rule: `bank` → `ap`, match `Narrative Ref` = `Invoice No`, left side `bank.[Debit Amt] + bank.[Bank Charges]`, `==`, right side `ap.[Invoice Total]`. Expected: 4 checks, **1 pass** (INV-1), 3 fail (INV-2 differs by -5.00, INV-3 missing in `ap`, INV-4 missing in `bank`). Square brackets are needed for names with spaces.

### Other checks

| # | Steps | Expect |
|---|---|---|
| 6.12 Results view | Open the job, **Show record results**. Click **Failed only**, then a rule card. | Totals (a job with rules 6.1 at tolerance 0 and 6.5 together: 125 checks, 100 pass, 25 fail), filters, 50 rows per page, a reason on every failure. |
| 6.13 Export | **Export CSV** and **Export PDF** under the results, with and without filters. | The export holds exactly the filtered records (not just the page). The buttons at the top of the job ("Run history CSV/PDF") export the run history instead. |
| 6.14 Replace a file | On a **Recurrent** or **Trigger-based** job: **Replace file** on `ledger` with another CSV, then run again. | New file listed, old one kept for audit, next run uses the new one and keeps its own results. A file missing a column the rules use is refused. |
| 6.15 Manual jobs run once | After a Manual file job completes, there is no Run now. | By design: create a new job, or use Recurrent / Trigger-based for repeats. |
| 6.16 Bad upload | Upload a `.txt`, or a CSV whose rows have different field counts. | A clear error naming the problem (and the row for a ragged file). |

## 7. Template: Capital Allocation Approval

Single-approver, manual-action flow. Manual kickoff only. Fields: Portfolio, Approver email, Amount, and optional "Remind approver if no action after (minutes)".

| # | Steps | Expect |
|---|---|---|
| 7.1 Approve | As business: new job (Portfolio "Growth Fund", **Approver email `business@jobscheduler.local`**, Amount 250000). Run now. Open **Manual actions**. **Approve**. | After the run the job is **Needs manual action** (not Completed) and listed in the queue (the tab shows a count badge). Console: `STUB EMAIL ... Approval needed`. Approving completes the job. |
| 7.2 Decide only once | After approving, try to decide again (for example via the API, `POST /jobs/{id}/approve`). | Refused with 409. |
| 7.3 Reject and retry | New job, run, **Reject** with a comment (a comment is required). Then log in as `tech@` or `admin@` and click **Retry**. | The job becomes **Failed**. **Retry** re-runs it (same run, nothing re-sent that already succeeded) and it waits for approval again. |
| 7.4 Reminder | New job with the reminder set to `1`, run, do nothing for a minute. | One reminder email to the approver ("is still waiting for your approval"), once. If you decide before the minute is up, none is sent. |
| 7.5 Approver must be allowed | Approver email `tech@jobscheduler.local`. | Creation refused: "does not have permission to approve jobs". An unknown email is refused too ("not a registered user"). |
| 7.6 Who can decide | Log in as `tech@` and open the Manual actions queue. | If the job is listed for you, its Approve and Reject buttons are disabled. Only the named approver or an admin can decide. |
| 7.7 At risk | Leave an approval waiting for more than 15 minutes (`AtRisk:ThresholdMinutes`). | An orange **At risk** badge on the job and in the summary strip. |
| 7.8 Muting | Notifications tab, switch off "Approval requested" and "Follow-up reminders", repeat 7.1 / 7.4. | The emails are skipped (`Email to ... skipped: ... notifications are switched off` in the console). |

## 8. Template: Follow-up Chaser

Emails an assignee when the job runs. Fields: Assignee email, Message, "Remind again after (hours)". Schedules: Fixed, Manual, Event-based.

> **Known gap:** the **Message** and **Remind again after** fields are saved but not used. The assignee gets the standard "<job name> completed" email without your message, and no second reminder is sent. The working chasing behaviour is the approval reminder in 7.4. See [13 Known gaps](#13-known-gaps).

| # | Steps | Expect |
|---|---|---|
| 8.1 Manual | New job, template Follow-up Chaser, Manual, assignee email, any message. Run now. | Steps succeed, the email step reports "Email sent to <assignee>", console `STUB EMAIL ... completed`. |
| 8.2 Chained after an approval | Create a Capital Allocation Approval job. Create a chaser with schedule **Event-based**, "Start when this job completes" set to the approval job. Run the approval job. | The chaser stays Scheduled while the approval is pending. After you **approve**, it starts by itself and completes. |
| 8.3 Fixed time | Fixed, a time 1 to 2 minutes ahead (IST). | Fires by itself and completes. |
| 8.4 Seeded chain | Run the seeded **Demo: Daily Reconciliation** (if it is still Scheduled). | **Demo: Follow-up after Reconciliation** starts by itself when it completes. |

## 9. Schedule types

| Type | How to test | Expect |
|---|---|---|
| **Manual** | Create, then Run now. | One run. Afterwards the job is Completed and cannot be run again. |
| **Fixed** | Run at (IST) 1 to 2 minutes ahead. | Fires once, by itself. A time in the past is refused when creating. If the API was down at the due time, it fires immediately on the next start-up. |
| **Recurrent** | Daily / Weekly / Monthly (day 1 to 28) at a time. | Fires at the **next** occurrence of that time, never immediately: "Daily at 18:08" created at 18:08:16 first fires tomorrow. For a quick test choose a time 2 to 3 minutes ahead. It skips a firing while the previous run is running, waiting on approval or Failed. A firing missed while the API is down is not replayed. Cancel stops it. |
| **Event-based** | Pick another job that starts it. | Starts once per upstream run, when that run completes (after approval for approval jobs). |
| **Trigger-based (webhook)** | Create the job and **copy the token shown once**. Call the webhook (below). | 202 and a run. The request body appears on the run. Wrong token, unknown job or non-webhook job all return the same 401. |

Webhook call and idempotency (PowerShell):

```powershell
$hook = @{ 'X-Webhook-Token' = '<token>'; 'Idempotency-Key' = 'test-1' }
Invoke-RestMethod -Method Post -Uri "$api/webhooks/jobs/<job-id>" -Headers $hook -ContentType 'application/json' -Body '{"event":"report-ready"}'
```

- Repeat the call with the **same** `Idempotency-Key`: no second run. Use a new key, or none, and every call starts a run.
- Manual run idempotency: the web button sends a fresh key on every click, so a double click cannot be used to test it. Use the API:

```powershell
$h = @{ Authorization = "Bearer $(Login 'business@jobscheduler.local')"; 'Idempotency-Key' = 'same-key-1' }
Invoke-RestMethod -Method Post -Uri "$api/jobs/<job-id>/run" -Headers $h   # run it twice: the second returns the same run id
```

## 10. Cross-cutting checks

| Area | Steps | Expect |
|---|---|---|
| Catalog approval | As admin, **Catalog** tab: "Liquidity Report (Draft)" is "Awaiting approval". As business it is not offered. Admin clicks **Approve**. | After approval business can create jobs from it. |
| Cancel | Cancel a Scheduled job. | Status Cancelled; its schedule stops. A running job cannot be cancelled. |
| Run history export | Job detail, **Export CSV** / **Export PDF** (labelled "Run history CSV/PDF" on file jobs). | Downloads the run history with IST times. |
| Filters | Jobs tab: status, team, type, dates, "My jobs", at-risk. | The list narrows; the summary strip keeps the full counts. |
| Team visibility | As `tech@`, open Business jobs. As the outsider user (section 2), the same jobs. | Observer sees them read-only; the outsider gets nothing (404 on direct links). |
| Notification preferences | Notifications tab: switch off "Job completed", run any job. | Console shows `Email to ... skipped: ... notifications are switched off` instead of an email. |
| Gmail | Set the Gmail secrets (README), restart, then `POST /admin/email/test` as admin. | An email to the admin's own address. 409 if not configured, 502 with Gmail's error if it rejects the login. |

## 11. Automated tests

```powershell
dotnet test                              # unit tests (database mocked); a few database tests show as skipped
cd web; npm test                         # rule-builder logic
cd web; npm run build                    # type-check and build
cd web; npm run lint
```

Database tests are opt-in. They create and drop a throwaway database on your Postgres:

```powershell
$cs = (dotnet user-secrets list --project src/JobScheduler.Api | Select-String 'ConnectionStrings:JobSchedulerDb').ToString().Split('=', 2)[1].Trim()
$env:JOBSCHEDULER_TEST_PG = $cs
dotnet test
Remove-Item Env:JOBSCHEDULER_TEST_PG
```

## 12. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Browser console: `ERR_INTERNET_DISCONNECTED` | The browser thinks it is offline. In DevTools, Network tab, set throttling to "No throttling" (not "Offline"), then Ctrl+Shift+R. |
| `ERR_CONNECTION_REFUSED` | The API is not running on port 5031. Start it. |
| Login fails | Password is `Passw0rd!` (zero, not the letter O). The API must be running, and demo users are created when it starts in Development. |
| A Recurrent job "isn't running" | It fires at the next occurrence of its time (IST), not at creation. The API must be running at that moment: a firing that happens while the API is down is **skipped, not replayed** (the scheduler is in memory and is rebuilt from "now" on start-up). |
| No **Run now** button | Only Manual jobs in Scheduled status have it. A Completed Manual job cannot be re-run ("only jobs that are ready (Scheduled) can be run"). |
| Page shows new fields missing or 500s after pulling code | Restart the API so it applies migrations. |
| Swagger returns 401 | Swagger has no Authorize button. Use the PowerShell helper. |
| No emails | Expected without Gmail credentials: look for `STUB EMAIL` lines in the API console. |
| Port in use | API 5031, web 5173 (the API only allows CORS from 5173), Postgres 5433. |
| Scheduled job did not start after an API restart | Triggers are rebuilt from the database on start-up, so give it a few seconds and check the console for the `Startup recovery: N job(s) rescheduled` line. A **Fixed** job whose time passed while the API was down fires immediately on start-up; a **Recurrent** firing missed while down is skipped. |
| Logged out after restarting the API | With no `Jwt:Key` configured, Development generates a random signing key on every start, so old tokens stop working. Log in again, or set `Jwt:Key` (32+ characters) as a user-secret. |

## 13. Known gaps

- Follow-up Chaser ignores **Message** and **Remind again after (hours)** (section 8).
- Manual jobs run once, so reconciling new files needs a new job or a Recurrent / Trigger-based one.
- File reconciliation compares numbers only: no text or date rules, no aggregates, CSV only.
- Swagger cannot authenticate; use PowerShell or the web app.
- Gmail delivery has not been verified with a real account.

# Portal reporting: Insights, standard reports, regulatory reports and data extracts

**Status:** draft for review, 2026-10-21. Open questions are in §9.

**Builds on:**
- `portal-navigation.md` §5.1, which gives each module an Insights view and puts cross-domain reporting under Reports;
- `portal-search.md`, which keeps record search behind selective criteria.

This document sets how people who need reports get at the data, and what each module's reports are.

## 1. Who needs reports, and for what

| Who | Needs | Example |
|---|---|---|
| Claims supervisor / manager | Where the work stands, and who is behind | Pended inventory by age and queue; throughput by examiner |
| Claims operations / adjustment analyst | The records a change touched, and what it cost | Claims repriced by a fee schedule correction, with the dollar difference |
| Finance / treasury | What was paid, what is outstanding, what reconciles | Check register; outstanding checks over 90 days; 835 vs payment reconciliation |
| Provider network / contracting | Network shape and contract health | Roster by network; contracts expiring in 90 days; adequacy by county |
| UM and A&G managers | Timeliness and outcomes | Decisions within timeframe; overturn rate |
| Compliance | Regulator-ready figures and samples | State prompt-pay report; CMS-0057-F metrics; timeliness universes |
| Executives | The plan's health | Medical loss ratio, cost trend, membership |
| Plan analysts / data team | Everything, in their own tools | Nightly extract to the plan's warehouse |

These are four different shapes of need. One reporting screen can't serve them all, so there are four layers.

## 2. Four layers

| Layer | Where | What it returns | Gate | Exists today |
|---|---|---|---|---|
| **1. Insights** | `/{module}/insights` | Aggregates (counts, rates, totals, trends). Each number drills to a list | Module read permission | Claims, Authorizations, Appeals |
| **2. Standard reports** | `/{module}/reports` | Record-level rows for set parameters, on screen and as CSV / Excel | `reports:{area}` | No catalog. Early tabs on `/reports` (claims summary, payment summary, eligibility, prior auth, provider performance) |
| **3. Cross-domain and regulatory reports** | `/reports` (Operations) | Plan-wide and regulator formats | `reports:{area}`, `reports:compliance` | CMS-0057-F console (Compliance) |
| **4. Data extracts** | Configured per tenant, outside the portal | Full datasets for the plan's warehouse or BI tool | Tenant admin sets them up; data-use agreement | FHIR bulk `$export` in fhir-service |

**Each module opens on three views: List | Insights | Reports.** Today's `ModuleViewSwitch` (List / Insights) gains a third option. Reports belong in the module because the people who run the claims reports are the people who work claims.

## 3. Rules for every report

1. **Reports are not searches.**
   - A report has defined parameters (period, LOB, provider, status, queue), runs on the server and is gated by a report permission.
   - The always-on search criteria rule (`portal-search.md` §3.5) governs interactive record search, not reports.
   - A wide report is legitimate when the role needs it; a wide search is browsing.
2. **One definition per metric** (`portal-navigation.md` §5.1 rule 2).
   - "Auto-adjudication rate" or "decided within timeframe" is defined once, server-side.
   - Insights, standard reports, Home tiles and regulator files use that one definition, so the numbers always agree.
3. **Every run is reproducible.** A run records:
   - its parameters;
   - the data as-of time;
   - who ran it.

   "Paid claims register, September, run 10/2 08:14 by J. Ortiz" can be re-run and checked.
4. **Every run is audited.**
   - Record-level runs log the record IDs returned, as record search does.
   - Aggregate runs log the report and parameters.
   - Downloads are logged separately from runs.
5. **Minimum necessary columns.**
   - Each report's columns are fixed by its definition.
   - Member-level columns (name, DOB, member ID) appear only in reports whose job needs them.
   - A finance register shows payee and amount, not the patient.
   - Where a member must be identifiable but not named (e.g. a compliance sample), the report shows the member ID only.
6. **Large runs are jobs.**
   - Anything over 5,000 rows, or slower than 10 seconds, runs in the background.
   - The user is notified when it finishes.
   - The output is stored encrypted and expires after 7 days.
7. **Scheduling sends a link, not data.**
   - A schedule runs a report with relative parameters ("last month") and emails a link.
   - Only recipients holding the report's permission can open the link.
8. **Scope.** Tenant always applies. Where a user's access is limited to certain LOBs or states, every report respects the same limits as record reads.

## 4. Standard reports by module

The **Exists** column says what can be built on today: a source endpoint, an existing Insights figure, or nothing yet.

### 4.1 Claims — `reports:claims`

**Insights:**
- **Exists (`ClaimsInsights`):** open inventory by age and status, auto-adjudication rate, outcomes by status, pend reasons, top denial reasons, receipt-to-adjudication median, line of business.
- **To add:** prompt-pay interest, first-pass rate by provider.

**Standard reports**

| Report | For | Parameters | Rows | Exists |
|---|---|---|---|---|
| Pended inventory | Supervisor | Queue, examiner, pend code, age band, LOB | Pended claims with age, pend codes, assignee, prompt-pay due date | Pend codes and received dates on claims; assignee in work-management |
| Throughput | Supervisor | Period, examiner, queue | Per examiner: finished, pended, overturned overrides, average handle time | Partly (work-management events) |
| Paid claims register | Finance, operations | Paid date range, LOB, provider/TIN | Claim, provider, DOS, billed, allowed, paid, check/EFT | Claims + payment-service |
| Denials | Operations, provider relations | Period, CARC/RARC, provider, LOB | Denied claims or lines with reason codes | Denial codes on claims |
| Prompt-pay | Compliance | Period, state, LOB | Clean claims paid late, days late, interest paid | Needs clean dates and interest recorded on claims |
| Duplicate review | Operations, SIU | Period, provider | Claims flagged as possible duplicates and their dispositions | Duplicate edits in the pipeline |
| High-dollar claims | Finance, medical management | Paid or billed over a threshold, period | Claim, member ID, provider, amounts, diagnosis | Claims |
| Claims by source | EDI operations | Period, trading partner, channel | Counts and rejects by source and file | Import transactions |

### 4.2 Adjustments and reprocessing — `reports:claims`

**Today:** an adjustment is created one claim at a time (`POST /claims/{id}/adjustments`, `claim-adjustment-workflow.md`), and reversals run in batches (`claim-reversal-run.md`). There is no "project" that groups the adjustments made for one reason.

**Proposal: an adjustment project.**
- **What it is:** a named, reasoned set of claims to reprocess, e.g. "Fee schedule correction, cardiology, eff. 7/1".
- **How claims get in:** from a claims work-list search (`portal-search.md` §2) or from a fee schedule impact analysis (4.4).
- **What it carries:** the claims, who approved it, and each claim's before and after amounts.

| Report | For | Rows |
|---|---|---|
| Project summary | Operations manager, finance | Claims in scope, done, failed; total paid before vs after; recoupments raised; interest |
| Project detail | Analyst, provider relations | Each claim: old and new allowed and paid, difference, new remit, status |
| Adjustments by reason | Operations, compliance | All adjustments in a period, grouped by reason code and source (provider-requested, appeal, audit, config fix) |

### 4.3 Payments and disbursements — `reports:financial`

**Insights (new):** paid by period, payment method mix, recoupments, held payments, outstanding checks.

| Report | For | Parameters | Rows | Exists |
|---|---|---|---|---|
| Check register | Treasury | Issue date range, bank account, LOB | Check #, payee, TIN, amount, issue date, status | payment-service |
| EFT register | Treasury | Settlement date range | Trace #, payee, amount, settlement date, status | payment-service |
| Outstanding and stale-dated checks | Treasury | Older than N days, state | Uncleared checks; escheatment candidates by state | Needs cleared dates (not stored today) |
| Voids, stops and reissues | Treasury, audit | Period | Original and replacement payment, reason, approver | Partly |
| 835 reconciliation | Finance, EDI ops | Payment run, trading partner | Each payment vs its ERA envelope; mismatches | ERA envelopes by run |
| Payment run summary | Finance approver | Run | Totals by LOB, payee type and method; holds; exceptions | Payment runs |
| Provider receivables and offsets | Recovery | Provider/TIN, status | Overpayments, offsets applied, balance | Receivables endpoint |
| 1099 | Tax | Tax year, TIN | Reportable payments by TIN | Needs year totals by TIN |

### 4.4 Fee schedules — `reports:claims` to view, `config:write` to publish

**A fee schedule update is a change with consequences, not a report.** It belongs in Configuration, with its impact shown before it goes live.

**Today:** fee schedules load through the Pricing API admin endpoints (RBRVS, OPPS, DRG uploads), plans carry a default fee schedule, and contracts resolve rates through rate resolution.

**Proposal: before publishing a new or changed schedule, the impact analysis shows:**
- **Who uses it:** contracts and providers that reference it, directly or through a plan default.
- **What changes:** rate differences by code (old vs new, % change), largest changes first.
- **Paid claims that would reprice:** claims paid under the old rates since the new effective date (when retroactive). It shows count, total paid now, total paid after, and the difference by provider.
- **Forward estimate:** last 12 months' volume at old vs new rates.

**After publishing,** the retroactive claims become an adjustment project (4.2) in one step, so the analysis and the reprocessing describe the same claims.

### 4.5 Providers — `reports:providers` (new)

**Insights (new):** network size and changes, credentialing due, top billers, denial rate by provider, records requests outstanding.

| Report | For | Rows | Exists |
|---|---|---|---|
| Network roster | Network management, regulators | Providers by network, plan and LOB, with locations, specialty and panel status | Network roster endpoint |
| Network adequacy | Network management, compliance | Count and time/distance coverage by county and specialty against state standards | Needs geocoded locations |
| Credentialing due | Credentialing | Recredential and license expirations in the next N days | Credentialing data |
| Changes | Provider data management | Adds, terms and demographic changes in a period | Provider versions |
| Top billers and outliers | Provider relations, SIU | Billed, paid and denial rate by provider vs specialty peers | Claims aggregates |
| Sanctions | Compliance | Providers matched against OIG / SAM lists, with resolution | Integrity flags |

### 4.6 Provider contracts and capitation — `reports:financial`, `contracts:read`

| Report | For | Rows | Exists |
|---|---|---|---|
| Contracts expiring | Contract managers | Contracts ending in the next N days, auto-renew flag, owner | Contract dates |
| Contracts by payment method | Network finance | Contracts and providers by method (FFS, % of Medicare, case rate, capitation, value-based) | Contracts |
| Contracts using a fee schedule | Configuration | Contracts referencing a given schedule | Needs the reference stored searchably |
| Capitation statements | Capitation analyst, finance | Statements by period, provider and LOB, with member months and retro adjustments | Capitation statements |
| Capitation reconciliation | Finance | Member months paid vs enrollment, retro differences | Partly |

### 4.7 Authorizations and appeals — `reports:um`, `reports:compliance`

**Insights (exist):** Authorization Insights (CMS-0057-F figures, queue, denial reasons, unused auths) and Appeals Insights.

| Report | For | Rows |
|---|---|---|
| Decision timeliness | UM manager, compliance | Requests by priority with received, decided, timeframe met |
| Pending by deadline | UM manager | Open requests by time left, reviewer |
| Denials and overturns | Medical director | Denials by reason, peer-to-peer outcome, overturned on appeal |
| A&G timeliness | A&G manager, compliance | Cases by level with received, acknowledged, resolved, within standard |
| Grievance categories | Quality, compliance | Grievances by category and resolution |

### 4.8 Members and enrollment — `reports:enrollment` (new)

| Report | For | Rows |
|---|---|---|
| Membership | Finance, executives | Member counts by LOB, plan and group, as of a date (aggregate) |
| Adds and terms | Enrollment, finance | Members added and termed in a period, with reason |
| 834 exceptions | Enrollment | Rejected or pended 834 transactions, with reason |
| PCP assignment gaps | Network, care management | Members without a PCP (counts; drill to list) |
| ID cards outstanding | Enrollment | Cards ordered and not produced |

### 4.9 Premium billing and AR — `reports:financial`

| Report | For | Rows | Exists |
|---|---|---|---|
| Aging | Finance, collections | Receivables by bucket (current, 30, 60, 90, 120+), by group or individual and LOB | AR aging endpoint (loads everything today; the portal page never loads it) |
| Billed vs collected | Finance | Invoiced, paid, outstanding by period and LOB | Invoices |
| Delinquency and grace periods | Collections | Accounts in grace, grace end dates, notices sent, terminations pending | Needs delinquency search |
| Cash posting exceptions | Cash application | Unidentified, short-paid and overpaid receipts | Remittance exceptions |
| EFT returns | Treasury | ACH returns by code, account and invoice | Needs EFT search |
| GL activity | Accounting | Entries by account and period, with source | GL entries (keyset paged; CSV exists) |
| Trial balance | Accounting | Balances by GL account for a period | AR balances |

## 5. Cross-domain and regulatory reports — `/reports`

These live under Reports in Operations, because no single module owns them:

- **Executive summary:** membership, claims paid, PMPM, MLR, inventory health, timeliness.
- **Medical loss ratio and cost trend:** paid claims and capitation vs premium, by LOB and period; PMPM by service category.
- **State reports:** prompt-pay, encounter submission status, state-specific quarterly formats, configured per tenant and state.
- **CMS reporting:** CMS-0057-F prior authorization metrics (the existing console), Medicare Advantage timeliness universes (ODAG, CDAG, grievances), MLR filing support.
- **Audit samples:** a random sample of records meeting a definition, with the sampling seed recorded, for internal or external auditors.

The current `/reports` tabs move into the matching module's Insights or Reports view as each module's view ships. `/reports` then holds only the cross-domain and regulatory set.

## 6. Data extracts for the plan's own analytics

Plans run their own BI tools and warehouses. CHO feeds them rather than replacing them:

- **FHIR bulk export** (`$export`, already in fhir-service) for standard resources.
- **Scheduled extracts:**
  - nightly or incremental, in flat files (CSV or Parquet), per domain;
  - delivered to the plan's storage (SFTP, S3, Azure Blob or GCS);
  - each file carries a manifest with row counts and as-of time.
- **Change feeds,** for plans that want near-real-time copies: domain events from the existing outboxes (`claim-event-outbox.md`, `appeals-event-outbox.md`), delivered to the plan's queue.

**Controls:**
- A tenant admin configures an extract.
- Each extract names its datasets and fields, and records the data-use agreement it falls under.
- Every delivery is logged with its manifest.

Extracts are where full-population, PHI-heavy analysis belongs, under the plan's own controls, not in interactive screens.

## 7. How it's built

### 7.1 Report definitions

Each report is a definition registered by its owning service:
- **id, name and module;**
- **permission;**
- **parameters:** typed, with defaults and allowed ranges;
- **columns:** fixed, with PHI classification per column;
- **query:** server-side, over the service's own store, using the shared metric definitions where it reports a metric;
- **row limit** for interactive runs.

The portal reads the catalog and renders a module's Reports view from it. Adding a report is a backend change only.

### 7.2 Runs and outputs

- **Interactive runs** return the first page to the screen, in the same `ResultsGrid` as search (`portal-search.md` §7.4).
- **Background runs** go to a report job queue. The output is written encrypted to the tenant's document store with a 7-day expiry. The user is notified (Home and email link).
- **Formats:** CSV and Excel. PDF only for regulator formats that require it.
- **Run records** (parameters, as-of time, user, row count, record IDs for record-level reports) go to the access log.

### 7.3 Schedules

- A schedule is a report, relative parameters, a frequency and recipients.
- Recipients must hold the report's permission when each run is delivered, not just when the schedule was created.
- A recipient who loses it is dropped, and the owner is told.

### 7.4 Permissions

These exist today:
- `reports:claims` — ClaimsSupervisor;
- `reports:financial` — Finance, FinanceApprover;
- `reports:compliance` — ComplianceOfficer.

Add:
- `reports:providers` — ProviderRelations;
- `reports:um` — UMCoordinator lead;
- `reports:enrollment` — EnrollmentSpecialist lead;
- `extracts:manage` — TenantAdmin.

## 8. Build order

1. **Report catalog and runner:** definitions, interactive and background runs, outputs, audit, the module Reports view. Start with three claims reports (pended inventory, paid claims register, denials) to prove the pattern.
2. **Payments:** check register, EFT register, payment run summary, 835 reconciliation.
3. **Adjustment projects** and their reports. Then the **fee schedule impact analysis,** which feeds them.
4. **Insights for Payments, Providers and Members** (Claims, Authorizations and Appeals exist).
5. **Provider and contract reports,** then **premium billing and AR** (after the AR aging fix and the delinquency and EFT searches).
6. **Schedules.**
7. **Cross-domain and regulatory reports** under `/reports`; the old tabs retire as their replacements ship.
8. **Data extracts** and change feeds, per tenant.

The supervisor reports in §4.1 (pended inventory, throughput) need an hourly
queue series that nothing writes today. That series, and the forecasting and
deadline-risk work built on it, are designed in
[workforce-optimization.md](workforce-optimization.md) §4; it should be built
once, in work-management-service, and read by both efforts rather than twice.

## 9. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | Should reports be a third module view (List / Insights / Reports), or one central catalog? | A module view, plus a filtered catalog on `/reports` that lists every report the user can run, so nothing is hard to find. |
| 2 | Can a user build ad hoc reports (pick columns and filters freely)? | Not in the portal. Ad hoc work belongs in the plan's BI tool over extracts. Portal reports stay defined, reviewed and audited. Revisit if plans without a BI team ask. |
| 3 | Should Excel exports keep member names and DOB? | Only for reports whose definition marks those columns as needed (rule 5). Downloads are logged with record IDs. |
| 4 | Who approves a mass adjustment project before it runs? | ClaimsSupervisor, plus FinanceApprover above a dollar threshold set per tenant. |
| 5 | Should the fee schedule impact analysis block publishing until reviewed? | Yes for retroactive changes; advisory for future-dated ones. |

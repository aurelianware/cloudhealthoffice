# Runbook: rated premium billing (premium-billing-service)

**Scope:** turning on rated billing for a tenant (invoice premiums from the rating
engine and the tenant's rate tables instead of coverage-service's stored premium),
running it, and fixing Draft invoices.
**Default:** off for every tenant. A tenant that is not configured is billed exactly
as before. Design: [premium-rated-billing.md](../architecture/premium-rated-billing.md).

> This changes what sponsors are billed. Load and check rate tables with two people:
> one saves them, the other compares them against the filed rates.

## 1. Before turning it on (per tenant)

1. **Deploy** premium-billing-service and member-service from the same release
   (premium billing calls member-service's `GET /api/v1/members/rating-census`).
   The service needs `MemberService:BaseUrl` (k8s ConfigMap `MemberService__BaseUrl`)
   and `member-service` in `ChoAuth:Outbound:Hosts` (in `appsettings.json`).
2. **Cosmos DB only:** create container `RateTables` with partition key `/tenantId` in
   the service's database. (Mongo creates the `RateTables` collection on first write.)
3. **Load rate tables** for every plan the tenant's groups are enrolled in, covering
   at least the retro window (default 3 months) before the first rated period:
   `POST /api/v1/rate-tables` (finance:write) with `{ "table": { ... }, "changeReason": "..." }`.
   Set `proration` (`Daily`, `HalfMonth`, `FullMonth`) from the plan's filing. For a
   mid-year rate revision set `ageDeterminationDate` to the plan-year start.
4. **Check member data** for every group: each subscriber and dependent needs a date
   of birth, dependents need `subscriberMemberId` and an X12 relationship code, and
   each dependent's coverage must be under the same plan as the subscriber's.
5. **Dry run** (recommended): turn the tenant on with `HoldForReview: true` (below) and
   run billing for the next period. Every invoice stays Draft. Compare each Draft's
   total with what the original pricing billed last month; read `ratingExceptions`.
   Void the Drafts you do not want to keep, or issue them.

## 2. Turning it on

Configuration (environment variables or `appsettings.Production.json`); the service
validates it at startup and refuses to start when it is invalid:

```json
"PremiumBilling": {
  "RatedBilling": {
    "Tenants": {
      "tenant-a": {
        "Enabled": true,
        "BillFormat": "ListBill",
        "MaxRetroMonths": 3,
        "EmployerContributionPercent": 0,
        "HoldForReview": false,
        "Groups": { "GRP-100": { "BillFormat": "Composite", "EmployerContributionPercent": 75 } }
      }
    }
  }
}
```

As environment variables: `PremiumBilling__RatedBilling__Tenants__tenant-a__Enabled=true`.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Price the tenant's invoices with the rating engine. |
| `BillFormat` | `ListBill` | `ListBill` (line per subscriber) or `Composite` (line per tier); per group under `Groups`. |
| `MaxRetroMonths` | `3` | How far back retro changes are reconciled (0–24). |
| `EmployerContributionPercent` | `0` | Employer share shown on each line (0–100); the total is the full premium. |
| `HoldForReview` | `false` | Keep every rated invoice in Draft until `POST /premium-invoices/{id}/issue`. |

**Turning it off** (`Enabled: false`) returns the tenant to the original pricing from
the next billing run. Rated invoices already issued stay as they are.

### Migration note

- **First rated period.** Turn the flag on before the billing run of the first period
  to be rated, and after the previous period's (unrated) invoices are issued. A period
  that already has an unrated invoice is left alone by rated billing
  (`UnratedInvoiceExists`); void it first if it must be re-billed rated.
- **No retro across the switch.** Rated billing reconciles only months billed by rated
  invoices. Retro adds, terms or rate corrections for months billed the original way
  must be adjusted by hand (a manual adjustment), as before.
- **Line shape changes.** Unrated invoices had one line per covered member; rated
  invoices have one line per subscriber (dependents are in the household's tier or
  age-rated premium). Reports that count lines as members should use `memberCount`.
- **Status filters on Cosmos DB.** `GET /premium-invoices?status=...`, the overdue list,
  the aging report and delinquency processing now match the stored status. On Cosmos
  DB they previously compared the stored string (`"voided"`) with the enum's number,
  which never matches, so these status filters did not work as intended there; expect
  their results to change (Mongo stores numbers and is unaffected).
- **Data.** No existing document is rewritten. New fields on invoices are optional and
  absent on unrated invoices (`pricingSource` defaults to `CoveragePremium`).

## 3. Each billing run

`POST /api/v1/billing-runs/execute` as usual. In the run result:

- `invoiceIds`: invoices created and issued by this run (counted in the totals);
- `draftInvoiceIds`: invoices left in Draft (not billed, not in the totals), with a
  warning naming the first exception;
- `unchangedInvoiceIds`: groups whose period was already invoiced; nothing changed.

Re-running a period is safe: issued invoices are never changed or duplicated, and
Drafts are recomputed in place.

## 4. Fixing a Draft

1. `GET /api/v1/premium-invoices/{id}`: read `ratingExceptions`.
2. Fix the cause:
   - `RATE_NOT_FOUND`: save the missing or extended rate table (a new version);
   - `RATE_TABLE_INVALID`: two current tables of the plan overlap; withdraw or correct one;
   - `ENROLLMENT_DATA`: fix the member or coverage record named in the message;
   - `ZERO_CHARGE`: check the rate table and the coverage dates;
   - `NO_BILLABLE_COVERAGE`: the group has no coverage for the period; void the Draft if that is right.
3. `POST /api/v1/premium-invoices/{id}/regenerate` (billing:run), or run billing for
   the period again. With no exceptions left the invoice is issued (unless the tenant
   holds for review; then `POST /api/v1/premium-invoices/{id}/issue`).

A Draft cannot be paid, sent, drafted by EFT or go delinquent. A remittance naming its
invoice number goes to the exceptions queue (`InvoiceClosed`) until it is issued.

## 5. Correcting rates

Save a new version of the table (`expectedCurrentVersion` = the version you read). The
next invoice of each affected group re-rates its invoiced months within the retro
window and bills or credits the difference as `RateChange` adjustments; issued invoices
are not changed. To find which rates an invoice line used:
`GET /api/v1/rate-tables/{rateTableId}/versions/{rateTableVersion}` (its `contentHash`
equals the line's `rateTableHash`).

## 6. Do not

- Run two billing runs of the same tenant at the same time (two periods could both
  reconcile the same earlier month).
- Delete a coverage record in coverage-service to end coverage: terminate it. A missing
  record reads as never covered and its invoiced months are credited back.
- Edit invoice documents in the database. Issued invoices are immutable by design.

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
   The service needs `MemberService:BaseUrl` (k8s ConfigMap `MemberService__BaseUrl`),
   `member-service` in `ChoAuth:Outbound:Hosts` (in `appsettings.json`), and its own
   service token (`ChoAuth:ServiceToken`, as for the sponsor bank-details lookup): the
   census admits only premium-billing-service's service token, never a user's.
2. **MongoDB:** run it as a replica set so lease-fenced invoice writes are
   transactional (leases are kept in the `PremiumInvoices` collection, so this works on
   Azure Cosmos DB for MongoDB too); Mongo creates the `RateTables` collection on first
   write.
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
| `RateCorrectionReviewThreshold` | `0` | A rate/plan correction adjustment larger than this keeps the invoice in Draft for review. |

Service-wide: `GroupLeaseSeconds` (300) and `GroupLeaseWaitSeconds` (60) under
`PremiumBilling:RatedBilling` (see the architecture note's group lease).

**Turning it off** (`Enabled: false`) returns the tenant to the original pricing from
the next billing run. Rated invoices already issued stay as they are, and a period that
already has a rated invoice is skipped by the original pricing (`skippedGroupNumbers`),
so it is not billed twice.

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
- **Data.** No existing document is rewritten. New fields on invoices are optional and
  absent on unrated invoices (`pricingSource` defaults to `CoveragePremium`).

## 3. Each billing run

`POST /api/v1/billing-runs/execute` as usual. In the run result:

- `invoiceIds`: invoices created and issued by this run (counted in the totals);
- `draftInvoiceIds`: invoices left in Draft (not billed, not in the totals), with a
  warning naming the first exception;
- `unchangedInvoiceIds`: groups whose period was already invoiced; nothing changed;
- `blockingDraftInvoiceIds`: groups not billed because an earlier period inside the
  retro window is still a Draft (the id is that Draft). Issue or void it, then run the
  period again.

Re-running a period is safe: issued invoices are never changed or duplicated, and
Drafts are recomputed in place. Runs may overlap: each group is computed under its
billing lease, and a group whose lease stays busy past `GroupLeaseWaitSeconds` fails
with a warning ("busy") for the next run.

## 4. Fixing a Draft

1. `GET /api/v1/premium-invoices/{id}`: read `ratingExceptions`.
2. Fix the cause:
   - `RATE_NOT_FOUND`: save the missing or extended rate table (a new version);
   - `RATE_TABLE_INVALID`: two current tables of the plan overlap; withdraw or correct one;
   - `ENROLLMENT_DATA`: fix the member or coverage record named in the message;
   - `ZERO_CHARGE`: check the rate table and the coverage dates;
   - `COVERAGE_MISSING`: a billed coverage record was deleted in coverage-service; restore it
     with its termination date (the credit is then computed), or post the credit by hand;
   - `NO_BILLABLE_COVERAGE`: the group has no coverage for the period; void the Draft if that is right.
3. `POST /api/v1/premium-invoices/{id}/regenerate` (billing:run), or run billing for
   the period again. With no exceptions and no review reasons left the invoice is
   issued; otherwise read `reviewReasons` and `POST /api/v1/premium-invoices/{id}/issue`.
4. **Issue answers 409** when the Draft changed since it was computed (coverage, rates,
   or another invoice of the group was issued meanwhile). It has been regenerated:
   review the new amounts and issue again. Issue answers 400 while an earlier period of
   the group is still a Draft; issue that one first.

A Draft cannot be paid, sent, drafted by EFT or go delinquent. A remittance naming its
invoice number goes to the exceptions queue (`InvoiceClosed`) until it is issued.

## 5. Correcting rates

Save a new version of the table (`expectedCurrentVersion` = the version you read). The
next invoice of each affected group re-rates its invoiced months within the retro
window and bills or credits the difference as `RateChange` adjustments marked
`isRateCorrection`; issued invoices are not changed. Those invoices stay Draft with a
`RATE_CORRECTION` review reason (above `RateCorrectionReviewThreshold`) until a person
issues them. A mid-year revision of an age-rated table must set `ageDeterminationDate`.
To find which rates an invoice line used:
`GET /api/v1/rate-tables/{rateTableId}/versions/{rateTableVersion}` (its `contentHash`
equals the line's `rateTableHash`).

## 6. Credits

- **Net credit invoices** (retro credits above the month's charges) are issued with
  their credit moved to the sponsor account's unapplied credit (entry
  `rated-credit-{invoiceId}`, type `RatedInvoiceCredit`); the invoice shows a
  `SponsorAccountCredit` payment and a zero balance. The invoice is saved before the
  account is credited; if a run dies in between, the next run for the period posts the
  missing entry. Voiding a rated invoice waits for its group's billing lease (409 if a
  run keeps holding it: retry shortly).
- **Terminated groups.** Billing runs bill Active sponsors only, so a retro term reported
  after the whole group terminated is not credited automatically. Compute the credit
  from the coverage-month shown on the last rated invoices and post it to the sponsor
  account by hand (a manual credit, with two people as for any ledger change).

## 7. Do not

- Delete a coverage record in coverage-service to end coverage: terminate it. A billed
  record that disappears holds the group's next invoice in Draft (`COVERAGE_MISSING`).
- Edit invoice documents in the database. Issued invoices are immutable by design.

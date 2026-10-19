# Rated premium billing

Rated billing prices premium invoices with the premium rating engine
([docs/engines/PREMIUM-RATING-ENGINE.md](../engines/PREMIUM-RATING-ENGINE.md))
instead of the `monthlyPremium` + `employerContribution` stored on each
coverage-service record. It is switched on per tenant and is **off by default**;
a tenant that has not turned it on is billed exactly as before. Operations and the
migration steps are in
[PREMIUM-RATED-BILLING-RUNBOOK.md](../operations/PREMIUM-RATED-BILLING-RUNBOOK.md).

## Contents

- [Where the amounts come from](#where-the-amounts-come-from)
- [Flow of one group's invoice](#flow-of-one-groups-invoice)
- [Households](#households)
- [Rate tables and versions](#rate-tables-and-versions)
- [Lines](#lines)
- [Proration rules](#proration-rules)
- [Mid-period and retro changes](#mid-period-and-retro-changes)
- [Rounding](#rounding)
- [Draft, issue and idempotency](#draft-issue-and-idempotency)
- [Exceptions](#exceptions)
- [Storage](#storage)
- [Limitations](#limitations)

## Where the amounts come from

| | Original behaviour (`PricingSource = CoveragePremium`) | Rated (`PricingSource = RatingEngine`) |
|---|---|---|
| Line amount | coverage-service `monthlyPremium + employerContribution`, prorated by day | Rating engine on the plan's rate table for the coverage period |
| One line per | coverage record (each covered member) | subscriber coverage and rate-table version (list bill), or plan + tier + rate (composite) |
| Retro changes | not handled | adjustment lines on the next invoice |
| Missing price | `$0` line (`monthlyPremium` null) | invoice stays **Draft** with an exception |
| Re-running a period | creates another invoice | finds the existing one (no duplicate) |
| Audit | none | rate table id, version and hash on each line; segments showing how the amount was built |

## Flow of one group's invoice

```
billing run (POST /api/v1/billing-runs/{id}/execute)
  └─ per sponsor group
       ├─ tenant not enabled ──────────────► original pricing (unchanged)
       ├─ tenant not enabled, but the period has a rated invoice ─► skipped (no double bill)
       └─ RatedInvoiceGenerator.GenerateAsync      (under the group's billing lease)
            1. read the group's invoices
               ├─ period has an unrated invoice ─► leave it (UnratedInvoiceExists)
               ├─ period has an issued rated one ─► leave it (AlreadyIssued)
               ├─ an earlier period in the retro window is a Draft ─► stop (BlockedByEarlierDraft)
               └─ period has a rated Draft ──────► recompute it in place
            2. coverage-service: every coverage record of the group (terminated included)
               member-service:   rating census (GET /api/v1/members/rating-census)
               rate tables:      current version of each table of the tenant
            3. RatingEnrollmentBuilder → households (+ data issues)
            4. PremiumInvoiceCalculator → lines for the period + retro adjustments (+ rating issues)
            5. list bill, or CompositeBill.Collapse for a composite group
            6. no exceptions and no review reasons → issued (a net credit moves to the
               sponsor's unapplied credit and settles the invoice); else Draft
            7. create with a deterministic id, or replace the Draft (version / ETag checked)
```

**Group lease and fencing.** Every read → compute → write of a group (generate,
regenerate, issue, and the void of a rated invoice) holds the group's billing lease:
one document per tenant and group (id = hash of tenant and group), taken by a create
that only one writer wins, or by a conditional takeover once it expires. The lease
lasts `GroupLeaseSeconds` (300); a second writer waits up to `GroupLeaseWaitSeconds`
(60) and then fails that group with a run warning (409 from the API).

A holder that stalls past its lease (and was taken over) must not write what it
computed from the older ledger, so the lease **fences** the writes:

- **Heartbeats.** The holder renews the lease (only if it still owns it and it has
  not expired) after the coverage-service call and after the member census; a failed
  renewal stops the run (`BillingLeaseLostException`) before anything is written.
- **Fenced invoice writes.** Lease documents live in the `PremiumInvoices`
  collection (`DocType = billingLease`), so on a replica
  set the lease renewal (owner must match) and the invoice write are one
  single-collection transaction, the only kind Azure Cosmos DB for MongoDB (RU)
  supports. A server that reports a replica set but refuses the transaction, and a
  standalone mongod, fall back (with a warning) to renewing the lease immediately before
  the write (owner and unexpired); the milliseconds between the two are not fenced.
- **Lease documents are never invoices.** Every invoice read and write refuses a lease
  id (`lease-…`) and every invoice query excludes `docType`/`DocType` documents, so
  `GET /premium-invoices/{leaseId}` is 404 and a payment or void cannot overwrite a lease.
- **Account credits** are idempotent by invoice id (below), so they need no fence.

Two periods of one group are therefore never reconciled from the same ledger, and a
retro change is billed once.

A coverage-service, member-service or database failure fails that group's invoice
(a billing-run warning); it never produces a `$0` or partial issued invoice.

## Households

coverage-service keeps one coverage record per covered member. member-service's
rating census (minimum necessary: member id, subscriber link, X12 relationship code,
date of birth, tobacco flag, name; never SSN, address or contact details) says who
is the subscriber. The census admits only premium-billing-service's own service token
(`RequireServiceClient`), and premium billing always calls it with that token, never a
user's; a Finance user cannot read it. `RatingEnrollmentBuilder` makes one household per **subscriber
coverage record** and attaches each dependent's coverage under the same plan and
insurance line, overlapping in time, with the dependent's own effective and
termination dates. The tier is derived from who is covered on each day; the coverage
record's `coverageLevel` is not used.

Relationship codes: `18` self; `01` spouse and `53` life partner rate as a spouse;
`19`, `09`, `10`, `17`, `05`, `15` rate as a child; any other code is another
dependent (rated as a child). Tobacco: `tobaccoUser` null is rated as a non-user.

These are reported as exceptions (the household is not billed and not reconciled,
the invoice stays Draft): a member missing from the census, no date of birth, a
dependent with no relationship code or no subscriber, a dependent with no subscriber
coverage of the same plan, two subscriber coverages of the same plan on the same day,
a dependent covered twice on the same day.

## Rate tables and versions

Rate tables (`/api/v1/rate-tables`, finance:write to change, billing:read to read)
are stored as **immutable versions**. Saving a table creates version 1; saving it
again (with `expectedCurrentVersion` = the latest version read, 409 otherwise) creates
the next version; withdrawing it creates a final version flagged `withdrawn`.
Nothing is updated or deleted, so `GET /api/v1/rate-tables/{id}/versions/{n}` always
returns the rates an invoice line recording `(id, n)` was billed with.

Each version carries a SHA-256 `contentHash` of its rating content, computed once
when stored and copied onto the lines rated with it. A save is validated alone
(`RateTable.Validate`) and with the tenant's other current tables of the plan: two
tables may not cover the same day, and the proration rule may change only on the 1st
of a month.

Billing uses the latest non-withdrawn version of each table. A plan whose stored
tables conflict anyway (two concurrent saves) is not rated at all: every coverage of
that plan becomes a `RATE_TABLE_INVALID` exception.

## Lines

**List bill** (default): one line per subscriber coverage and rate-table version for
the billed month. A month normally has one line per subscriber; a rate change taking
effect mid-month gives two, each with its own version. Every rated line records
`rateTableId`, `rateTableVersion`, `rateTableHash`, `ratingMethod`, `prorationRule`,
`servicePeriodStart`/`End` and `ratingSegments` (from, to, days, tier, monthly
premium, amount, basis such as `500.00 × 22/31`). The line amount is the sum of its
segments.

**Composite** (per group, `BillFormat: Composite`): full-month lines with the same
plan, insurance line, tier, rate version and amount become one line with `quantity`
and `unitRate`; its `components` list each coverage and its amount (they sum to the
line). Prorated lines keep their own line. The invoice total is the same as the list
bill. Later invoices reconcile composite invoices per coverage through the components.

Retro adjustments are per coverage in both formats and record the rate versions of the
corrected charge in `rateTableVersions` (`id@vN`).

## Proration rules

The rule is part of the rate table (`proration`), so it is versioned with the rates.

| Rule | A month is charged as | Starts mid-month | Ends mid-month |
|---|---|---|---|
| `Daily` (default) | monthly × days ÷ days in month, per run of days with the same household and rate | from that day | through that day |
| `HalfMonth` | two halves (1–15, 16–end), each half the monthly premium, rated with the household of the half's first day | from the next half | the whole current half |
| `FullMonth` | the whole month, rated with the household of the 1st | from next month | the whole month |

Outcomes that surprise people (all tested in `RatedBillingCalculatorTests.ProrationEdges`):

- `HalfMonth`: coverage starting on the 17th is not charged that month; a stay from the
  2nd to the 10th is never in force on a half's first day and costs nothing; a stay
  from the 2nd to the 20th is charged one half.
- `FullMonth`: coverage ending on the 1st is charged the whole month; coverage starting
  on the 2nd is not charged until the next month.
- `Daily`: coverage ending on the 1st is charged one day (500 × 1/31 = 16.13).

A rate change applies from its effective date: within each billing unit the amount is
split at the rate table boundary (by days of that unit). Under `Daily` that is
exactly the effective date.

## Mid-period and retro changes

- **Adds, terms, tier changes in the billed month** are prorated by the plan's rule.
- **Changes to months already invoiced** (retro adds, retro terms, tier corrections,
  a new rate-table version) are never edited into the issued invoice. The next invoice
  re-rates each invoiced month within `MaxRetroMonths` (default 3) and adds one
  adjustment per coverage and month for the difference between what is due now and
  what was billed (lines plus earlier rating retro adjustments): `RetroAdd`,
  `RetroTerm` or `RateChange`. Manual adjustments are never reversed.
- **Ages** are fixed at the plan-year start (`effectiveFrom`, or `ageDeterminationDate`)
  or at a member's own later coverage start, per 45 CFR 147.102. A birthday during
  the year changes nothing until the next plan year's table. A mid-year revision of an
  age-rated table (age band, composite, or any table with a tobacco surcharge; one that
  starts less than a year after the previous table's age date) **must** set
  `ageDeterminationDate` (normally the plan-year start): saving it without one is
  rejected.
- **Rate or plan corrections.** A retro adjustment that re-prices an invoiced month
  under different rate-table versions than it was billed with (a new version of the
  table, or the coverage moved to another plan) is marked `isRateCorrection`. When its
  size is above `RateCorrectionReviewThreshold` (default 0: always), the invoice stays
  Draft with a `RATE_CORRECTION` review reason until someone issues it.
- **Net credits.** When retro credits exceed the month's charges the issued invoice is
  a net credit. It is saved first, settled by a `SponsorAccountCredit` payment of the
  same (negative) amount (balance due 0, status Paid); only then is the credit posted
  to the sponsor account's unapplied credit (entry `rated-credit-{invoiceId}`, type
  `RatedInvoiceCredit`), once per invoice. If the process dies between the two, the
  next run finds the invoice issued and posts the missing entry (self-healing); a void
  cannot land in between because it takes the group lease, and a credit invoice is
  Paid and cannot be voided. The credit is then held on the account as unapplied
  credit, the same as an overpayment received by cash application.

## Rounding

Money is `decimal`, rounded to the cent half away from zero:

1. The engine rounds each member premium (age band, tobacco); tier rates are exact.
2. Each segment is rounded once: monthly × days ÷ denominator (days in month; for
   a half, 2 × days in the half). A segment that is the whole month is the monthly
   premium exactly. Adjacent pieces with the same rate version, tier and premium are
   merged before rounding (so two halves of 500.01 bill 500.01, not 500.02).
3. A line is the sum of its rounded segments, not rounded again.
4. The employer share of a line is rounded; the subscriber share is the rest.
5. An adjustment is due minus billed, both already in cents.
6. The invoice total is the sum of its lines and adjustments. There is no
   invoice-level rounding; per-line rounding drift is kept, never redistributed.

The generator checks before saving that every amount is whole cents, each line is
employer + subscriber share, each composite line is the sum of its components, and
the total is the sum of lines and adjustments; otherwise the group's invoice fails.

## Draft, issue and idempotency

`InvoiceStatus.Draft` (appended to the enum, so stored numeric values keep their
meaning) is a rated invoice that is not issued: it has `ratingExceptions` (blocking),
or `reviewReasons` (the tenant holds rated invoices for review, a rate correction, a
net credit with nowhere to go). A Draft:

- is recomputed in place by the next billing run for the period, or by
  `POST /api/v1/premium-invoices/{id}/regenerate`;
- is issued automatically by a recomputation that finds neither exceptions nor review
  reasons, or by `POST /api/v1/premium-invoices/{id}/issue`. Issue **recomputes** the
  Draft under the group lease and issues it only if it has no exceptions and is exactly
  what was computed before (same `ratingFingerprint` of lines, adjustments, exceptions
  and review reasons) against the same issued invoices (`reconciledInvoiceIds`).
  Otherwise the recomputed Draft is saved and the request answers 409: review it and
  issue again. A stale Draft is never issued;
- blocks the billing of later periods of its group inside the retro window
  (`BlockedByEarlierDraft`, listed in the run's `blockingDraftInvoiceIds`), and cannot
  be issued while an earlier period of the group is still a Draft;
- cannot be sent, paid, applied to by remittances, drafted by EFT, become overdue or
  delinquent, count toward a sponsor's open balance, or show on a member's premium tab;
- does not count as billed for later retro reconciliation;
- is not in the billing run's invoice list or totals (`draftInvoiceIds` instead).

An issued rated invoice is never changed by rating again; corrections go on the next
invoice. A rated invoice's id is derived from tenant, group, period and reissue number
(`inv-r-{hash}-{yyyyMM}-{n}`), so a second or concurrent generation for the period hits
the store's create conflict and finds the existing invoice instead of creating another.
After a void, the next generation creates reissue `n + 1` with the same invoice number.

## Exceptions

`ratingExceptions` on the invoice, with `code`, `coverageId`, `memberId`,
`serviceMonth` and a message:

| Code | Meaning |
|---|---|
| `RATE_NOT_FOUND` | No current rate table of the plan covers a day to be charged (missing or expired rates). |
| `RATE_TABLE_INVALID` | The plan's stored tables conflict. |
| `ENROLLMENT_DATA` | The household cannot be built or rated from the data (see Households). |
| `ZERO_CHARGE` | A coverage rated to `0.00` for the month. |
| `NO_BILLABLE_COVERAGE` | Nothing to bill or reconcile for the group this period. |
| `COVERAGE_MISSING` | A coverage billed in the retro window is no longer listed by coverage-service at all (deleted rather than terminated). It is not credited; restore it with a termination date, or credit it by hand. |

Review reasons (`reviewReasons`; they do not block an explicit issue): `HELD_FOR_REVIEW`,
`RATE_CORRECTION`, `NET_CREDIT` (no sponsor account store).

## Storage

MongoDB is the only store (ADR 006).

| Store | MongoDB |
|---|---|
| Rate table versions | collection `RateTables`, `_id` = hash of tenant + table id, plus version |
| Invoices | `PremiumInvoices` (unchanged; new optional fields) |
| Group billing leases | in collection `PremiumInvoices` (`DocType = billingLease`) |
| Net credits | `SponsorAccounts` (existing) |

## Limitations

- Months billed before a tenant turned rated billing on are not reconciled
  (see the runbook's migration note).
- Billing runs list only Active sponsors, so retro terms reported after a group itself
  terminated are not credited automatically. The runbook gives the manual procedure;
  an automatic final adjustment invoice for terminated groups is a follow-up.
- On a standalone MongoDB, or a MongoDB API that refuses the transaction, the lease
  fence is renew-then-write, not atomic; run MongoDB as a replica set.
- Released leases are deleted; a lease left behind by a crashed run stays until it is
  taken over after it expires (no TTL index).
- Heartbeats run between the coverage-service and member-service calls, not between
  their pages; a single very slow listing can still outlive the lease, in which case
  the run is stopped at its next heartbeat or write and writes nothing.
- Rated lines are per subscriber, so a dependent's premium tab (which finds invoices
  by line member) no longer lists the group's rated invoices; composite invoices have
  no member line at all and appear on no member's tab.
- The portal has no Issue / Regenerate buttons or Draft filter yet (follow-up); use the
  API. Draft exceptions and review reasons are read through the API.
- The member census reads the legacy `subscriberMemberId` link on dependents.
- Rate tables have no maker-checker; finance:write saves them.

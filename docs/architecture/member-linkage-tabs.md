# Member Linkage Tabs (5.12, 5.14–5.16)

Adds four member-scoped read surfaces — one per financial/coverage service —
and exposes them in the portal Member Details dialog so an operator can see
Claims, AR, Premium Billing, and Sponsor context for a single member
without leaving the dialog.

## Scope of this PR

- **claims-service** — new `GET /api/v1/claims?memberId=…` that returns a
  FHIR R4 `ExplanationOfBenefit` array wrapped with pagination metadata.
  Adds `IClaimRepository.SearchForMemberAsync` (Cosmos + Mongo) with filters
  for date range, status, provider, claim type, and amount range. Adds a
  hand-built `IExplanationOfBenefitProjector` (no `Hl7.Fhir.R4` transitive
  dep — same pattern as `IFhirPatientProjector` in member-service).
- **ar-service** — new `GET /api/v1/members/{memberId}/ar-summary`.
  Aggregates `ArPostingEntry` rows across all `ArBalance` documents for the
  tenant where `entry.memberId == memberId`. Adds `MemberId` to
  `ArPostingEntry` and a supporting Mongo index on
  `postingEntries.memberId`.
- **premium-billing-service** — new
  `GET /api/v1/members/{memberId}/premium-summary`. Selects invoices whose
  `LineItems` mention the member, picks the most recent as "current", and
  computes APTC-aware grace-period state. Adds `IsAptcSubsidized`,
  `AptcMonthlyAmount`, and `GraceType` (`Standard | AptcThreeMonth`) to
  `PremiumInvoice`.
- **sponsor-service** — new
  `GET /api/v1/sponsors/{groupNumber}/member-view`. Wires
  `ISponsorRepository` into `SponsorsController` (replacing the mock data
  path). Adds `Broker` and `OpenEnrollmentWindow` sub-objects to `Sponsor`.
- **Portal** — three new `MudTabPanel`s appended to `MemberDetailsDialog`
  (Claims / AR / Premium) plus a `MudExpansionPanel` sub-section "Sponsor"
  inside each coverage card on the Coverage tab. Each tab loads lazily on
  first activation; each sponsor panel loads lazily on first expansion,
  keyed by `groupNumber` to avoid N+1 on initial dialog open.
- **Portal service layer** — extends `IClaimsService`, `IArService`,
  `IPremiumBillingService`, and `ISponsorService` with the four new member-
  scoped methods and their DTOs (`EobSearchResponse`, `MemberArSummary`,
  `MemberPremiumSummary`, `SponsorMemberView` and nested records).
- **Tests** — `ClaimsV1MemberSearchTests` for FHIR projection + filter
  pass-through; `MembersArControllerTests.BuildSummary` for bucket math;
  `MembersPremiumControllerTests.ComputeGrace` for APTC vs. standard grace
  boundaries; `SponsorsMemberViewTests` for the projection.

## Why the shapes look the way they do

### FHIR EOB wrapper, not raw EOB array
The spec called for a FHIR `ExplanationOfBenefit` array with "small wrapper
metadata". The wrapper carries `total`, `page`, `pageSize`, and `resources`.
This keeps the endpoint FHIR-compatible for resources[] consumers while
giving the portal the pagination metadata it needs for the Claims tab
count card without a second request.

### FHIR projection hand-built with `JsonObject`
`Hl7.Fhir.R4` pulls a heavy transitive dep graph (NewtonsoftJson,
multiple model libs) and we already established the pattern in
`member-service` to hand-build FHIR JSON via `System.Text.Json.Nodes`. The
claims-service `ExplanationOfBenefitProjector` maps `Claim` → `JsonObject`
following the same convention. Projection mapping is covered by unit tests.

### APTC grace modeled on the invoice, not the coverage
Grace-period state is *per invoice* — an APTC enrollee can be in the
statutory 3-month grace for one month's bill while another month is paid.
Storing `IsAptcSubsidized` / `AptcMonthlyAmount` / `GraceType` on
`PremiumInvoice` keeps that granularity and avoids a cross-service hop
from premium-billing to coverage-service on a read-hot path. A follow-up
PR can propagate an `IsAptcSubsidized` flag to `Coverage` when broader
APTC reporting needs it.

### AR aggregation via posting entries
AR documents are per-(account, period) today; there's no standalone
"member AR ledger". Rather than introduce a second collection, we tag
each `ArPostingEntry` with an optional `MemberId` and aggregate on read.
A single Mongo index on `(tenantId, postingEntries.memberId)` keeps the
lookup fast.

### Sponsor sub-section, not a new tab
The spec explicitly called out the Sponsor section as an expansion of the
Coverage tab rather than a new tab, because sponsor context is naturally
scoped to a specific coverage card (many members have multiple active
coverages with different group numbers). Per-card expansion panels keyed
by `groupNumber` match that shape.

### Read-only AR
Per spec, the AR tab initiates no payments. The tab renders `MudTable` rows
without row-level action buttons, and the portal `IArService` path for this
tab is strictly `GET`. Payment actions remain on the dedicated AR page.

## Endpoint contracts

### `GET /api/v1/claims?memberId=…`
Filters: `serviceDateFrom`, `serviceDateTo`, `status` (ClaimStatus),
`providerNPI`, `claimType` (ClaimType), `amountMin`, `amountMax`, `page`,
`pageSize`. Returns `EobSearchResponse { total, page, pageSize, resources[] }`
where each `resources[]` element is a FHIR R4 ExplanationOfBenefit.

### `GET /api/v1/members/{memberId}/ar-summary`
Returns `MemberArSummary { memberId, currentBalance, aged { bucket0_30,
bucket31_60, bucket61_90, bucket91Plus }, recentCharges[≤10],
recentPayments[≤10], asOfUtc }`. Empty lists and zero balance for members
with no posting activity (never 404 on a valid member).

### `GET /api/v1/members/{memberId}/premium-summary`
Returns `MemberPremiumSummary { memberId, currentInvoice, nextBillDate,
autopayEnabled, grace { isInGrace, graceType, daysRemaining, expiresOn },
last12[] }`. `graceType` is always present (`Standard` or
`AptcThreeMonth`). `isInGrace` is `true` iff (a) balance > 0, (b) status
is not Paid/Voided/WriteOff, and (c) `DueDate <= now <= GracePeriodExpires`.

### `GET /api/v1/sponsors/{groupNumber}/member-view`
Returns `SponsorMemberView { groupNumber, sponsorName, lineOfBusiness,
status, primaryContact, broker, openEnrollment }`. `openEnrollment.status`
is computed at response time from `(start, end, now)` so stale documents
don't report "Open" past the end date.

## Grace-period semantics

Two grace regimes coexist on `PremiumInvoice`:

| Regime         | Trigger                      | Window                  |
|----------------|------------------------------|-------------------------|
| `Standard`     | Commercial sponsor, non-APTC | `DueDate + GracePeriodDays` (30 default) |
| `AptcThreeMonth` | ACA APTC-subsidized Exchange enrollee | `DueDate + 90 days` (45 CFR §156.270(d)) |

`GracePeriodExpires` is stamped by the billing run at invoice generation;
if the model lacks the value (older invoices), `ComputeGrace` falls back
to `DueDate + 90d` for APTC or `DueDate + GracePeriodDays` for standard so
the endpoint always has a definitive answer.

## Portal tab wiring

All three new tabs follow the existing lazy-load pattern used for
Benefits/834/Family: state fields live on the dialog's `@code`, loaders
are invoked from `MudTabPanel.OnClick`, and a `_*LoadedOnce` flag prevents
re-fetching when the user clicks between tabs.

The Coverage tab sponsor sub-section uses the same lazy pattern but keys
by `coverage.GroupNumber` so multi-coverage members don't fetch sponsors
they never expand.

Claims tab **does not render a claims grid**. The spec explicitly forbids
duplication — the tab shows a count card plus a "View all claims" button
that navigates to `/claims?memberId=…`.

## Tests

Each service gets focused unit coverage of the deterministic pieces:

| Suite                              | What it proves                                   |
|-----------------------------------|--------------------------------------------------|
| `ClaimsV1MemberSearchTests`        | v1 route is reachable, 400 without memberId, filter pass-through, EOB `status`/`outcome` mapping |
| `MembersArControllerTests`         | Aggregation scoped to requested member, correct bucket assignment at day boundaries |
| `MembersPremiumControllerTests`    | APTC 3-month window yields `AptcThreeMonth` + day counter; Paid invoice is never in grace; current invoice is the newest |
| `SponsorsMemberViewTests`          | Broker + OE projected; null-safety on missing contacts; OE status shifts with `asOf` |

The Sponsor test project is new (`SponsorService.Tests`) and registered in
`cloudhealthoffice-main.sln`.

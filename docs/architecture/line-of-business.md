# Line of business

A member's line of business comes from their **program** in tenant-service. A program
(`PlanProgram`: carrier, code, line of business, state, for example TX STAR) is reached
through coverage.PlanId → BenefitPlan.ProgramId → program, and read with
`IProgramDirectory` (`CloudHealthOffice.PlanConfiguration`). Services that store a line of
business keep a cached copy of the program's value.

## The one type

`CloudHealthOffice.Infrastructure.Programs.LineOfBusiness`:

| Value | Name |
|---|---|
| 0 | Unknown |
| 1 | Commercial |
| 2 | Medicare |
| 3 | Medicaid |
| 4 | Exchange |
| 5 | TRICARE |
| 6 | VA |

The numbers are the ones most services already store in MongoDB and send as JSON. Never
renumber them.

`LineOfBusinessNames` reads text: the names above, the numbers 1-6, and the legacy
spellings still stored or in flight (`Marketplace` → Exchange, `MedicarePartD` →
Medicare). `Normalize` gives the canonical name. Use it whenever a line of business
arrives as a string (Kafka events, GL source events, query strings).
`PlanConfiguration.LinesOfBusiness.Normalize` delegates to it.

## Serialization

- JSON: `LineOfBusinessJsonConverter` writes the canonical name and reads names and
  legacy spellings. Numbers are refused unless the service opts in. Register it ahead of
  the service's generic enum converter; it is not an attribute on the enum, so services
  that send numbers keep doing so until they move over.
- MongoDB: `LineOfBusinessBsonSerializer` reads names, legacy spellings and numbers and
  writes the name (or the number, when configured for `Int32`). A value it cannot read
  is `Unknown`, so one bad document never fails a query.

## What is not a line of business

- **Part D** is a Medicare benefit type. In appeals it is `BenefitType = PartDDrug` on a
  Medicare case, which picks the Part D clocks (42 CFR 423.590). `LineOfBusinessNames.IsPartD`
  recognises the legacy `MedicarePartD` spelling.
- **STAR, STAR+PLUS, STAR Kids, CHIP** and other Medicaid sub-programs are programs.

## Migration

Each service still has its own enum and moves to the shared one in its own PR.
`LineOfBusinessDriftTests` (Infrastructure.Tests) fails when a local enum's names or
numbers differ from the shared one. Its `KnownExceptions` list holds the enums that differ
today, each with the PR that removes it:

1. Shared enum, parser, drift test, GL normalization (payment-service GL events now say
   Exchange, not Marketplace).
2. appeals-service and Appeals.Contracts (done): shared enum plus `AppealBenefitType`
   (MedicalOrService, PartDDrug). `AppealLineOfBusinessMigrationHostedService` rewrites
   stored `MedicarePartD` (and the old number 5) to Medicare + PartDDrug and `Marketplace`
   to Exchange at startup, before requests are served. The API reads `Marketplace` as
   Exchange and refuses `MedicarePartD`; TRICARE and VA appeals are refused (no rules
   modelled). The FHIR `cho-appeal-line-of-business` codes are unchanged
   (`marketplace`, `medicare-part-d`). Appeal events carry `lineOfBusiness` (canonical)
   and `benefitType`.
3. payment-service (done): the shared enum on payment-run criteria, stored by name. The
   hand mapping to claims-service's numbers is gone (the shared numbers are claims').
   `PaymentRunLineOfBusinessMigrationHostedService` rewrites stored 0-3 numbers to names
   once at startup (every tenant database when `MongoDb:UseTenantScoping`), and the
   shared `LegacyLineOfBusinessBsonSerializer` reads any number not yet rewritten as the
   old enum, so reads never depend on the rewrite having run. Both are shared
   (`LegacyLineOfBusinessStorage.cs`) for the next service with stored numbers.
   The API reads `Marketplace` as `Exchange` and now writes `Exchange`.
4. eligibility-service (done): the shared enum on inquiries, 271 results and temporal
   eligibility. The off-by-one conversions from coverage-service's numbers are gone; a
   coverage with no line of business (0) now reports `Unknown`, not `Commercial`. Stored
   inquiries' 0-5 numbers are rewritten to names at startup by
   `EligibilityLineOfBusinessMigrationHostedService`, and read correctly until then
   through the shared `LegacyLineOfBusinessBsonSerializer`. The API takes names
   (`Marketplace` reads as `Exchange`) and refuses numbers.
5. The services whose enums already match: type swaps, no data change.
6. Coverage, claims and authorization intake take the line of business from the program.
7. ProviderEnrollmentService's flags enum is renamed `ProgramParticipation`.

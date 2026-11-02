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

## What is not a line of business

- **Part D** is a Medicare benefit type. `LineOfBusinessNames.IsPartD` recognises the
  legacy `MedicarePartD` spelling so appeals can keep the Part D deadline rules.
- **STAR, STAR+PLUS, STAR Kids, CHIP** and other Medicaid sub-programs are programs.

## Migration

Each service still has its own enum and moves to the shared one in its own PR.
`LineOfBusinessDriftTests` (Infrastructure.Tests) fails when a local enum's names or
numbers differ from the shared one. Its `KnownExceptions` list holds the enums that differ
today, each with the PR that removes it:

1. Shared enum, parser, drift test, GL normalization (payment-service GL events now say
   Exchange, not Marketplace).
2. appeals-service and Appeals.Contracts: shared enum plus a Part D benefit type. Stored
   `Marketplace` / `MedicarePartD` strings are read through the parser.
3. payment-service: one-time migration of stored run criteria from 0-3 to 1-4.
4. eligibility-service: one-time migration of stored records from 0-5 to 1-6.
5. The services whose enums already match: type swaps, no data change.
6. Coverage, claims and authorization intake take the line of business from the program.
7. ProviderEnrollmentService's flags enum is renamed `ProgramParticipation`.

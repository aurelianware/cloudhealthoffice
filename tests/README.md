# Tests

This folder contains test projects and supporting test assets.

## Test Projects
- [CloudHealthOffice.Edi.Tests](CloudHealthOffice.Edi.Tests/README.md) — X12 EDI parser/generator regression tests (835, 277CA, 270, 271)
- [CloudHealthOffice.NcciEngine.Tests](CloudHealthOffice.NcciEngine.Tests)
- [CloudHealthOffice.BenefitEngine.Tests](CloudHealthOffice.BenefitEngine.Tests)

## Acceptance And Interoperability Suites

Two suites answer deliberately different questions, and their results are never
merged into one score:

- [Cms0057Acceptance.Tests](Cms0057Acceptance.Tests) — does CHO implement the
  behavior its own CMS-0057-F acceptance specification requires? Reports
  `PASSABLE` / `PARTIAL` / `GAP`.
- [DaVinciInterop.Tests](DaVinciInterop.Tests) — can CHO exchange
  standards-conformant requests and responses with an *independent* HL7 Da Vinci
  implementation? Reports `Passed` / `Failed` / `Skipped` / `NotRun`. External
  scenarios are opt-in and start pinned third-party containers; see
  [docs/interop/davinci.md](../docs/interop/davinci.md). Executing today:
  `BR-PAS-SUBMIT-001` (PAS `$submit`), `BR-CRD-001` (CRD CDS Hooks) and
  `BR-DTR-001` (DTR `$questionnaire-package`, chained from the payer's own CRD
  determination).

## Cosmos DB Emulator Tests

Repositories with a Cosmos DB implementation are also tested against a real
Cosmos DB — the Linux "vnext" emulator — with the shared fixture in
[Shared/CosmosEmulator](Shared/CosmosEmulator/CosmosEmulatorFixture.cs). The
tests carry `[Trait("Category", "Cosmos")]` and live beside the Mongo
(EphemeralMongo) tests for the same behaviour: claims-service resolution-lock
fence and status guards, accumulator-service processed-claim leases, snapshots
and reversal/tombstone rows, the appeals outbox and premium-billing cash
application. Without an emulator they are skipped; CI's
`.NET Cosmos DB Emulator Tests` job sets `COSMOS_EMULATOR_REQUIRED=true`, which
turns an unreachable emulator into a failure.

```bash
docker run -d -p 8081:8081 \
  mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20261008
dotnet test tests/CloudHealthOffice.ClaimsService.Tests --filter Category=Cosmos
```

`COSMOS_EMULATOR_ENDPOINT` (default `http://localhost:8081/`) and
`COSMOS_EMULATOR_KEY` (default: the emulator's published key) point the tests
elsewhere. Known emulator gap: `ARRAY_LENGTH` is not evaluated inside a patch
`FilterPredicate` (HTTP 400), so the claims "contradictory approval" repair
test skips itself on the emulator; it runs unchanged once the emulator
supports it.

## Supporting Artifacts
- [E2E-TEST-RESULTS.md](E2E-TEST-RESULTS.md)
- [fixtures](fixtures)
- [integration](integration)
- [unit](unit)

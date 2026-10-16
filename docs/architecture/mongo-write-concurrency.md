# MongoDB write concurrency

**Status:** high-risk sites fixed (Phase 5a). Medium and low sites are
tracked below and still open.

## The rule

A document that is read, changed in memory and written back whole must carry
a revision, and the write must be conditional on it:

- **Field:** `long Revision`. Expected `0` also matches documents written
  before the field existed.
- **Replaces:** a replace filters on `{ _id, TenantId, Revision: expected }`
  and writes `expected + 1`.
- **Targeted updates:** every other write to the same document (`$set`,
  `$push`, conditional transitions) also does `$inc Revision`, so a replace
  based on an older read fails instead of undoing it.
- **On conflict:** when nothing matched but the document exists, the write
  throws a service exception derived from
  `CloudHealthOffice.Infrastructure.Data.ConcurrencyConflictException`. Every
  service answers it with **409** (`ConcurrencyConflictMiddleware`, installed
  by `UseChoAuthentication`).
- **Callers:** request paths let the 409 through. Background consumers, jobs
  and money-movement outcome writes re-read and re-apply, a bounded number of
  times, where that is correct. Where only one actor may win, such as starting
  a run, the loser stops.

Reference implementation: `ClaimRepositoryMongo` (`Bumped`, `RevisionIs`).

## Fixed (Phase 5a)

| Service | Document | Notes |
|---|---|---|
| claims-service | Claim | `UpdateAsync` and the lock-fenced replace are conditional; 15 targeted updates bump the revision |
| claims-service | ClaimAdjustment | a stale step-11 write cannot erase a lifecycle transition |
| payment-service | Payment, PaymentRun, ReversalRun | run saves merge concurrent reservation outcomes while Running |
| premium-billing-service | BillingRun, EftDraft | two executes cannot both run; a Stripe or NACHA outcome cannot undo a cancel or claim |
| capitation-service | CapitationRun, CapitationDisbursement, CapitationStatement | NACHA outcomes re-apply after the money moved |
| authorization-service | Authorization | the RFAI-docs consumer re-reads and retries |
| rfai-service | RfaiCase | concurrent delivery and response both survive |
| coverage-service | Coverage | a stale replace cannot turn Terminated back into Active |
| member-service | Member | |
| ar-service | CashPosting, ArAdjustment | posting and reversing an adjustment write a fixed balance entry id (`adj-post:{id}`, `adj-reverse:{id}`), so a retry after a failed adjustment save cannot count it twice |
| idcard-service | IdCardRecord | a scan can no longer undo a revocation |
| tenant-service | TenantUser | a login sets only `LastLoginAt`; it can no longer restore a removed role or status |

Already conditional before Phase 5a: the accumulator stores (Redis and Mongo),
WorkItem, PaymentFileTransmission, ProviderReceivable, BankReconciliation, GL
journal, ArBalance, SponsorAccount, SponsorBankAccount, ProviderBankAccount,
MemberDocument, PremiumInvoice, and the Consent and PersonalRep transitions.

## Still open

### Medium

| Service | Site | Risk |
|---|---|---|
| benefit-plan-service | `BenefitPlanRepositoryMongo` UpdateAsync, UpdateDraftAsync, publish/supersede, terminate | `VersionState` is checked on the read but not in the filter, and publish has no Draft precondition |
| provider-service | `ProviderRepositoryMongo`, `OrganizationRepositoryMongo` (draft update, activate/supersede, replace version row) | same as benefit-plan |
| capitation-service | `CapitationContractRepositoryMongo` UpdateAsync | contract rates |
| provider-contracts-service | `MongoProviderContractRepository` UpdateAsync | contract terms |
| engines | `FeeScheduleRepositoryMongo` fee schedule and provider contract upserts | pricing data |
| eligibility-service | `MongoBatchJobContainer` UpsertAsync, WriteInlinePayloadAsync, RecordBlobPayloadAsync | whole-document writes from separate reads can erase each other |
| encounter-service | `EncounterRepositoryMongo` UpdateAsync | |
| risk-adjustment-service | `RiskScoreRepositoryMongo` UpdateAsync | RAF scores |
| attachment-service | `AttachmentRepositoryMongo` UpdateAsync | |
| sponsor-service | `SponsorRepositoryMongo` UpdateAsync | can undo `UpdateStatusAsync` |
| member-service | `FamilyRelationshipRepositoryMongo` UpdatePairAsync | the transaction does not detect a stale read |
| tenant-service | `TenantRepository` UpdateAsync | Stripe webhooks and admin edits overwrite each other |
| premium-billing-service | `RemittanceRepositoriesMongo` RemittanceBatch UpdateAsync | |
| fhir-service | `MongoPayerToPayerImportRepository` UpdateBackfilledLedgerAsync | the backfill job writes back a stale copy |
| shared Infrastructure | `MongoClaimLifecycleStore` SaveAsync for transmissions, inbound attachment receipts and remittance receipts | a 999 and a 277CA processed concurrently can move a status backwards; outbox markers can be lost |

### Low

These are mostly admin configuration, import ledgers and rarely edited
documents:
- PaRule, TenantEnrollmentConfig, NCCI load and version records;
- work-management queues, teams and memberships;
- compliance config, trading partners, tenant roles, payer overrides and sync
  status;
- ID-card templates, member alerts, GL accounts and AR batch rules;
- fhir-service DTR questionnaire create and payer-to-payer ledger
  open/commit/fail;
- X12 outbound interchange records and claim-lifecycle acknowledgment,
  cursor, attachment and status-inquiry records;
- MassAdjudicationRun, ChoServiceCategoryMapping, RemittanceException and
  MpipRate qualification.

The full per-call-site audit (114 replaces) is summarised in the Phase 5a
change summary. Re-run it with:

```
grep -rnE "ReplaceOneAsync|FindOneAndReplaceAsync|\.ReplaceOne\(" --include='*.cs' src | grep -v Tests
```

## Known residual gaps in fixed services

- **Multi-document flows.** These write one document and then another, so a
  409 on the second leaves the first written. The order was the same before;
  no compensation was added.
  - capitation `ProcessReturnAsync`, `SettleDisbursementAsync` and
    `CancelDisbursementAsync`;
  - premium-billing NACHA loops;
  - coverage PCP assignment.
- **payment-service.** The GL outbox dispatcher marks `PublishedAt` without
  bumping the revision. A stale run save can clear it, which only causes the
  event to be delivered again; consumers deduplicate.
- **API responses.** Documents now include `revision` in API responses
  (tenant-service hides it on TenantUser).

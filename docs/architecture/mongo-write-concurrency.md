# MongoDB write concurrency

**Status:** high- and medium-risk sites fixed (Phase 5a). Low-risk sites are
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

### Medium-risk sites (Phase 5a, second pass)

| Service | Document | Notes |
|---|---|---|
| benefit-plan-service | BenefitPlan | publish needs a Draft and a Published predecessor at the revisions read; two publishes of one draft, or two drafts of one version, cannot both succeed; terminate needs Published |
| provider-service | Provider, Organization | same shape for activate/supersede and draft edits; suspend and terminate are revision-conditional; projection patches bump the revision |
| capitation-service | CapitationContract | |
| provider-contracts-service | ProviderContract | |
| engines | FeeSchedule, provider contract (FeeScheduleEngine) | create-or-replace upserts are conditional; revision 0 means create |
| encounter-service | Encounter | two dispatches cannot both take the same Pending encounter |
| risk-adjustment-service | RiskScore | a recalculation cannot undo Submitted |
| attachment-service | Attachment | the acknowledgment endpoint answers 409, not 500 |
| sponsor-service | Sponsor | a stale edit cannot undo a status change |
| member-service | FamilyRelationship | both rows are conditional inside the pair transaction; a server WriteConflict (112) is also 409 |
| tenant-service | Tenant | Stripe webhooks re-read and re-apply up to 5 times, then answer 400 so Stripe retries; `revision` is hidden in API responses |
| premium-billing-service | RemittanceBatch | |
| eligibility-service | batch job document (`MongoBatchJobContainer`) | no revision: job, inline payload and blob payload writes are targeted `$set`s of the parts they own |
| fhir-service | payer-to-payer ledger (`UpdateBackfilledLedgerAsync`) | no revision: a targeted `$set` of the backfill's own fields, fenced on the commit it read |
| shared Infrastructure | claim transmission, remittance receipt, inbound attachment receipt (`MongoClaimLifecycleStore`) | the 277CA processor, gateways, remittance processor/poster and attachment outbox re-read and re-apply through the existing state rules, so a status cannot move backwards and outbox markers survive. A submit does not send while another request has the same key `Transmitting` (`TransmittingSinceUtc` within a 10-minute lease); a retry claims the send with a conditional save first |

Benefit-plan, provider, capitation contract, provider contract, fee schedule
and risk score PUTs replace the document with the request body. The body
carries no revision, so the server copies the revision from its own read.
That closes the race between the server's read and its write. It does not
detect a client editing from an old copy; that would need the client to send
a revision (`If-Match`), which is an API change.

## Still open

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
- **Multi-item request loops.** encounter `DispatchBatch` and
  `MarkBatchSubmitted`, and risk-adjustment batch status, update many
  documents in a loop. A 409 part way through leaves the earlier items written
  (encounters queued under a batch id the caller never receives).
- **Genesis versions.** Two first drafts (no predecessor) of the same plan,
  provider or organization can both publish, because they share no document
  to check. Closing this needs a uniqueness constraint or a chain lock.
- **Insert races without a unique index.** Two first-time risk-score requests
  for the same member and year can create two scores.
- **eligibility-service.** Two saves of the same job from separate reads are
  still last-writer-wins on the job itself; this matters only if a job is
  processed twice at once.
- **claim lifecycle.** Two different 277CA outcomes still end at whichever was
  applied last. The acknowledgment, cursor, outbound attachment transmission
  and status-inquiry records are still unconditional (low).
- **fhir-service.** The clinical backfill's `StageAsync` upserts rows from the
  archive it read, so an ingestion re-staging the same exchange at the same
  time could be overwritten.
- **tenant-service.** `ValidateApiKeyAsync` and `UpdateUsageAsync` read and
  replace the whole tenant; nothing calls them today, but wired up they would
  409 under load and should become targeted updates.
- **payment-service.** The GL outbox dispatcher marks `PublishedAt` without
  bumping the revision. A stale run save can clear it, which only causes the
  event to be delivered again; consumers deduplicate.
- **API responses.** Documents now include `revision` in API responses
  (tenant-service hides it on TenantUser and Tenant).

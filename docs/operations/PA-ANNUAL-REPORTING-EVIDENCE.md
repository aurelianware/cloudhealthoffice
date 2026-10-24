# PA annual reporting evidence

Baseline: main `401e3ca3064031ee2cabfd0e52ad520582d65c7b` after PR #1541.
Architecture: proposed [ADR 030](../adr/030-pa-reporting-evidence-and-annual-drafts.md).

## What executes

New native authorizations receive an immutable reporting receipt header in
`Authorizations.ReportingLedger`. `CreateAsync` ignores any supplied ledger.
`UpdateAsync` reads the stored revision, preserves the header/facts, captures a
changed clinical determination and replaces conditionally on the same revision.
Receipt and decision evidence therefore cannot commit independently of the
owning record. Missing/bad initial decision timing is explicitly unknown.
Expiry, cancellation, RFAI and an ordinary note edit do not invent a new decision.

There is no historical backfill. Existing records without a ledger remain
diagnostic candidates; their current approved/denied state is not used to invent
past final outcomes. A receipt correction requires a separate reviewed protocol.

## Recording PA source evidence

`POST /api/authorizations/cms-0057/{id}/facts`

Requires **both** `authorizations:decide` and
`authorizations:reporting-evidence`. Existing roles do not automatically obtain
the second permission. Grant it only to accepted PA sources/designated reporting
stewards; ordinary authorizations write access is insufficient. Tenant comes
from the token-scoped repository and recorder from the authenticated subject.
The native authoritative backend is required; Augment does not fall back to CHO.

Illustrative synthetic appeal payload:

```json
{
  "eventId": "appeal-42-level-2-decision-1",
  "kind": "AppealDecision",
  "effectiveAt": "2026-10-20T15:00:00Z",
  "outcome": "Approved",
  "sourceSystem": "accepted-pa-appeals-source",
  "evidenceReference": "pa-appeals:case-42/decision-1"
}
```

`ReviewExtension` instead takes `originalDueAt` and `extendedDueAt`, no outcome.
The extended due instant must increase and the effective grant must precede or
equal the original deadline. This attests source evidence; it **does not** grant
an extension or alter `DecisionDueAt`. Source acceptance must verify applicable
program/state conditions, supporting reasons and delivered notice. Do not infer
that every request is allowed a universal extension.

Appeals need a captured adverse original determination (Denied or Modified)
effective no later than the appeal. Each PA request and every appeal level must
use the same original authorization identity. Native claim appeals-service has
no automatic PA linkage in this change. Requests needing legacy reconciliation
or an unsupported ledger version return conflict rather than accepting a made-up
receipt. Unknown kinds, future/pre-receipt times and malformed evidence fail.

The `(sourceSystem, eventId)` key is scoped to the authorization. Exact retry is
200 without another revision, preserving original recorder/time. Changed content
under a used key is 409. Concurrent conflicting revision is 409; retry the same
payload after reread. Missing/other-tenant records are 404. The source must keep
an outbox/reconciliation log; this endpoint alone does not establish delivery.
Never put PHI narratives, credentials or signed download URLs in evidence keys.
No free-text clinical content is needed by this contract.

## Annual draft

`GET /api/authorizations/cms-0057/annual-draft?year=2026&timeZone=America%2FLos_Angeles&lineOfBusiness=Medicaid`

Requires `authorizations:read`. Optional `outcomeCutoff` and `knowledgeCutoff`
accept offset timestamps, default to server now, and cannot be future instants.
Persist **both returned UTC cutoffs**, year, time zone and calculation version to
reproduce an investigation. Cohort boundaries are local January 1 to next January
1, converted to UTC; the upper boundary is exclusive. The tenant comes from the
token; LOB is mandatory and Unknown is rejected. No default tenant, mixed-LOB
total or implicit California/Phoenix calendar is used.

- Immutable receipt scope selects non-drug requests. A receipt recorded after
  the knowledge cutoff does not enter the evidenced denominator. Legacy counts
  are diagnostics only and may contain records whose historical scope is unknown.
  Drug classification reuses `PriorAuthorizationApiScope`: mixed drug/medical
  requests are excluded as a whole under that existing policy. Reporting-specific
  classification and complete medical-item capture need acceptance before use.
- Effective-time cutoff determines which outcomes have occurred. Recording-time
  cutoff determines which evidence CHO had received. Late appeal evidence can
  change a later restatement, while the earlier knowledge cutoff stays available.
  Recording time is a server write-attempt timestamp, not Mongo commit time;
  in-flight commits and cursor reads prevent a transactionally frozen report.
  An approved publication must persist reconciled inputs/output separately.
- Latest evidenced clinical/appeal outcome is counted once per original request.
  All accepted appeal levels can contribute. Same-time contradictory final facts
  are counted as unresolved and explicitly diagnosed. Distinct later facts are
  not silently treated as corrections to earlier evidence.
- Approval, denial and approved-after-appeal rates use all evidenced requests of
  each type, including unresolved requests. No requests means null rates.
- Extended-then-approved counts qualifying pre-initial-determination extension
  evidence and a final approval, once per request; its aggregate denominator is
  all evidenced requests across both types.
- Turnaround is explicitly **receipt to initial determination**, with arithmetic
  mean and exact median in hours. It is a diagnostic, not an implicit compliance
  decision about which appeal-inclusive timing definition belongs in publication.
  Missing timing is counted, not turned into zero hours.

Every result has `publicationReady: false` and acceptance gaps. It does not
publish, certify, include a required-services inventory, establish plan mapping,
or attest all-channel/all-level source completeness. The operational console
retains its separate current-state metrics; its gap messages point to this draft.

## Rollout and outstanding acceptance

1. Review ADR 030 and map tenant/LOB to the actual reporting plan. Approve time
   zone, cohort/timing/restatement policy with compliance.
2. Deploy additive model/index changes before granting evidence permission.
   Avoid old writers during the rollout: a pre-ledger binary doing full-document
   replacement can erase evidence it cannot model. Roll back write traffic only
   after preserving/migrating the evidence, not by blind image reversion.
3. Accept each authoritative PA source and every appeal level/extension event.
   Reconcile counts to intake and source outboxes, including non-API channels.
   Do not grant the reporting role to a broad operator population to mask a
   missing source integration. Failed deliveries must be retried and reconciled.
4. Reconcile legacy receipts/history with provenance, design supersession and
   an approved immutable annual report artifact, freeze inputs, include the
   actual items/services list, and separately authorize public publication.
5. Benchmark intended volume/recovery on the actual Mongo deployment. Cursor
   batches are 250 documents; exact-median memory grows with timed requests.
   The ledger has a 10,000-fact ingestion cap; archive before approaching Mongo
   document limits. Retention is still the owning authorization policy.

## Validation

Meaningful unit cases cover receipt/body protection, invalid timing, exact and
conflicting retries, missing adverse determinations, historical cutoffs, late and
reordered appeals, ambiguous final outcomes, LOB/drug scope, California year
boundaries, open denominators, extension counting and exact median. Real-host
tests verify both required permissions and token recorder. Real-Mongo tests
exercise persistence, stale writes, tenant isolation, duplicate races, legacy
diagnostics and immutable receipt queries.

Local validation on October 24, 2026: the authorization-service unit project
and its dependencies built in Release without warnings/errors; the HTTP test
project built in Debug. The VSTest 17.11.1 host still crashes before execution in
this environment, so selected tests run through the package's real xUnit
`AssemblyRunner` (discovery/theories/fixtures), not a hand-invoked method harness.
97 unit/adjacent reporting, deadline, scope and PA projection cases passed.
Seven real-host HTTP cases passed, including the annual endpoint and both
write permissions. Five new Mongo integration cases compile,
but their shared fixture cannot start `mongod` locally (`open: Operation not
permitted`, exit 100); their bodies and standard CI remain unverified locally.
No warning policy or assertion was weakened to address these environment limits.

Normal reviewer commands:

```bash
dotnet test src/services/authorization-service/AuthorizationService.Tests/AuthorizationService.Tests.csproj
dotnet test tests/CloudHealthOffice.AuthorizationService.Tests/CloudHealthOffice.AuthorizationService.Tests.csproj
```

## Primary interpretation sources

[CMS process/reporting FAQ](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/frequently-asked-questions/improving-prior-authorization-processes),
[CMS metrics overview/template](https://www.cms.gov/prior-authorization-metrics-reporting-overview-template),
[CMS final rule](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/policies-regulations/cms-interoperability-prior-authorization-final-rule-cms-0057-f).
Reviewed October 24, 2026. The FAQ distinguishes original requests from episodes,
requires final appeal outcomes/all levels to be reflected, and starts the decision
clock at receipt across channels. The template is recommended format/guidance;
this draft is not an assertion that a particular cutoff or timing convention is
mandated. Proposed CMS-0062-P changes are not applied as final requirements.

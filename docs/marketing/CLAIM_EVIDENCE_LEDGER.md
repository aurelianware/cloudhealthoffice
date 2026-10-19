# Marketing Claim-Evidence Ledger

**Maintained:** October 19, 2026 (America/Phoenix)
**Verified baseline:** main `d5af561d490f1c440c7cd7fb1098fa3e18d8521c`
**Proposed accountable owner:** Product/engineering; commercial/legal review required for licensing, customer and offer claims. No new licensing terms are proposed.

Use the latest verified implementation. Do not repeat resolved historical gaps. Implementation, standards conformance and production readiness are separate dimensions. This ledger supersedes earlier provisional statements that all DTR storage was durable: creates/read-back are durable, questionnaire update/search remain partial.

The [capability reconciliation](../CAPABILITY_RECONCILIATION.md) contains the exact implementation paths, supporting tests, executed results and remaining limits for each ID. Candidate wording below may be used only within that scope; it is not publication approval.

| Claim ID | Verified on | Status | Evidence ID | Accurate wording / limits |
| --- | --- | --- | --- | --- |
| CAP-01 | 2026-10-19 | Implemented | [Reconciliation CAP-01](../CAPABILITY_RECONCILIATION.md) | Stores mapped request and decision before answering; persistence failure produces OperationOutcome without preAuthRef; inquire round-trips the stored outcome. Full PAS profile validation and production readiness are separate. |
| CAP-02 | 2026-10-19 | Implemented | [Reconciliation CAP-02](../CAPABILITY_RECONCILIATION.md) | Stable line numbers, quantities, A1/A2/A3/A4 outcomes and coded/human-readable adverse reasons are validated before mutation and survive Mongo reload. Item projection is implemented; complete STU2 semantics and X12 reason-code conformance are not established. |
| CAP-03 | 2026-10-19 | Implemented | [Reconciliation CAP-03](../CAPABILITY_RECONCILIATION.md) | Unknown/uncoded input and failed/timed-out evaluation return uncertainty rather than no-PA-needed. An explicit classification can permit no-PA-needed. This is not full CRD conformance; hardcoded context and structured coverage actions are separate limitations. |
| CAP-04 | 2026-10-19 | Partially Implemented | [Reconciliation CAP-04](../CAPABILITY_RECONCILIATION.md) | Creates, response submission and id-based reads use Mongo; real-store tests prove reload across new service instances. UpdateQuestionnaireAsync does not call the durable store; search/existence/update indexes remain in-process. Do not claim all DTR CRUD is durable. |
| CAP-05 | 2026-10-19 | Partially Implemented | [Reconciliation CAP-05](../CAPABILITY_RECONCILIATION.md) | Tenant/client-bound launch consumption, token patient/encounter context and withdrawal of approval affecting refresh are exercised. Do not equate this with every SMART response field, revocation/introspection mechanism, v2 scope or asymmetric-auth requirement. Deployment-specific production validation is unverified. |
| CAP-06 | 2026-10-19 | Partially Implemented | [Reconciliation CAP-06](../CAPABILITY_RECONCILIATION.md) | Computes standard/expedited counts, rates, mean/median and timeliness; NotYetTracked explicitly lists approved-after-appeal and extended-then-approved. Does not establish complete annual public reporting. Legacy repository summaries are not proof that the new calculator is missing. |
| CAP-07 | 2026-10-19 | Implemented | [Reconciliation CAP-07](../CAPABILITY_RECONCILIATION.md) | CHO receipt-v1 persists UTC receipt/due timestamps and urgent/standard targets; RFAI does not restart the clock; worker uses the persisted deadline. Program-specific extensions, tighter policies, notification and per-tenant-database worker support remain separate. |
| CAP-08 | 2026-10-19 | Implemented | [Reconciliation CAP-08](../CAPABILITY_RECONCILIATION.md) | Token-derived tenant is used; body tags are not authority. An unbuilt reference copy is not a deployed production path. |
| CAP-09 | 2026-10-19 | Implemented | [Reconciliation CAP-09](../CAPABILITY_RECONCILIATION.md) | Own tenant plus un-overridden read-only defaults are visible; writes to reserved default library refused. Search completeness across restarts remains CAP-04. |
| CAP-10 | 2026-10-19 | Partially Implemented | [Reconciliation CAP-10](../CAPABILITY_RECONCILIATION.md) | System-context registered-payer binding and mismatched-body rejection implemented. This does not prove receiving-payer-specific consent or recipient-scoped member-match. |
| CAP-11 | 2026-10-19 | Implemented | [Reconciliation CAP-11](../CAPABILITY_RECONCILIATION.md) | Exercises scope/scp handling through HTTP; no inference of all issuer integrations. |
| CAP-12 | 2026-10-19 | Implemented | [Reconciliation CAP-12](../CAPABILITY_RECONCILIATION.md) | Real-Mongo tests cover concurrent insert, cross-tenant number reuse, index creation and legacy duplicate startup failure. No deletion/renumbering implied; current supported Mongo backend only. |
| CAP-13 | 2026-10-19 | Implemented | [Reconciliation CAP-13](../CAPABILITY_RECONCILIATION.md) | Unavailable lookup pends before benefit engine; definitive invalid/not-found denies; PA lookup carries service identity. Does not prove every claim requires PA or fix unrelated coverage/plan token propagation. |
| CAP-14 | 2026-10-19 | Confirmed Gap | [Reconciliation CAP-14](../CAPABILITY_RECONCILIATION.md) | Current implementation immediately completes zero-count jobs and manufactures output URLs without performing export. Passing scaffold tests are not a real export. |
| CAP-15 | 2026-10-19 | Confirmed Gap | [Reconciliation CAP-15](../CAPABILITY_RECONCILIATION.md) | Returns Questionnaire-only collection and does not use patientId for prepopulation. Complete package semantics and clinical logic are not implemented by this method. |
| CAP-16 | 2026-10-19 | Confirmed Gap | [Reconciliation CAP-16](../CAPABILITY_RECONCILIATION.md) | Tenant classifications use expiring IMemoryCache; durable DTR creation does not resolve CRD classification persistence. |
| CAP-17 | 2026-10-19 | Confirmed Gap | [Reconciliation CAP-17](../CAPABILITY_RECONCILIATION.md) | Production methods throw NotImplementedException. CHO-native persistence is not evidence of a live external-core adapter. |
| CAP-18 | 2026-10-19 | Confirmed Gap | [Reconciliation CAP-18](../CAPABILITY_RECONCILIATION.md) | Named asset is a text placeholder. Separate rejection-suggestion/advisory-examiner implementations still exist. |
| CAP-19 | 2026-10-19 | Unverified | [Reconciliation CAP-19](../CAPABILITY_RECONCILIATION.md) | Neither confirmed absence nor completed certification inferred. Requires specification/version, workflow, environment and dated results. |
| CAP-20 | 2026-10-19 | Confirmed Gap | [Reconciliation CAP-20](../CAPABILITY_RECONCILIATION.md) | Service directory still lacks Dockerfile and named workflow entries. Keep as a build gap, distinct from fee engine functionality. |
| CAP-21 | 2026-10-19 | Implemented | [Reconciliation CAP-21](../CAPABILITY_RECONCILIATION.md) | Authorization Mongo updates are revision-fenced; stale concurrent updates conflict. No cross-service transaction guarantee. |
| CAP-22 | 2026-10-19 | Confirmed Gap | [Reconciliation CAP-22](../CAPABILITY_RECONCILIATION.md) | Default FHIR data bindings remain mocks; validate resource-specific source integration without generalizing to CHO-owned authorization persistence. |

## Maintenance

Reverify affected implementation, host registration, behavioral tests and integration paths whenever they change and before publishing stronger claims. Add the date, commit, checks and limits; mark historical issues resolved only for the behavior actually verified. Keep unresolved or unverified assertions visible. Preserve history in the dated reconciliation and Git; do not turn a class, endpoint, test name or old TODO into a maturity verdict.

Licensing remains governed by `LICENSE`: BSL 1.1 source-available, with production use requiring a commercial license. No pricing, savings, customer deployment, conformance or production-readiness claim is established by this ledger. Existing benchmark evidence retains its episode, synthetic-workload and environment qualifications.

| Date | Change |
| --- | --- |
| 2026-10-19 | Reconciled current main implementation, executed focused tests, corrected historical underclaims and preserved confirmed/unknown limitations. |

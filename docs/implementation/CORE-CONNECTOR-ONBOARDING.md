# Core connector onboarding and acceptance

Use this with the [core adapter QRE](../features/questionnaires/qre-core-admin-adapter-questions.md)
and [ADR 027](../adr/027-core-adapter-engagement-boundary.md). CHO is the reference
backend; each QNXT/Facets/HealthEdge connector is a separate payer-specific
implementation. This template does not assert working connectivity, vendor
approval, interface licensing, native completeness or production acceptance.
Record non-secret configuration/evidence references here; exchange credentials,
PHI and customer-specific datasets only through approved engagement channels.

Use the [core data and interface specification](../integration/CORE-DATA-CONTRACT.md)
for required data, standard anchors, actual code seams and SI deliverables.

## 1. Engagement identity and responsibility

| Item | Agreed value / owner / evidence reference |
| --- | --- |
| Payer legal entity, tenant, lines of business, states and environments | |
| Use case: native CHO, coexistence, Augment/shadow or replacement | |
| CHO/backend owner, payer core owner, hosting/BPaaS operator, vendor/partner contacts | |
| QRE revision, unanswered questions, assumptions and explicit exclusions | |
| Supported core product/version/release/patch and upgrade window per environment | |
| Contract/SOW, licensed modules/interfaces, vendor access/support restrictions | |
| Data processing/security responsibilities and approved transfer mechanisms | |
| Scope acceptance, cutover authority, operations and escalation owner | |

For a native CHO deployment, mark incumbent connector work **not in scope** unless
an agreed coexistence/migration/comparison interface requires it. Do not turn an
unused vendor template into a native-core blocker.

For the first QNXT engagement, also complete the [connectivity, security and
partnership design](QNXT-CONNECTIVITY-ENGAGEMENT.md). Access is still pending;
its milestones distinguish a contract harness from a live connector.

## 2. Environment/access matrix

Complete one row per supported environment and interface. Never infer production
access from test connectivity or assume API/database rights from product ownership.

| Environment | Exact version/config baseline | Domain owner | Mechanism and licensed module | Hosting/network/auth owner | Access scope and limits | Evidence / unresolved prerequisite |
| --- | --- | --- | --- | --- | --- | --- |
| Development | | | | | | |
| Integration test | | | | | | |
| UAT | | | | | | |
| Production | | | | | | |

Specify supported API operations/version, approved read views/replica and latency,
extract layouts, EDI versions or event contracts. Record rate limits, pagination,
change detection, service identity, certificate/key rotation, scheduled windows,
recovery objectives and who can authorize writes. A forbidden mechanism is not a
fallback. Record vendor/hosting restrictions before implementation begins.

## 3. Domain and operation scope

| Domain | Source of truth / effective date | Required read/write/event operations | History and change feed | Contract/mapping version | In/out of scope and owner |
| --- | --- | --- | --- | --- | --- |
| Member, dependent identity and eligibility | | | | | |
| Provider, network, credentialing and attribution | | | | | |
| Contracts, benefits, pricing and accumulators | | | | | |
| Claims, adjustments, COB and adjudication results | | | | | |
| Authorizations, decisions, appeals and documents | | | | | |
| Payments, remittance, premium, AR and accounting | | | | | |
| EDI, FHIR and downstream reporting | | | | | |

For each required operation, attach the adapter contract, wire format, identifier
crosswalk, code/value mapping, timezone/date boundary, monetary precision, null
semantics, paging and historical selection. Identify subscriber versus individual
member and claim/version/chain identity explicitly. Record missing or lossy fields
and who accepts each limitation; do not silently manufacture required values.

## 4. Failure and recovery contract

- Distinguish definitive not-found/no-coverage from auth failure, throttling,
  timeout, malformed response, stale source and partial extraction.
- Define retries, timeout budgets, backoff, idempotency keys, duplicate/out-of-order
  handling, replay/checkpoint ownership, retention and dead-letter resolution.
- Define reconciliation counts/totals and control records per extraction/batch;
  require financial balancing and investigated exceptions where money moves.
- Agree degraded behavior per operation: explicit unavailable, pend, queued work,
  or operator action. No silent reference-backend or synthetic fallback.
- Define correction, rollback and compensating actions for authorized writes;
  record whether a retried request can change an incumbent result.
- Validate member/tenant confinement, minimum field disclosure and audit identity.
  Document log sanitization and privileged operations without storing secrets.

## 5. Traceable acceptance evidence

| QRE requirement | Contract/operation | Fixture or payer-approved case | Expected result / dollar or count control | Negative/recovery case | Environment + version + connector commit | Evidence and acceptance owner |
| --- | --- | --- | --- | --- | --- | --- |
| | | | | | | |

Required gates before claiming an engagement is ready:

1. Discovery/licensing/access assumptions approved; unresolved blockers named.
2. Connector contract tests pass for mappings, identity and date/money semantics.
3. Payer integration tests prove real authorized connectivity and representative
   operations, including tenant/member negative cases and outage behavior.
4. Initial historical load and incremental updates reconcile; replay/retry does
   not duplicate changes, decisions, claims or money. Resolve exceptions explicitly.
5. UAT and operational recovery pass in the agreed version/configuration; record
   deployment, secret rotation, monitoring, alerting and incident ownership.
6. Cutover/rollback plan approved; production verification recorded separately.

Use separate statuses for **native reference capability**, **interface contract**,
**engagement connector implementation**, **environment connectivity**, and
**deployed acceptance**. Suggested engagement stages: Discovery, Contract agreed,
Implemented, Test connected, UAT accepted, Production accepted, Suspended.
Do not label the entire connector ready based on one successful API request.

## 6. Cutover and support handoff

Record migration checkpoints, balanced opening amounts, backlog and dual-run
resolution; owner/signoff for every acceptance exception; rollout/rollback
triggers; on-call contacts and SLAs agreed in the SOW; vendor/core upgrade
compatibility policy; regression corpus and retest responsibility; configuration
promotion and evidence retention. Keep status current after a core upgrade,
license/interface change, hosting migration or material payer customization.

A clearinghouse-style QRE process is the discovery analogy, not a statement
that different vendors require the same connector, effort or certification.

# Core integration data and interface specification

Design baseline: October 22, 2026. Audience: payer architects, system integrators
(SIs), connector developers and acceptance owners. This is the vendor-neutral
requirements baseline, not a claim that every interface below is implemented.
Use with [ADR 027](../adr/027-core-adapter-engagement-boundary.md), the
[QRE](../features/questionnaires/qre-core-admin-adapter-questions.md) and
[onboarding worksheet](../implementation/CORE-CONNECTOR-ONBOARDING.md).

## 1. What the SI delivers

For each selected use case, deliver a signed operation/data matrix, versioned
source-to-target field mappings, a connector using the applicable existing CHO
seam, and executable tests with representative payer-approved evidence. Where no
suitable CHO seam exists, design and review one explicitly before implementation.
Do not invent a universal HTTP API from the conceptual operation names here.

The same target semantics apply to QNXT, Facets and HealthEdge. The source API,
view, event or extract is payer/version specific. Never guess vendor tables or
operation names. An EDI transaction or FHIR resource is an exchange model, not
proof it contains all the data needed for adjudication, accounting or migration.

Choose a scope per domain: query-through, replicated read model, inbound business
transaction, outbound submission, migration, or CHO-native only. Record one write
authority per operation and a conflict-resolution owner. Native-only domains do
not require an incumbent connector.

## 2. Existing code integration points

Paths below are actual repository seams; their DTOs and executing callers are the
code contract. This guide adds acceptance requirements, not runtime behavior.
Method-level semantics, the CMS-0057-F data path and the conformance suites are in
the [core adapter implementer guide](CORE-ADAPTER-IMPLEMENTER-GUIDE.md).

| Integration point | Existing code | Actual operations / boundary |
| --- | --- | --- |
| Eligibility inquiry | [IEligibilityAdapter](../../src/services/eligibility-service/Adapters/IEligibilityAdapter.cs) | `VerifyEligibilityAsync`; subscriber/dependent, service dates/type, provider, normalized coverage/benefit/accumulator response. This is an inquiry adapter, not a general enrollment write API. |
| Provider directory | [IProviderAdapter](../../src/services/provider-service/Adapters/IProviderAdapter.cs) and [IOrganizationAdapter](../../src/services/provider-service/Adapters/IOrganizationAdapter.cs) | Provider/organization lookup and search; provider network roster. Review each selected operation's implementation; a method declaration alone is not acceptance. |
| Benefits | [IBenefitPlanAdapter](../../src/services/benefit-plan-service/Adapters/IBenefitPlanAdapter.cs) | `GetPlanAsync`, `GetPlanVersionAsync`, `GetMemberBenefitViewAsync`; published version and service-date selection. |
| Claims | [IClaimAdapter](../../src/services/claims-service/Adapters/IClaimAdapter.cs) | Get by ID/number, version lookup/history, search/member search and submission. Native adjudication writes/event emission are deliberately outside this adapter; do not assume submission performs vendor adjudication. |
| Authorization authority | [IAuthorizationBackend](../../src/services/authorization-service/Backends/IAuthorizationBackend.cs) | Create, lookup by number, status transition and trusted automated-decision recording. Preserve append-only history. `IsAuthoritative` distinguishes native ownership from fronting a core. |
| FHIR read/projection | [IFhirDataAdapter](../../src/services/fhir-service/Services/IFhirDataAdapter.cs) and [native bindings](../../src/services/fhir-service/Services/ChoNative/FhirDataBindings.cs) | Resource projection/serving boundary; does not replace domain writes or authorize arbitrary upstream records. |
| Finance event ingestion | [GL event contracts](../../src/services/shared/CloudHealthOffice.Finance.Contracts/GlSourceEvents.cs) | Typed event envelopes and lifecycle semantics, not a universal premium/ERP connector. Confirm produced versus reserved events against the chosen release. |
| Enrollment, pricing contracts, premium and receivables integration | Engagement design required beyond seams above | Identify the actual service/controller/message contract and its auth/idempotency behavior in the selected release before coding. Do not assume one generic incumbent adapter covers these domains. |

A gap between required data and a current DTO is an explicit contract change
request. Do not silently drop it, put it in an undocumented string, or declare the
connector complete. Reference a code commit in every accepted operation matrix.

## 3. Common field semantics

These requirements apply to the selected scope. “Required” below means the
integration must supply it or explicitly block/pend the dependent operation; it
does not redefine an X12 TR3 or FHIR profile cardinality.

| Data | Required semantics |
| --- | --- |
| Tenant / payer | Trusted routing context and payer identifier namespace; reject cross-tenant identifiers. Never derive authorization from a caller-supplied tenant field alone. |
| Business identifiers | Preserve source system, identifier type and value; maintain crosswalks for member/subscriber, group, plan/version, provider/location, claim chain/version, authorization and payment. Do not use an NPI alone as a contract/location identity. |
| Dates | Distinguish service date, coverage/contract effective dates, source transaction time, source update time, ingestion time and requested historical as-of time. Record timezone, date-only boundaries and inclusive/exclusive end-date rules. |
| Status | Map every source value to a named target value, preserving original code. Unknown, not found, stale, pending and upstream unavailable are different results. A failed source lookup is not a negative determination. |
| Codes | Record code system, version, code and applicable display; mapping owner for local codes. Preserve procedure modifiers, diagnosis qualifiers and units. No unreviewed default code substitutions. |
| Money / quantities | Currency and decimal precision, rounding point/mode, amount sign, units, allowed versus paid versus patient liability, original versus corrected values. No floating-point money or missing-to-zero conversion. |
| Provenance | Source record/version, operation or batch ID, mapping version, source update time, ingestion watermark and accepted connector commit. Carry correlation/audit identity without putting PHI in logs. |
| Missing data | Document required, conditional or optional per operation. Null/unknown is not zero, false, no coverage or deleted. Conditional requirements must state the condition. |

For historical queries, distinguish the business effective date from what the
system knew at a prior time. A current plan-year accumulator is not a historical
service-date balance. If the source cannot meet a query's semantics, return an
explicit limitation or unavailable result; do not interpolate history.

## 4. Domain data requirements and standard anchors

The entries are a discovery minimum. Expand each into field-level mappings and
requiredness for the agreed use case using section 5. Pin the exact X12
implementation guide/errata and companion guide, or FHIR package/profile version.
Base FHIR R4 is 4.0.1; a resource name alone is not a conformance contract.

| ID / use case | Required data groups | Standards anchors and limits | Minimum acceptance cases |
| --- | --- | --- | --- |
| MEM-01 identity/enrollment | Individual member and subscriber IDs, relationship, demographics needed for match, group, product/plan/version, coverage intervals, status/reason and changes/termination | X12 834 for enrollment maintenance where applicable; FHIR Patient and Coverage for exchange. Capture source enrollment history separately; a card-level Coverage is not the complete enrollment ledger. | Dependent versus subscriber; duplicate/merged identities; retro termination/reinstatement; overlapping coverage; wrong tenant |
| ELG-01 service-date inquiry | Member/subscriber/dependent match inputs, payer/provider, service date/range and service type; active/inactive result, coverage period, plan, benefit categories, network tier, cost share, limits, accumulator period/availability and COB coverage order | X12 270/271; FHIR CoverageEligibilityRequest/Response where selected. Coverage alone does not establish a service-specific benefit determination. | Prior/current/future date; multiple coverages; explicit inactive; not found; source outage; stale snapshot; unavailable historical accumulator |
| PRV-01 directory/network | Source provider ID, NPI/type, organization, service location, specialty/taxonomy, roster and effective-dated network/plan participation; accepting-patients and credentialing data only when the use case requires it | FHIR Practitioner, PractitionerRole, Organization, Location and HealthcareService as applicable. No general HIPAA transaction here supplies a complete network or credentialing system. | One NPI/multiple locations/contracts; terminated participation; network and line-of-business filters; historical roster |
| BEN-01 benefit configuration | Product/plan/version, coverage/effective periods, service category/code/modifier mappings, covered/excluded services, network tiers, copay/coinsurance/deductible/OOP rules, family/individual basis, limits, utilization and authorization rules | FHIR InsurancePlan and CoverageEligibilityResponse may express selected plan/benefit data. X12 271 is not a complete executable benefit configuration or adjudication rule export. | Midyear plan revision; individual/family limits; in/out-of-network; excluded service; missing code mapping |
| CON-01 pricing/contract | Provider/contract/location identifiers, participation dates, fee schedule/version, procedure/modifier/POS scope, rate method/amount/percentage, precedence, carve-outs, capitation terms and amendments for selected pricing scope | No universal X12/FHIR artifact is assumed to carry executable payer pricing rules. FHIR Contract is not by itself a pricing engine specification. Use a licensed contract API or agreed extract and CHO rule mapping. | Overlapping contracts/precedence; rate changes by service date; modifier rules; missing rate pends; pricing dollar parity |
| CLM-01 submission/history | Payer claim number, source chain and version IDs, member/coverage/provider links, claim type, bill/service dates, diagnoses/procedures/modifiers/revenue/POS/units/charges, prior authorization, COB and original/replacement/void references | X12 837 variants per selected claim type; FHIR Claim for selected exchange. Claim version/adjustment history must come from authoritative history, not be inferred from a single 837. | Original/replacement/void; same claim number with versions; line identity; COB; duplicate submission; historical query |
| CLM-02 adjudication/status | Header and line status, decision date, allowed/paid/patient liability and adjustment/reason codes, benefit accumulations used, denial/pend reasons, payment links, reversals and adjudication history | X12 276/277 for status, 835 for remittance; FHIR ClaimResponse and appropriate EOB profile for exchange. Submission acknowledgment is not adjudication or payment. | Partial denial; pend; reversal/reissue; zero paid; paid with offsets; totals reconciled at line/header/run |
| AUT-01 authorization | Tracking/source IDs, member/provider, requested services/units/dates, urgency/type, received and decision times, line decisions/approved units, denial reasons, supporting document references, status history; appeals/extensions if reporting requires them | X12 278 and/or agreed FHIR PAS Claim/ClaimResponse workflows; DocumentReference/Binary or approved document transfer as applicable. Pin guide versions and routing; never assume automatic equivalence of all fields. | Approved/partial/denied/pending; correction/cancel; duplicate submission; document unavailable; appeal/extension provenance; timestamp reconciliation |
| PAY-01 claims payment | Payment/payee/run/claim-line IDs, gross/net/offset amounts, method, trace/check, issue/transmit/settle/return dates/status, bank references, remittance and ledger linkage | X12 835 for remittance and associated EFT trace matching; bank/NACHA/BAI2 interfaces have their own contracts. An 835 is not bank settlement confirmation. | Remittance/EFT match; duplicate bank line; unbalanced run; returned/voided/reissued payment; out-of-order events |
| PRE-01 premium | Account/group/member links, policy/coverage, billing period, rating basis/version, invoice/line IDs, billed/due/adjusted amounts, due dates, grace/termination rules, receipts and allocation history | X12 820 for applicable premium payment/remittance exchange. It does not replace rating, invoice generation or the complete premium subledger. Other invoice/receipt contracts require explicit design. | Retro coverage/rate changes; partial/unapplied/overpayment; adjustments; cancellation; invoice-to-cash reconciliation |
| AR-01 receivables/GL | Open-item/invoice and debtor IDs, original/due date, original/open amounts, currency, applications/reversals/write-offs, period/cutover, journal source/business keys and ERP account/dimension mappings | Explicit subledger/ERP interface; FHIR Account/Invoice resources alone do not define CHO posting semantics. Aggregate balance buckets cannot establish invoice age. | Due-date aging; partial receipt; unapplied cash; reversal; closed period; duplicate event; balanced opening and closing totals |
| CLN-01 clinical/supporting data | Patient/provider references, encounters and clinical resource IDs, status/effective time, code systems, document metadata/content access, provenance and corrections | Selected US Core/PDex profiles and supporting-document contracts. Claims-derived clinical facts do not replace records the payer actually maintains. | Missing references; amended/deleted record; terminology validation; document authorization; patient/tenant confinement |

FHIR serving profiles are purpose-specific: patient-directed financial EOB data
must not be reused indiscriminately for provider/payer access. Apply consent,
attribution, opt-out, data minimization and recipient policy at the serving
boundary. Backend service credentials do not grant an external requester access.

## 5. SI field mapping deliverable

Create one mapping table per operation, including every DTO/profile-required field
and every data element needed for the agreed business decision. Pin it to an
immutable source contract and CHO commit. No empty required cells at acceptance.

| Requirement ID | CHO operation + DTO/model path | Required/conditional/optional + condition | Type/cardinality/unit | Source vendor/version + API field or extract column | X12 guide/loop/segment/element OR FHIR package/profile/path | Transformation/code map/default policy | Effective-time and null rules | Owner + fixture + expected output | Status/blocker |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| ELG-01.member | IEligibilityAdapter / EligibilityAdapterRequest.MemberId | Conditional: member-specific lookup | string / 0..1 | Payer/SI to resolve from licensed contract | Identifier crosswalk to selected standard's member identity | Preserve source namespace; no subscriber substitution for dependent | Missing identity rejects/requests match inputs | SI / ELG-dependent-01 | Unresolved until discovery |
| ELG-01.date | IEligibilityAdapter / EligibilityAdapterRequest.ServiceDate | Required | date-time in current DTO; agreed service-date semantics | Payer/SI to resolve | Selected 270/271 TR3 service-date field or FHIR request serviced[x] mapping | Explicit timezone/date conversion | No substitution of current date for a supplied historical date | SI + payer / ELG-history-01 | Unresolved until discovery |
| ELG-01.coverage-start | IEligibilityAdapter / EligibilityAdapterResponse.CoverageBeginDate | Conditional: known coverage interval | nullable date-time | Payer/SI to resolve | Selected 271 TR3 date field or Coverage.period.start | Preserve boundary convention | Null means unknown, not coverage since minimum date | SI + payer / ELG-retro-01 | Unresolved until discovery |

Examples above are mapping tasks, not completed QNXT/Facets/HealthEdge mappings.
Do not guess X12 loop/element positions: SI fills them from the licensed TR3 and
payer companion guide, including situational rules. Add lossiness findings and
contract-change requests wherever CHO and the source differ.

## 6. Operation, transport and delivery contract

For every operation record:

1. Consumer and producer; read/write authority; actual interface/method/route;
   request/response schema version; licensing and environment prerequisites.
2. Selection semantics: identifiers, filters, effective date/as-of, sort order,
   page limits and continuation tokens. Prove completeness under concurrent updates.
3. Security: trusted tenant binding, service identity, token audience/scopes or
   vendor mechanism, network path and disclosure policy; follow the
   [QNXT security worksheet](../implementation/QNXT-CONNECTIVITY-ENGAGEMENT.md).
4. Success, definitive not-found, invalid request, auth failure, conflict,
   throttling, timeout, stale/partial data and unavailable behavior. Record actual
   wire status/error mappings; do not invent a common HTTP status for non-HTTP APIs.
5. Timeout/latency and freshness budgets, rate/concurrency limits, retries with
   backoff, idempotency and cancellation. Specify whether timeout means outcome
   unknown for a write and how to query/reconcile before retrying.
6. Audit/correlation, source version, mapping version, observability and incident
   ownership. Redact sensitive payloads; use approved restricted evidence storage.

For scheduled data feeds, additionally define initial load scope/history, schema,
encoding, transfer mechanism, batch/file ID, source watermark, extraction window,
full versus delta semantics, deletion/tombstone behavior, late/retroactive changes,
record and financial control totals, checksum, completion marker, quarantine,
restart checkpoint and reconciliation procedure. Partial delivery must not replace
a complete prior snapshot. Agree whether reads serve the last complete snapshot
with an explicit age or fail unavailable. EDI acknowledgments and business
application/reconciliation checkpoints are tracked separately.

For events, specify schema/version, stable event ID, business key, tenant,
aggregate ID/version, occurrence/effective time and provenance; delivery ordering,
replay/retention and dead-letter recovery. These are design requirements, not a new
universal envelope imposed on existing CHO contracts. Financial events must use
actual accepted finance contracts and balance to source business documents.

### Fill-in operation specification (copy once per selected operation)

```text
Requirement/operation ID:
Use case and business acceptance owner:
Mode and authoritative system:
CHO code seam, DTO/model paths and commit:
Source product/version/config, licensed interface and operation:
Direction and write authority:
Environment and non-secret endpoint/config reference:
Request schema and validation / requiredness:
Response schema and definitive not-found semantics:
Identifier namespaces and crosswalk owner:
Field-mapping document revision:
X12 TR3 + errata + companion guide OR FHIR package/profile/dependencies:
Business effective-date and historical as-of semantics:
Source timestamp, freshness budget and stale-data behavior:
Paging/sorting/completeness and change/deletion semantics:
Auth identity, audience/scopes or vendor method; network dependency:
Timeout, throughput/rate limit, retry and unknown-write-outcome recovery:
Idempotency/business key and duplicate/order behavior:
Error mappings, quarantine/replay and operator:
Control totals and reconciliation frequency/exception owner:
Synthetic tests and connected test evidence:
Unsupported fields/operations and accepted limitations:
Cutover/rollback, runtime operator and support escalation:
Status: Discovery / Contract agreed / Implemented / Test connected /
        UAT accepted / Production accepted / Suspended
```

## 7. Architect and SI acceptance pack

| Artifact | Acceptance owner and criterion |
| --- | --- |
| Context/data-flow diagram | Architect: domains, authority, trust zones, transports, ingress/egress and PHI boundaries identified |
| Operation catalog | Architect + payer: selected code seams/routes, interface versions, scopes, unsupported operations and dependencies resolved |
| Field/code/identifier mappings | Domain owner + SI: required data complete, date/money/status semantics accepted, lossy cases explicit |
| Synthetic contract tests | CHO + SI: deterministic mappings, negative cases, version compatibility; no claim of live source connectivity |
| Connected integration evidence | Payer + SI: authorized real test endpoint/version and representative operations, auth/outage/freshness tests |
| Reconciliation and replay evidence | Finance/domain owners: counts and dollars balance; duplicates, restart, out-of-order and retro corrections handled |
| Operations/cutover pack | Named runtime operator + payer: secrets rotation, monitoring, lag thresholds, incident/replay ownership, dual run, rollback and support agreement |

Every evidence row includes requirement ID, source version/configuration,
connector commit, test case, expected/actual result, evidence location and owner.
Unsupported source data is a visible dependency/blocker with an agreed resolution,
not an implementation success. Publication of a contract or test fixture alone
establishes neither vendor connectivity nor CMS conformance.

## 8. Standards references and version control

- [CMS adopted transactions and operating rules](https://www.cms.gov/priorities/key-initiatives/burden-reduction/administrative-simplification/hipaa/adopted-standards-operating-rules): confirm applicable adopted X12 versions, transaction use and operating rules; use licensed TR3s and payer companion guides for exact mappings.
- [FHIR R4 resource definitions](https://hl7.org/fhir/R4/resourcelist.html), [Coverage](https://hl7.org/fhir/R4/coverage.html) and [CoverageEligibilityResponse](https://hl7.org/fhir/R4/coverageeligibilityresponse.html): reference models; not a substitute for profile and business requirements.
- [CARIN Blue Button](https://hl7.org/fhir/us/carin-bb/), [Da Vinci PDex](https://hl7.org/fhir/us/davinci-pdex/) and [Da Vinci PAS](https://hl7.org/fhir/us/davinci-pas/): purpose-specific profiles/workflows. These landing pages can change; pin published package versions and dependencies in the engagement contract and validate against that set.

This guide does not automatically adopt the newest guide version or change CHO's
conformance packages. Record deviations and planned upgrades explicitly. Do not
copy licensed X12 implementation guides or proprietary vendor specifications into
the repository.

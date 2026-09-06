# Payer-to-Payer Exchange

CMS-0057-F Payer-to-Payer as Cloud Health Office implements it: the inbound
respond path, `$member-match`, outbound initiation, and durable ingestion of what
comes back. Everything below lives in `fhir-service`.

Acceptance scenarios: **P2P-01** (inbound respond), **P2P-02** (outbound initiate
+ ingest), **P2P-03** ([consent enforcement](#consent)), **P2P-04**
(`$member-match` / concurrent coverage).

## Topology

```
CHO as PRIOR payer (inbound)                CHO as NEW payer (outbound)
─────────────────────────────               ──────────────────────────────────────
POST Patient/$member-match                  coverage transition
  └─ PayerToPayerMemberMatchService           └─ PayerToPayerOutboundService
POST PayerToPayer/$member-data-export              ├─ member + prior coverage (CHO data)
  └─ PayerToPayerExchangeService                   ├─ IPayerToPayerEndpointResolver
                                                   ├─ IPayerToPayerConsentGate
                                                   │    └─ ConsentAuthorizationPolicy
                                                   │         over consent-service registry
                                                   ├─ IPayerToPayerRemoteClient
                                                   │    ├─ remote $member-match
                                                   │    └─ remote member-data export
                                                   ├─ PayerToPayerResponseReader
                                                   │    (parse, member-consistency, Provenance)
                                                   └─ IPayerToPayerPackageIngestionService
                                                        └─ IPayerToPayerImportRepository
                                                             (Mongo, or in-process)
```

Both directions share one wire format: CHO calls the same two operations it
serves. There is no second Payer-to-Payer transport.

## Outbound order of operations

Fail-closed, and nothing leaves CHO until every local gate has passed:

1. **tenant scope** — a member of another tenant is never initiated on;
2. **member + prior-payer coverage context** — from CHO's own data; overlapping
   coverages with the target payer refuse rather than guess;
3. **endpoint resolution** — `IPayerToPayerEndpointResolver` maps a *payer id* to
   endpoints from trusted configuration. A caller never supplies a URL, non-HTTPS
   entries are rejected, and the HTTP client does not follow redirects. This is
   the SSRF boundary;
4. **authorization** — `IPayerToPayerConsentGate` asks CHO's consent registry
   whether the member authorized the **Payer-to-Payer purpose**, server-side.
   Enforced *before* any remote call, so an unauthorized member's identity is
   never disclosed. Re-asked again before the export — see [Consent](#consent);
5. **remote `$member-match`** — CHO builds the request and interprets the answer;
   it does not re-run the peer's matching rules. No match or an ambiguous match
   is terminal;
6. **member-data export** — issued only after exactly one member resolves *and*
   consent is re-confirmed as of that moment;
7. **validation** — the Bundle must parse, carry exactly one Patient, and that
   Patient (and every `Patient/…` reference, relative or absolute) must be the
   matched member;
8. **provenance** — the package is stamped with a `Provenance` naming the source
   payer, exchange, and receipt time;
9. **durable ingestion** — below. The exchange reaches `Completed` **only** once
   the import commits.

### Exchange states

`Pending → Matching → Matched → RequestingData → DataReceived → Ingesting →
Completed`, with terminal `NoMatch`, `Ambiguous`, `NotAuthorized`, and `Failed`.

`DataReceived` exists so "retrieved but not stored" is a state the system can be
in and report. Retrieval alone never reads as success.

## Consent

The consent model itself — purposes, lifecycle, the policy, the registry, and
migration — is documented in [Consent](consent.md). What matters here is how the
exchange uses it.

**Payer-to-Payer has its own purpose.** Only
`ConsentPurposeOfUse.PayerToPayerExchange` authorizes an exchange. An Active
consent is not sufficient by itself: a member with a Provider Access consent, or
with a consent carrying no purpose, is denied with `NoConsentForPurpose`. The
required purpose is a constant on `ConsentRegistryPayerToPayerConsentGate`, not
configuration.

**Both directions ask the same registry.** Inbound
(`PayerToPayerExchangeService`) and outbound (`PayerToPayerOutboundService`) call
one `IPayerToPayerConsentGate` over one `ConsentAuthorizationPolicy` against
`consent-service`. There is no P2P-local consent store and no direction-specific
rule, so responding cannot become more permissive than initiating.

**Consent is never supplied by a caller.** No request type in this surface has a
consent field — not the inbound `$member-data-export` request, not the outbound
initiation request. A receiving payer cannot self-attest, and neither can an
internal caller.

**Evaluated at the attempt, not carried forward.** Outbound evaluates before the
remote `$member-match` and again immediately before the export, so a revocation
landing while the match is in flight stops the data request. A retry clears the
recorded decision and re-asks. Full semantics, including what a revocation does
*not* undo, are in
[Revocation semantics](consent.md#revocation-semantics).

**The exchange records what authorized it.** `AuthorizingConsentId`,
`ConsentDecisionReason`, and `ConsentEvaluatedAtUtc` are written on the exchange,
and the consent id and reason on both audit entries — so a completed exchange
names the consent record that permitted it and a refused one names why.

Denials are terminal `NotAuthorized`, distinct from `Failed`: a member who has
not authorized the exchange is not an error to retry.

## Ingestion

`PayerToPayerPackageIngestionService` receives an already-validated package and
never contacts a payer itself.

### Where imported data lives

In its own store (`IPayerToPayerImportRepository`), separate from CHO's
authoritative member, enrollment, claim, and provider stores. **Source ownership
is structural, not a convention**: an imported row physically cannot be read as a
CHO-owned record, so "did CHO originate this?" is answered by which store the
data lives in.

MongoDB backs it when `MongoDb:ConnectionString` is configured; otherwise an
in-process implementation is used, the same Demo-mode fallback `DtrService` takes.

### Supported resource types

Taken from what CHO's FHIR surface **actually serves**, not from the CMS wish
list:

| Class | Types | Treatment |
| --- | --- | --- |
| `MemberHistory` | `ExplanationOfBenefit`, `Claim`, `ClaimResponse`, `Encounter`, `DocumentReference` | Ingested as the member's imported history |
| `AdministrativeReference` | `Patient`, `Coverage`, `Organization`, `Practitioner`, `PractitionerRole`, `Provenance` | Stored for reference resolution and traceability **only** |
| `ClinicalRecord` | the USCDI clinical set — `AllergyIntolerance`, `CarePlan`, `CareTeam`, `Condition`, `Device`, `DiagnosticReport`, `Goal`, `Immunization`, `MedicationDispense`, `MedicationRequest`, `Observation`, `Procedure` | Ingested **and served** through Patient / Provider Access (PAT-02) |
| `Rejected` | a clinical resource the payload validator refused (no source id, oversized, too deeply nested) | Counted and **named by reason** on the exchange; still in the archived package |
| `Unsupported` | everything else (`RiskAssessment`, `NutritionOrder`, …) | Counted, **named on the exchange**, and preserved in the archived package |

Unsupported types are never silently dropped, and CHO never claims to have
ingested a type it cannot serve. The clinical list is not restated here in code:
`PayerToPayerImportPolicy` asks `ClinicalResourceInventory`, the same table the
FHIR routes and the CapabilityStatement read, so a type is ingested exactly when
it is served. See [Clinical FHIR resources](clinical-fhir.md).

Clinical resources pass a payload gate the reference-only administrative context
does not need — type, source id, size and nesting limits — because they become
readable PHI on CHO's own FHIR surface. A refusal is per resource: one oversized
Observation does not cost the member the rest of their history.

### Ownership and reconciliation

* the remote `Patient` establishes source-side identity **only** — it never
  replaces CHO's authoritative member record;
* a prior payer's `Coverage` never touches the member's current CHO enrollment;
* administrative resources keep the peer's resource id as `SourceResourceId`
  while being filed under CHO's own member id.

### Identity and deduplication

Import key = SHA-256 of
`tenant + local member + source payer + resource type + source resource id`
(joined with a unit separator, so no two tuples collide by concatenation).

* replaying a package resolves to the same keys — the read collapses them to one
  version per key, so history does not double;
* **the same source id from a different payer is a different key** — two payers'
  records are never merged;
* a content hash distinguishes "same again" from "changed";
* each exchange's own `Provenance` stamp is its own record, so which exchange
  delivered what stays recoverable.

### Atomicity

Rows are **versioned by exchange**: a row is identified by
(tenant, exchange, import key), and reads return, for each import key, the
version from the **most recently committed** exchange.

Staging writes only that exchange's own rows; committing is a single-document
ledger write. Both halves of the guarantee follow, without needing a
multi-document transaction:

* a failed ingestion **adds nothing visible** — its rows belong to an uncommitted
  exchange;
* a failed ingestion **takes nothing away** — it cannot overwrite or hide the
  version an earlier exchange committed, so the member keeps the history they
  had. An updated resource supersedes the older version only once the exchange
  carrying it commits.

A retry re-stages the same deterministic keys under the same exchange and
commits.

### Retry and stuck exchanges

A previously failed exchange is retried under its own id. An exchange abandoned
in a non-terminal state (a process that died after recording `Ingesting`, say) is
taken over once it is stale — otherwise every later initiation would replay a
record that can no longer advance itself. An exchange that is genuinely still
running is replayed rather than re-run, so the peer is not called twice.

### Provenance

The package is archived **as received** — before CHO rewrites any reference — so
the archive answers "what did the payer actually send?".

Per imported resource CHO retains: originating payer, endpoint directory key,
exchange id, received timestamp, the resource's identity at the source, the local
member, and the tenant — plus `IngestedAtUtc`, which is CHO's own act, distinct
from receipt. The `Provenance` resource stamped during the exchange is itself
stored.

### Reference normalization

A reference is rewritten **only** when it resolves to another resource in the
same package, and only to `PayerToPayerImport/{importKey}`. Relative and absolute
forms both resolve; versioned (`/_history/n`) references resolve to the same
resource. References to resources the peer did not send, contained (`#…`)
references, and `urn:uuid` forms are left exactly as they arrived — CHO does not
invent links the source payer never asserted.

## Security and PHI

* target payers come from configuration; a caller names a payer id, never a URL;
* tenant, member, and source payer on every stored row come from the exchange
  context — identifiers **inside** the peer's Bundle never redirect an import;
* logs and audit carry ids, categories, and counts only: no Bundle bodies, no
  demographics, no clinical payloads, no endpoint URLs, no credentials. CR/LF is
  stripped from any id that reaches a log line (CWE-117).

## Limitations

* Consent must be **recorded with the Payer-to-Payer purpose** before any
  exchange is authorized. Pre-existing generic consent does not carry it and is
  not reinterpreted, so a deployment upgrading to this code authorizes nothing
  until purposes are recorded — see
  [Migration](consent.md#migration-and-backward-compatibility).
* Imported **clinical** data is surfaced through CHO's FHIR read APIs as of
  PAT-02 — the import store is also the clinical serving store, so there is no
  projection between them (see [Clinical FHIR resources](clinical-fhir.md)).
  Imported member **history** (EOB, Claim, ClaimResponse, Encounter,
  DocumentReference) is still durable and queryable only through the import
  repository; surfacing it alongside CHO's own claims history is follow-up work.
* Types outside the clinical and member-history inventories are archived, not
  ingested. They are named and counted rather than dropped.
* Transport credentials for a specific payer (SMART Backend Services / UDAP
  client registration, mTLS) remain deployment integration behind
  `IPayerToPayerCredentialProvider`, which supplies none by default.
* No external-core (QNXT/Facets/HealthEdge) Payer-to-Payer integration exists.

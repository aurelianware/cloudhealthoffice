# Clinical FHIR resources (USCDI)

How Cloud Health Office stores and serves the USCDI **clinical** resource types
through Patient and Provider Access.

Acceptance scenario: **PAT-02** (Patient Access — US Core clinical). Related:
**PAT-01** (member claims / CARIN EOB), **PROV-01/02/03** (Provider Access),
**CONSENT-01** (one consent registry), **P2P-02** (Payer-to-Payer ingestion),
**SEC-01** (SMART/OAuth).

## What changed, and why

PAT-02 was PARTIAL for a specific reason. CHO could *receive* a prior payer's
Condition, Observation, Procedure and the rest — the Payer-to-Payer pipeline
validated them, counted them, named them, and archived the package verbatim —
but it had nowhere to put them and no way to serve them. They were classified
`Unsupported`, because claiming to ingest a resource type with no read path
behind it would have been a false claim.

That gap is now closed at its root: the clinical types have durable
member-scoped storage, a standards-correct FHIR read path, and both
authorization boundaries. Nothing about the honesty rule changed — a type is
ingested exactly when it is served, and both facts come from one table.

---

## 1. The resource inventory

`ClinicalResourceInventory` (`src/services/fhir-service/Services/Clinical/`) is
the **single source of truth**. Twelve types:

| FHIR R4 type | USCDI data class(es) | subject element | search parameters |
| --- | --- | --- | --- |
| `AllergyIntolerance` | Allergies and Intolerances | `patient` | `_id`, `patient` |
| `CarePlan` | Assessment and Plan of Treatment | `subject` | `_id`, `patient`, `subject` |
| `CareTeam` | Care Team Members | `subject` | `_id`, `patient`, `subject` |
| `Condition` | Problems, Health Concerns | `subject` | `_id`, `patient`, `subject` |
| `Device` | Unique Device Identifiers | `patient` | `_id`, `patient` |
| `DiagnosticReport` | Laboratory, Diagnostic Imaging | `subject` | `_id`, `patient`, `subject` |
| `Goal` | Goals | `subject` | `_id`, `patient`, `subject` |
| `Immunization` | Immunizations | `patient` | `_id`, `patient` |
| `MedicationDispense` | Medications | `subject` | `_id`, `patient`, `subject` |
| `MedicationRequest` | Medications | `subject` | `_id`, `patient`, `subject` |
| `Observation` | Laboratory, Vital Signs, Smoking Status, Clinical Tests | `subject` | `_id`, `patient`, `subject` |
| `Procedure` | Procedures | `subject` | `_id`, `patient`, `subject` |

**Where the list comes from.** The USCDI clinical data classes this repository
already documents as CMS-0057-F obligations
([docs/features/CMS-0057-F-COMPLIANCE.md](../features/CMS-0057-F-COMPLIANCE.md),
"USCDI Data Classes"), mapped onto their US Core R4 resource types, **minus**
the classes CHO discharges elsewhere:

| USCDI class | already served by |
| --- | --- |
| Patient Demographics | `Patient` (`PatientController`, PAT-01) |
| Health Insurance Info | `Coverage` (`CoverageController`) |
| Coverage / claims | `ExplanationOfBenefit` (CARIN BB, PAT-01) |
| Clinical Notes | `DocumentReference` (`DocumentReferenceController`) |
| Provenance | resource metadata — see §6 |

`subject` is advertised **only** where FHIR R4 defines it. AllergyIntolerance,
Device and Immunization define `patient` alone, so a `subject` on those is
refused rather than silently ignored. A test asserts each entry against the
Firely `ModelInfo` search-parameter registry, so the table cannot claim a
parameter FHIR does not have.

### One table, four consumers

The inventory is read by the SMART scope layer, the Provider Access
authorization filter, the Payer-to-Payer import classification, the
CapabilityStatement, and the controller's route constraint. Structural tests pin
each of them to it.

That is not tidiness. A clinical type reachable through SMART but missing from
the Provider Access governed set would be readable by any provider with a scope,
attributed or not, consented or not. Taking both from one table removes the
possibility rather than relying on a reviewer to notice.

The route constraint has to be a compile-time literal, so
`ClinicalResourceInventory.RouteAlternation` exists purely for a test to compare
it against.

---

## 2. Storage ownership

```
P2P validated package
      │
      ▼
PayerToPayerPackageIngestionService     ← the ONLY writer
      │  stage → commit (single-document ledger flip)
      ▼
p2p_imported_resources                  ← ONE store, TWO interfaces
      │
      ├── IPayerToPayerImportRepository  (write + exchange history)
      └── IClinicalResourceStore         (member-scoped clinical reads)
                │
                ▼
      ClinicalResourceService → ClinicalResourceController
                │
                ▼
      Patient / Provider Access FHIR
```

The Payer-to-Payer import store was **promoted** into the clinical serving
store, not copied into one. There is exactly one instance in DI, resolved
through a second interface, so:

* there is **no projection** to fall behind the source,
* there is **no dual write** to reconcile,
* there is **no second place** a resource could be stale in,
* and "which store did this come from?" still answers "the imported one".

Clinical data therefore stays out of CHO's authoritative member, enrollment,
coverage and claim stores **by construction** — a prior payer's Condition
physically cannot be read as a CHO-owned record, because it does not live where
CHO-owned records live.

**Durability.** MongoDB when `MongoDb:ConnectionString` is configured
(`MongoPayerToPayerImportRepository`); the in-process store otherwise — the same
Demo-mode fallback the rest of fhir-service uses. The in-process one does not
survive a restart and is not shared across instances.

---

## 3. Native vs imported

`StoredClinicalResource.Origin` is `Imported` or `ChoNative`, and it is part of
what a read returns and what `meta.source` says.

**No CHO component authors native clinical data today.** Every served clinical
resource is imported from a payer. The axis exists so that when a native writer
appears it *coexists* with imported data instead of overwriting it: a native
record would get its own identity derivation, so it can never collide with an
imported one, and two sources for the same clinical fact would be two resources
rather than one silently merged one. Until then this is an unexercised seam, and
it is described here as such.

(`coverage-service` has a `CareTeamProjector` that renders a US Core CareTeam
from PCP assignments. It is a different service with its own API and is not
wired into this store; making it a native clinical source is future work, not a
claim made here.)

---

## 4. FHIR identity

A resource is served under the **deterministic import identity** the ingestion
already computes:

```
SHA-256( tenant ␟ member ␟ source payer ␟ resource type ␟ source resource id )
```

(`PayerToPayerImportPolicy.ImportKey`; `␟` is the unit separator, which cannot
occur in an identifier, so two distinct tuples can never collide through
concatenation.)

The prior payer's own `Observation/123` is **not** served, because it is unique
only inside that payer. Deriving instead of allocating gives four properties at
once:

* **deterministic** — a replayed exchange resolves to the same id, so a
  re-import updates the resource a reader already fetched instead of creating a
  second one at a new URL;
* **collision-free** — tenant, member, payer and type are all inside the hash,
  so two payers' `OBS-1` are two CHO resources and neither is reachable from the
  other tenant;
* **opaque** — a hash leaks no member id, no payer name, no clinical detail, and
  is not a row number or an offset a caller could walk;
* **stable** — it survives a rebuild of the store and the migration in §12.

64 lowercase hex characters: inside FHIR's `[A-Za-z0-9\-\.]{1,64}` id rule.

**Knowing an id is not authority to read it** — see §8.

---

## 5. Member binding

Tenant and member come from the **trusted Payer-to-Payer exchange context** CHO
itself drove, never from the peer's Bundle. A package whose Observation names
another member is filed under the member CHO resolved, and *served* under that
member too: `ClinicalResourceProjector` rewrites the subject/patient element to
`Patient/{member}` from the stored binding on the way out.

So the served `subject` can never disagree with the record the reader is
authorized for, and an imported `subject.reference` is data — never
authorization authority. Asserted directly
(`PAT02_Replace_AHostilePackageCannotFileClinicalDataUnderAnotherMember`).

---

## 6. Provenance

Every served clinical resource carries:

| element | value |
| --- | --- |
| `meta.source` | `urn:cho:clinical:imported:{payer}:{source resource id}`, components percent-escaped so a payer id containing the separator cannot forge a different origin. Native data would be `urn:cho:clinical:native`. |
| `meta.lastUpdated` | when this version became CHO's record of the resource |
| `meta.versionId` | first 12 hex of the content hash — it changes exactly when the stored content changes, and not when an unrelated exchange re-commits an identical copy |

Imported data is therefore never indistinguishable from CHO-authored data at the
point a reader consumes it.

Richer provenance stays in repository metadata rather than on the wire: the
exchange id, the source endpoint key, the remote member id, the receipt instant
and the ingestion instant are all on the stored row and answerable from the
exchange record. No `Provenance` **resource** is emitted, because CHO serves no
read path for one and inventing an unresolvable reference would be worse than
saying nothing.

---

## 7. Read and search endpoints

```
GET /fhir/r4/{ClinicalType}/{id}
GET /fhir/r4/{ClinicalType}?patient=Patient/{member}
GET /fhir/r4/{ClinicalType}?subject=Patient/{member}     (where R4 defines it)
GET /fhir/r4/{ClinicalType}?_id={id}
```

Both `Patient/123` and a bare `123` are accepted, per FHIR reference search.
Paging is the shared `_count` / `_page` the rest of the surface uses, and
searches return a proper `searchset` `Bundle` with `total` across all pages and
RFC-5988 self/prev/next links — never a raw array.

`patient` and `subject` naming **different** members is a 400: two member
parameters that disagree is a malformed search, not a licence to pick one.

One controller serves all twelve types (`ClinicalResourceController`), its routes
constrained to exactly the inventory. Twelve near-identical controllers would be
twelve places for the member binding to drift, and the drift would be invisible
until a resource was served without a check.

### What is deliberately not implemented

`category`, `code`, `status`/`clinical-status`, and date searches are **not**
implemented and are **not** advertised. They are the difference between a
member-scoped read API and a search engine, and PAT-02 needs the former. When
they arrive they go in the inventory table, and the CapabilityStatement follows
automatically.

---

## 8. Authorization

### Patient Access

An authenticated **patient-context** token, whose `patient` claim binds the
request. `SmartScopeEnforcementMiddleware` requires
`patient|user|system/{Type}.read` for the type in the path, and enforces the
token binding against **both** member-naming parameters — `patient` *and*
`subject`. Checking only the first would have left the second as an unguarded
way to ask for somebody else's record, and that is exactly what the clinical
resources added.

### Provider Access

Unchanged from CONSENT-01: **authentication + SMART scope + provider/member
attribution + active ProviderAccess-purpose consent**, all four independent and
all four mandatory, composed fail-closed.

The clinical types participate automatically because they are in
`ProviderAccessAuthorizationFilter.GovernedResources` — taken from the same
inventory — and the filter is registered **globally**, so there is no
per-controller opt-in to forget. `subject` was added to the filter's
member-naming parameters, or a provider searching `?subject=Patient/x` would be
refused for want of a member context it did in fact supply.

A Payer-to-Payer consent does **not** open clinical data to a provider: the
consent that authorized the exchange that brought the data in is not the consent
that authorizes a provider to read it.

### The control only the clinical layer can apply

Every store call is keyed on the tenant **and** the member the caller is
authorized for. A resource belonging to anyone else is not filtered out of a
result — it is never selected. So:

* a **direct id read** establishes member ownership *in the query*, before any
  PHI is fetched; a guessed or leaked id resolves to nothing outside its own
  member;
* a **provider** direct read must name the member (`?patient=`), because
  Provider Access authorizes a member, not an id — a read naming no member
  cannot be judged for attribution or consent, and is refused;
* an id that could not have been issued by CHO is answered without a lookup at
  all.

### Anti-enumeration

"No such resource", "someone else's resource" and "another tenant's resource"
all return the **same 404**, with only the id the caller themselves supplied
differing. Telling them apart is precisely what enumeration needs. The
distinguishing category goes to the audit line instead. A refusal to read
another member is a **uniform 403** matching the Provider Access layer's.

---

## 9. Audit

One PHI-free line per clinical access:

> tenant · caller · patient-or-provider context · member (opaque id) · resource
> type · resource id · outcome · result count · instant

Never a value, a diagnosis, a medication name, a procedure description, free
text, a resource body, or a token. `ClinicalAccessContext` has no field one
could live in, asserted structurally. CR/LF is stripped from every
caller-influenced field (CWE-117).

---

## 10. Ingestion and payload validation

`PayerToPayerImportPolicy.Classify` now has four buckets:

| class | what it means |
| --- | --- |
| `MemberHistory` | EOB, Claim, ClaimResponse, Encounter, DocumentReference — stored and served by their own controllers |
| `AdministrativeReference` | Patient, Coverage, Organization, Practitioner, PractitionerRole, Provenance — reference resolution and traceability only, never authoritative |
| `ClinicalRecord` | the inventory in §1 — stored and served as clinical data |
| `Unsupported` | a type CHO's FHIR surface still does not serve — **named, counted, and preserved in the archived package**, never dropped |
| `Rejected` | a clinical resource the payload validator refused — counted and named by reason |

Clinical data becomes readable PHI on CHO's own surface, so it passes a gate the
reference-only administrative context does not need
(`ClinicalPayloadValidator`):

* the resource type is one CHO serves clinically;
* it carries a source id, of legal FHIR length (≤ 64);
* the serialized resource is within `Clinical:PayloadLimits:MaxResourceBytes`
  (default 1 MiB — generous for a Condition, small enough that a peer cannot use
  the clinical store as a blob dump; documents belong behind `DocumentReference`);
* it nests no deeper than `MaxDepth` (default 40), checked with a **streaming**
  reader so a payload designed to blow up an object graph is rejected without
  being materialized as one.

It does **not** re-parse anything. The package has already been read with the
Firely parser this service owns; a second parser would be a second opinion about
what FHIR is.

A refusal is **per resource**: one oversized Observation does not cost the
member the rest of their history, and the package stays archived verbatim either
way. The exchange record carries `ClinicalResourceCount`,
`RejectedResourceCount` and `RejectedResourceReasons`
(`"{ResourceType}:{reason}"`, categories only).

---

## 11. Deduplication, versioning and freshness

Reads return, for each identity, the version from the most recently
**COMMITTED** exchange.

| situation | behaviour |
| --- | --- |
| exact replay of a package | same identity, same content hash → one resource, two deliveries; no duplicate |
| changed content from the same payer | same identity, new content hash → supersedes at the same URL once its exchange commits |
| same source id from a **different payer** | different identity → two resources, never merged |
| same source id in a **different tenant** | different identity → invisible here |
| package staged but not committed | invisible, and never displaces a committed version |
| exchange failed | invisible |

Freshness follows from that last pair: Patient and Provider Access read current
committed clinical state, and a partial or failed ingestion cannot be read as
the member's record. The commit is a single-document ledger flip, so it is
atomic without needing a multi-document transaction.

Version history is not exposed: there is no `_history` read path, and
`meta.versionId` is a content discriminator rather than a sequence. Superseded
versions remain on their own exchange's rows, so "which exchange delivered
what" stays answerable from durable state.

---

## 12. Reference normalization

Ingestion rewrites intra-package references to the local identity
`PayerToPayerImport/{id}` so they stop pointing at the other payer's server
(unchanged from P2P-02, and narrow on purpose: only references that resolve to
another resource in the *same* package are rewritten).

Serving completes the round trip. `PayerToPayerImport/{id}` becomes a real
`{Type}/{id}` FHIR reference **only** when the target is a clinical type CHO
genuinely serves *and* belongs to the same member — resolved for a whole page in
one store round trip, scoped to the tenant and member so resolving a reference
can never confirm that somebody else holds the target.

Everything else is left exactly as stored:

| form | behaviour |
| --- | --- |
| local identity → served clinical type | rewritten to `{Type}/{id}` — resolvable |
| local identity → a type CHO does not serve | left as the opaque local identity; dressing it up would produce a reference that looks resolvable and 404s |
| local identity CHO cannot resolve | left alone |
| a reference the payer left pointing outside the package | untouched — CHO does not invent links |
| contained (`#…`) | never rewritten; it is local to its own resource |
| `urn:uuid:…` | not a resource reference; untouched |
| versioned (`Type/id/_history/2`) | resolved to the same resource at ingestion |

The subject/patient element is the one exception, and it is not reference
resolution: it is replaced with the trusted member binding (§5).

---

## 13. Migration

Clinical data CHO **already holds** from exchanges committed before this feature
lives only in the ledger's archived package: those types were `Unsupported`, so
no rows were staged.

`ClinicalBackfillService` re-reads each committed exchange's archive — the
payer's own bytes — and stages its clinical resources under exactly the
identities the original ingestion would have used. No operator has to re-run a
prior payer exchange, which would mean asking another payer for data CHO already
has, and would be impossible once that payer relationship has ended.

| property | how |
| --- | --- |
| deterministic | same archive in, same rows out — the import key is a pure function of the exchange's own binding |
| replay-safe | rows are upserts on (tenant, exchange, import key); running it twice, or after a partial run, converges |
| non-destructive | it only **adds** clinical rows; member history, administrative context, the archive and every exchange timestamp are untouched |
| tenant-safe | tenant, member and payer come from the ledger entry, never from the archived Bundle |
| committed-only | an exchange that failed or never committed is skipped — the backfill cannot publish what the original ingestion refused |
| gated | the same payload validator runs, so an oversized resource is refused here exactly as on arrival |
| fault-tolerant | one unreadable archive is skipped and logged by category; the rest of the sweep continues |

**Configuration** (`Clinical:Backfill`): `Enabled` (default **false**),
`DryRun`, `BatchSize` (default 50). It runs once at startup when enabled — a
one-shot convergence, not a recurring sweep, because a second run over an
already-backfilled store changes nothing. A failure is logged and swallowed: the
backfill is not on any request path, and taking the FHIR service down because a
historical archive could not be re-read would be the worse outcome.

The exchange's counts are brought up to date and the ledger records
`ClinicalBackfilledAtUtc`. Leaving `unsupported: Condition, Observation` on an
exchange whose Conditions and Observations CHO is now serving would make the
record untrue; the marker keeps "these arrived later, from the archive"
answerable.

**Limitation:** a run interrupted part-way leaves the exchanges it reached
backfilled and the rest not. Nothing is corrupted and nothing is lost — the next
run finishes the job — but the sweep is not one transaction.

---

## 14. Indexes and performance

MongoDB (`p2p_imported_resources`):

| index | serves |
| --- | --- |
| `(tenantId, exchangeId, importKey)` unique | staging identity; the store itself refuses a second copy |
| `(tenantId, memberId, importKey)` | the member's imported history; resolving a normalized reference |
| `(tenantId, memberId, resourceType, importKey)` | **the clinical read path** — both the direct read and the member-scoped search are served by this one prefix |

`p2p_import_ledger` adds `(tenantId, status)` for the commit lookups a clinical
read performs.

Every constraint that bounds a clinical result — tenant, member, resource type,
classification, and the optional `_id` — is in the Mongo filter. There is no
"find all Observation, then filter". Choosing the winning *version* among an
identity's per-exchange rows is the only step done in memory, over an
already-member-and-type-scoped set, because it needs each exchange's commit
instant from the ledger; and the ledger query names exactly the exchanges in
that set rather than every exchange the tenant ever ran.

Paging is skip/take over the deduplicated result. That is honest about its
cost: it is right for a member's clinical record and would not be right for a
tenant-wide scan, which this API cannot express.

---

## 15. Profiles and conformance

**No US Core profile is claimed.** No `meta.profile` on a served resource, and
no `supportedProfile` in the CapabilityStatement.

CHO serves these as **valid FHIR R4** and does not re-shape a prior payer's
clinical content to satisfy US Core invariants. Two things would have to be true
before a profile URL meant anything here:

1. the resource shape would have to be validated against the profile — a label
   without validation is a claim, not conformance; and
2. the search-parameter support would have to meet US Core's SHALL set, which
   §7 says plainly it does not.

Both are future work, and stating that is the point. A CapabilityStatement that
claims a profile the server does not satisfy is worse than one that claims
nothing.

---

## 16. CapabilityStatement

Generated from the inventory. For each clinical type: `type`, `read`,
`search-type`, and exactly the search parameters the controller honours. No
`supportedProfile`. No create/update/delete — nothing is written through this
surface.

Tests assert, over the whole statement: every advertised clinical resource is
routable and has a real read path; every inventory type is advertised, once;
advertised search parameters equal implemented ones; and no resource is
advertised that no controller route serves.

---

## 17. Limitations

1. **No US Core profile conformance** is claimed or validated (§15).
2. **Search is narrow** — read plus `_id`, `patient`, and `subject` where R4
   defines it. No `category`, `code`, `status` or date search (§7).
3. **No CHO-native clinical writer** exists; every served resource is imported,
   and the coexistence semantics in §3 are an unexercised seam.
4. **No version history read path** and no `_history` interaction (§11).
5. **No `Provenance` resource** is emitted; provenance is `meta.source` plus
   repository metadata (§6).
6. **In-process storage is the Demo-mode fallback** — durability requires
   MongoDB configured, the same posture as the rest of fhir-service.
7. **The backfill sweep is not transactional** (§13).
8. Clinical data has **no retention policy** of its own; it lives as long as the
   imported record does. (Contrast PAT-03, where prior-authorization retention
   is an explicit lifecycle.)

---

## Traceability

| concern | file |
| --- | --- |
| inventory | `src/services/fhir-service/Services/Clinical/ClinicalResourceInventory.cs` |
| identity | `.../Clinical/ClinicalResourceIdentity.cs` |
| store contract | `.../Clinical/ClinicalResourceStore.cs` |
| payload gate | `.../Clinical/ClinicalPayloadValidator.cs` |
| projection | `.../Clinical/ClinicalResourceProjector.cs` |
| read + audit | `.../Clinical/ClinicalResourceService.cs` |
| migration | `.../Clinical/ClinicalBackfill.cs` |
| routes | `src/services/fhir-service/Controllers/ClinicalResourceController.cs` |
| classification | `.../PayerToPayer/Ingestion/PayerToPayerImportPolicy.cs` |
| durable store | `.../PayerToPayer/Ingestion/MongoPayerToPayerImportRepository.cs` |
| SMART | `src/services/fhir-service/Middleware/SmartScopeEnforcementMiddleware.cs` |
| Provider Access | `.../ProviderAccess/ProviderAccessAuthorizationFilter.cs` |
| conformance | `src/services/fhir-service/Controllers/MetadataController.cs` |
| acceptance | `tests/Cms0057Acceptance.Tests/Scenarios/PatientAccessClinicalTests.cs` |
| HTTP behaviour | `tests/CloudHealthOffice.FhirService.Tests/Controllers/ClinicalResourceControllerTests.cs` |

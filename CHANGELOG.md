# Changelog

All notable changes to Cloud Health Office will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Da Vinci PAS `Claim/$inquire` — prior-authorization status through the FHIR surface

Prior-authorization state was persisted and retrievable, but only through
authorization-service's own REST surface. The standards-facing FHIR operation did
not exist, which is what kept PAS-04 PARTIAL. `POST fhir/r4/Claim/$inquire` now
serves it.

**One record, projected — not a second model.** The inquiry reads the same
authorization record `$submit` writes and the rest of the platform updates, over
the read endpoint authorization-service already exposes. There is no
inquiry-specific store and no second status field. `PriorAuthorizationRecord` is
a deliberately narrow read projection: the fields an inquiry does not need —
patient name, date of birth, clinical attachments, reviewer, notes — have no
property to land in, so they cannot leak into a response or a log.

**Standards shape, not a CHO wire format.** The request is a PAS Bundle carrying
a `Claim` with `use = preauthorization`, whose identifier (or
`insurance.preAuthRef`) names the authorization. The response is a Bundle
carrying a `ClaimResponse` on the Da Vinci PAS profile, built by the same
`PasResponseBuilder` that serves `$submit`.

**The status mapping is deterministic and total** over CHO's authorization
states: Submitted/InReview → queued `pending`; Pended/A4 → queued
`pended-additional-information` with the X12 306 reviewAction; Approved/A1 →
complete `approved`; Modified/A2 → partial `modified`; Denied/A3 → complete
`denied` with the coded reason; Expired → complete `expired`; Cancelled →
cancelled. `outcome` carries the coarse answer and `disposition` the specific
one, so a caller can distinguish pending from pended-for-information from
approved from denied. An unrecognised status reads as still in progress rather
than as an approval CHO cannot vouch for.

**Read-only by contract.** `IPriorAuthorizationStore` exposes one lookup method
and no write method at all — asserted structurally, so an inquiry cannot create a
record, move a status, restart a decision clock, or trigger a payer submission
however often it is repeated. Status comes from the stored adjudication record,
so an inquiry never becomes an outbound X12 transaction.

**Freshness.** Every inquiry reads live committed state, so a status changed
after submission is the status returned; tests drive pended → approved and assert
the second inquiry reflects it.

**Lookup cannot be guessed into.** An authorization number alone is never
sufficient — a corroborating member or provider key must accompany it and match
the record, and a supplied key that does *not* match refuses even when another
does. Tenant comes from the authenticated context and is re-checked on the record
itself, so it holds even if header propagation is lost.

**Anti-enumeration, without swallowing honest errors.** Request-shape defects —
no authorization identifier, no corroborating key — return `400` naming what is
missing, because they say nothing about what exists; telling a caller who forgot
an identifier that their authorization "does not exist" would be both wrong and
unhelpful. Every refusal about a *record* — unknown, wrong tenant, not the
caller's — returns one identical `404` `OperationOutcome`, and a structural test
pins that classification so a newly added outcome cannot quietly become
distinguishable. The category is kept in a PHI-free audit line carrying tenant,
caller, authorization number, outcome and status only.

**Thin controller.** The action routes and maps to HTTP; lifting the lookup keys
out of the inquiry Claim belongs to the service, since which element carries
which key is a property of the PAS request shape rather than of HTTP.

**Authorization controls.** Authentication (`[Authorize]`), the SMART
`*/Claim.read` scope, and tenant from context — the same controls `$submit` has.
Deliberately **not** routed through the Provider Access consent gate: that gate
governs a provider reading a member's clinical record, whereas PAS is a
system-to-system transaction between the submitter and the payer about the
submitter's own request. The corroborating key, not a member consent, is what
binds an inquiry to its authorization.

**Making status inquirable required fixing the write side.** `preAuthRef` was set
only on approvals, and a pended submission persisted an authorization number that
was never returned to the caller — so the outcome that most needs following up
was un-inquirable. Approved, denied and pended responses now all carry the
number that was persisted. The denial code and reason, the approved period, and
the service lines from the submitted Claim are now persisted too, so an inquiry
can answer *why* and *for what* rather than just "denied". The authorization HTTP
client now propagates the tenant header — without it authorization-service falls
back to its default partition, and reads and writes would have crossed tenants.

**CapabilityStatement.** `Claim` now advertises both `submit` and `inquire` with
their Da Vinci PAS `OperationDefinition` canonicals, pinned by test to the routes
actually served. `$submit` had never been advertised.

Acceptance: **PAS-04 moves PARTIAL → PASSABLE**, so CHO Replace declares
17 PASSABLE / 4 PARTIAL / 0 GAP — computed by the evidence generator, not
hard-coded. **PAS-07 stays PARTIAL**: `$inquire` *reports* a pended-for-
information decision, which CHO already knows from the A4 review decision, but it
neither requests documentation nor accepts it — that round-trip is what CDex is,
and it is not implemented. Remaining PARTIAL: PAS-07, PAT-02, PAT-03, SEC-01.
PAS-01/02/03/05/06/08 are unchanged and green. Zero GAPs is still not complete
CMS-0057-F compliance.

New documentation: `docs/architecture/prior-authorization.md`. Updated:
`docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`, `docs/diligence/ADAPTER-STATUS.md`.

### Enforce Provider Access through the shared consent registry

Payer-to-Payer already ran on a purpose-scoped consent decision; Provider Access
did not. A provider-shaped token (`user/…` or `system/…`) that passed the SMART
scope check could read **any** member's record — the acceptance suite even
asserted that as expected behaviour. Provider Access now composes four
independent, mandatory controls, and the consent one runs on the same registry
and the same policy as Payer-to-Payer.

**Four controls, none implying another.** Authentication and SMART scope stay
where they are (middleware — token validation is not moved or re-implemented).
On top of them `ProviderAccessAuthorizationService` adds provider/member
attribution and an active `ConsentPurposeOfUse.ProviderAccess` consent. A correct
scope implies neither attribution nor consent; attribution does not imply
consent; consent does not imply attribution; and a Payer-to-Payer consent
authorizes nothing here. The composed decision fails closed — any one refusal
denies, and so do a missing tenant, a missing member context, an unidentified
caller, and an unreadable registry.

**One registry, one policy, now shared by construction.** A new
`IConsentEvaluator` owns the fail-closed registry read and delegates every
lifecycle and purpose rule to the existing pure `ConsentAuthorizationPolicy`.
Payer-to-Payer's gate was refactored onto it rather than keeping its own copy, so
the two capabilities cannot drift: they differ only in the purpose they ask for.
`IConsentSource` generalises the registry seam that #1152 introduced — the same
`HttpConsentRegistryConsentSource` against `consent-service` serves both, and no
second consent store exists.

**Enforced at one shared boundary, before any PHI is read.**
`ProviderAccessAuthorizationFilter` is a **global** MVC action filter, not a
per-controller attribute, so a new member-scoped controller is governed the
moment it exists. A filter rather than middleware because Provider Access needs
the tenant and `TenantMiddleware` runs *after* `SmartScopeEnforcementMiddleware`;
a filter runs after the whole pipeline yet still before any action body. It
governs every member-scoped resource the SMART layer serves — `Patient`,
`Coverage`, `ExplanationOfBenefit`, `Encounter`, `Claim`, `Task`,
`Communication`, `DocumentReference`, `ClaimResponse` — and a structural test
pins that inventory to the SMART layer's own list so a resource cannot escape one
by being forgotten in the other. FHIR operations are deliberately excluded:
`$member-match` and `$member-data-export` have their own Payer-to-Payer
authorization, and an operation name is not a member id.

**Provider Access is a caller shape, not a route name.** A `user/`- or
`system/`-scoped token is a provider reading someone else's record. A
patient-scoped token is Patient Access — the member reading their own data —
which a Provider Access consent does not govern and is not required for. The
distinction comes from the token, so it cannot be lost by adding an endpoint.

**Member context is required, not guessed.** `Patient/{id}` names the member;
otherwise it comes from `?patient=` or the SMART binding. A resource id is never
resolved to a member, because resolving it means reading the resource being
authorized. No member context denies — which is why a provider-shaped search
across the whole membership is now refused rather than returning every member.

**Refusals cannot be used to enumerate.** "Not attributed", "no consent" and "no
such member" return one identical `403` FHIR `OperationOutcome`; a test asserts
the bodies are byte-identical. The structured category lives in the audit record
instead. Decisions are audited with PHI-free identifiers only — tenant, member
id, caller id, resource type, consent id, category, instant — never
demographics, clinical payloads, consent narrative, or credentials, with CR/LF
stripped from ids (CWE-117).

**Attribution, stated honestly.** The repository had **no** attribution code at
all: PROV-02's "attribution enforcement" test asserted a dictionary miss on an
unknown id, and the capability text describing Provider Access as "governed by
attribution plus SMART scopes" was aspirational. Attribution is now a real,
enforced control backed by a configured panel catalog
(`Cms0057:ProviderAttribution`) that fails closed on an empty catalog — but no
live roster feed from a payer source system is wired up, and nothing claims one
is. That remains engagement integration behind `IProviderAttributionSource`.

Two existing tests asserted the hole and were rewritten rather than deleted:
`EobSearch_UserToken_CanSearchAnyPatient` (a provider token reading any patient)
and `PatientSearch_SystemToken_NoPatientBinding` (a backend token listing the
whole membership) now assert the refusals, alongside new tests for the authorized
paths.

Acceptance: **CONSENT-01 moves PARTIAL → PASSABLE**, so CHO Replace declares
16 PASSABLE / 5 PARTIAL / 0 GAP — computed by the evidence generator from the
manifest, not hard-coded. Payer-to-Payer behaviour is unchanged and its suite
re-runs green: a Provider Access consent still authorizes no exchange. Remaining
PARTIAL: PAS-04, PAS-07, PAT-02, PAT-03, SEC-01. Zero GAPs is still not complete
CMS-0057-F compliance; this is implementation evidence, not certification, and
the QNXT/external-core column is unchanged.

New documentation: `docs/architecture/provider-access.md`. Updated:
`docs/architecture/consent.md`, `docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`,
`docs/diligence/ADAPTER-STATUS.md`.

### Dedicated Payer-to-Payer consent lifecycle and enforcement

Payer-to-Payer authorization was a generic Active consent: any active record for
the member let an exchange proceed. It is now a first-class, purpose-scoped
decision on the same registry.

**A purpose axis, not a new consent type.** `ConsentPurposeOfUse`
(`Unspecified` / `PayerToPayerExchange` / `ProviderAccess`) says what a consent
authorizes the plan to *do*, orthogonal to `ConsentType`, which says what
regulatory instrument the record *is*. A §164.508 authorization for
Payer-to-Payer and one for Provider Access are the same instrument and different
permissions, and a sensitive-category authorization can be purpose-scoped too.
The axis follows FHIR `Consent.provision.purpose` (HL7 v3 PurposeOfUse), so the
record projects onto FHIR Consent when that projection lands.

**One registry, one policy.** `consent-service` remains the authoritative store —
no second consent collection was added. The new
`CloudHealthOffice.Consent.Contracts` project carries a PHI-free
`ConsentAuthorizationSnapshot` (tenant, member, id, purpose, status, period,
version — no narrative fields, and no field to put them in) and
`ConsentAuthorizationPolicy.Evaluate`, a pure function that is the single place
any purpose-scoped authorization is decided. A snapshot must match the tenant
**and** the member, carry the requested purpose, be `Active`, and be in force at
the evaluation instant — the effective period is applied by the policy rather
than trusted from the stored status, so a record persisted as `Active` past its
`ExpiresAt` still denies. Ties resolve deterministically (latest-expiring,
unbounded first, then highest version), so two evaluations of the same registry
state name the same consent.

**Refusals say which refusal.** `NoConsentOnRecord`, `NoConsentForPurpose`,
`NotActivated`, `Revoked`, `Expired`, `NotYetEffective` — reported
most-specific-first, because "they revoked it", "it lapsed", and "they never
granted this purpose" are different operational facts.

**Enforced server-side, identically in both directions.** Inbound
(`PayerToPayerExchangeService`) evaluates before any member data is assembled.
Outbound (`PayerToPayerOutboundService`) evaluates twice: before the remote
`$member-match`, so an unauthorized member's identity never leaves CHO at all,
and **again immediately before the export**, so a revocation landing while the
match is in flight stops the data request. Both call one
`IPayerToPayerConsentGate` over one policy against one registry, so responding
cannot drift more permissive than initiating. No Payer-to-Payer request type has
a consent field in either direction — an acceptance test asserts that by
reflection over all four request types, so neither a peer payer nor an internal
caller can self-attest.

**Provider Access separation is structural.** A member with an Active
`ProviderAccess` consent and nothing else is denied for Payer-to-Payer with
`NoConsentForPurpose`. The purposes are compared as data inside the policy;
nothing about the calling controller or route participates.

**Exchanges record what authorized them.** `AuthorizingConsentId`,
`ConsentDecisionReason`, and `ConsentEvaluatedAtUtc` are written on the outbound
exchange, and the consent id and reason on both audit entries. A retry clears
them so it re-asks rather than reusing an earlier answer.

**Fail-closed at every edge.** Blank tenant or member, an unreadable or
unreachable registry, a source that throws, an empty catalog, and `Unspecified`
purpose all deny. Consent-lookup failures log a category only, never registry
detail.

New consent-service surface: `PurposeOfUse` on `Consent` (optional on create) and
`GET api/v1/members/{memberId}/consents/authorization-snapshots?purposeOfUse=`,
a PHI-free projection so another service can authorize without reading consent
narrative. `fhir-service` reads it through `HttpConsentRegistryConsentSource`
when `Services:ConsentServiceUrl` is configured; the configuration-backed source
remains the Demo/test fallback in the *same* shape (purpose, status, period), so
Demo exercises the real policy instead of a boolean allow-list.

**Migration is explicit and fails closed.** Records written before `PurposeOfUse`
existed deserialize to `Unspecified` and authorize nothing purpose-specific.
There is no backfill, no inference from `ConsentType`, and no "treat Active as
P2P" fallback. The practical consequence is deliberate: **a deployment upgrading
to this code authorizes zero Payer-to-Payer exchanges until members' consents are
recorded with `PurposeOfUse = PayerToPayerExchange`.** Unchanged: `ConsentType`,
the state machine and its transitions, the encrypted narrative fields, and every
existing endpoint's contract (the consent event payload gains a `purposeOfUse`
field).

Acceptance: **P2P-03 moves PARTIAL → PASSABLE**, so CHO Replace declares
15 PASSABLE / 6 PARTIAL / 0 GAP — computed by the evidence generator from the
manifest, not hard-coded. **CONSENT-01 stays PARTIAL** with a new GAP test naming
the reason: the registry can now express a Provider Access purpose, but the
Provider Access *read path* does not consult it (attribution plus SMART scopes
govern that path), so CONSENT-01 does not ride on the Payer-to-Payer work. The
QNXT/external-core column is unchanged — P2P-03 augment stays **GAP**. Zero GAPs
is still not complete CMS-0057-F compliance; this is implementation evidence, not
certification or attestation.

New documentation: `docs/architecture/consent.md`. Updated:
`docs/architecture/payer-to-payer.md` (Consent section, order of operations),
`docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`.

### Durably ingest inbound Payer-to-Payer data into the member record

The outbound Payer-to-Payer exchange previously stopped at a validated Bundle:
CHO retrieved another payer's member-scoped package, checked it, stamped
provenance, audited it — and kept nothing. A successful exchange now produces a
durable, tenant-safe, member-scoped, provenance-preserving CHO record.

New fhir-service code: `PayerToPayerPackageIngestionService` (application
service) receives an ALREADY VALIDATED package from `PayerToPayerOutboundService`
and never contacts a payer itself — the orchestration and transport added for
P2P-02 are unchanged, and no second Payer-to-Payer wire format exists. It
classifies each resource, normalizes intra-package references, stages every row
under a deterministic import key, and commits.

**Imported data is kept apart from CHO-authoritative data.**
`IPayerToPayerImportRepository` (MongoDB when `MongoDb:ConnectionString` is set,
in-process otherwise — the same fallback `DtrService` uses) is a separate store
from CHO's member, enrollment, claim, and provider records. Source ownership is
structural rather than conventional: an imported row cannot be read as a CHO-owned
record, so a remote `Patient` never replaces CHO's member identity and a prior
payer's `Coverage` never touches current enrollment. Both are stored as
reference-only administrative context, keeping the peer's resource id while filed
under CHO's own member.

**Supported types are the ones CHO actually serves**, not the CMS wish list:
`ExplanationOfBenefit`, `Claim`, `ClaimResponse`, `Encounter`, and
`DocumentReference` are ingested as member history; `Patient`, `Coverage`,
`Organization`, `Practitioner`, `PractitionerRole`, and `Provenance` are stored as
reference-only. Everything else — `Condition`, `Observation`, and the rest of the
USCDI clinical set — is **named and counted on the exchange and preserved in an
archived copy of the package**, never silently dropped and never claimed as
ingested.

**Replay-safe by construction.** The import key is a hash of
tenant + local member + source payer + resource type + source resource id, joined
with a separator that cannot occur in an identifier. Replaying a package lands on
the same rows instead of doubling a member's history; the same source id from a
*different* payer is a different key, so two payers' records are never merged; a
content hash tells "same again" from "changed"; and each exchange's own
`Provenance` stamp stays its own record so it remains clear which exchange
delivered what.

**Atomic enough to be safe.** Rows are versioned by exchange — identified by
(tenant, exchange, import key) — and reads return the version from the most
recently committed exchange. Staging writes only that exchange's own rows and
committing is a single-document ledger write, so a failed ingestion both adds
nothing visible AND takes nothing away: it cannot overwrite or hide the version an
earlier exchange committed, and an updated resource supersedes the older one only
once the exchange carrying it commits. A retry re-stages the same deterministic
keys and commits; an exchange abandoned in a non-terminal state is taken over
once stale, so a process that dies mid-ingestion cannot strand a coverage
transition. The
exchange gained `DataReceived` and `Ingesting` states plus structured ingestion
fields (status, failure category, persisted / duplicate / administrative /
unsupported counts with the unsupported types named, and start/finish timestamps);
`Completed` is now reachable **only** after the commit lands, so a package that
was retrieved but not stored is never reported as success.

**References** are rewritten only when they resolve to another resource in the
same package — relative, absolute, and versioned forms all resolve to CHO's
imported identity, while references to resources the peer did not send, contained
(`#…`) references, and `urn:uuid` forms are left exactly as they arrived. CHO does
not invent links the source payer never asserted, and an absolute URL does not
survive as a live pointer at the peer.

Tenant, member, and source payer on every stored row come from the validated
exchange context, never from the peer's Bundle: a package whose resources name
another tenant or member changes nothing about where its data is filed. Logs and
audit carry ids, categories, and counts only — no Bundle bodies, demographics,
clinical payloads, or endpoint URLs. Real tests
(`PayerToPayerIngestionTests`, `[Trait("Backend","Replace")]`, 15 scenarios, plus
`PayerToPayerImportPolicyTests` / `PayerToPayerReferenceNormalizerTests`, 36
cases, plus `PayerToPayerOutboundControllerTests`) drive the production path:
durable persistence with correct binding, provenance retention, administrative
ownership, unsupported-type handling, replay and cross-payer non-merging, staging
and commit failure, a failed later exchange not hiding committed history, retry
and stale-exchange takeover, tenant and member safety, reference resolution, and
per-failure HTTP mapping.

**No acceptance scenario status changed.** P2P-02 was already PASSABLE and its
rationale is updated; **P2P-03 stays PARTIAL** (no dedicated Payer-to-Payer
`ConsentType`), **PAT-02 stays PARTIAL** (USCDI clinical types are archived, not
served), and **PAT-03 stays PARTIAL** (no retention job). CHO Replace remains
14 PASSABLE / 7 PARTIAL / 0 GAP, which is not full CMS-0057-F compliance,
completeness, or certification. Imported data is durable but **not yet projected
into CHO's FHIR read APIs**; payer onboarding (SMART Backend Services / UDAP,
mTLS) remains deployment integration; and QNXT/external-core Payer-to-Payer
integration remains **GAP**. New architecture documentation:
`docs/architecture/payer-to-payer.md`.


### Implement CHO-native outbound Payer-to-Payer exchange (P2P-02)

Closed CMS-0057-F acceptance gap **P2P-02** (Payer-to-Payer outbound initiation)
as real Cloud Health Office Replace-mode capability: on an authorized coverage
transition, CHO — the member's new payer — initiates the exchange against the
member's prior payer, rather than only answering other payers' requests. It
orchestrates the existing P2P primitives instead of duplicating them (P2P-01
respond semantics for the data request, P2P-04 coverage selection for the local
prior-coverage context) and adds no second wire format.

New fhir-service code: `PayerToPayerOutboundService` (application service) drives
the workflow fail-closed and in order — tenant scope, member + prior-payer
coverage context from CHO-owned data, target-payer endpoint resolution,
server-side opt-in, remote `Patient/$member-match`, member-data export, response
validation, provenance, audit, exchange state. A thin
`PayerToPayerOutboundController` (`POST fhir/r4/PayerToPayer/$initiate`, under
the SMART-enforced surface, tenant from the authenticated context) only routes;
no outbound logic lives in it.

**Endpoint resolution is the SSRF boundary.** `IPayerToPayerEndpointResolver`
resolves a *payer id* — never a caller-supplied URL — against a tenant-scoped
configuration directory (`Cms0057:PayerToPayerOutbound`), and fails closed: an
unknown payer, a duplicate entry, a non-absolute or non-HTTPS base URL (plain
HTTP only under an explicit development flag, with a warning), or a URL carrying
user info, a query, or a fragment resolves to nothing. The outbound request and
its DTO carry no URL/endpoint field at all. `HttpPayerToPayerRemoteClient` uses a
named `HttpClient` with redirects disabled (a peer cannot bounce CHO onto another
host), a response-size cap, unchanged TLS validation, and no logging of payloads,
demographics, credentials, or endpoint URLs — log lines identify a peer by its
opaque directory key.

**Authorization is server-side and enforced before anything leaves CHO.** The
existing `IPayerToPayerConsentGate` decides the member's opt-in; there is no
caller-supplied consent field, and an unauthorized member's identity is never
disclosed to a remote payer (not even in a member-match). The remote match sends
only what the operation needs — the member's identifier with that payer (from
CHO's own coverage record) plus family name and birth date; no SSN, address,
phone, or email. Export is requested **only** after the peer resolves exactly one
member, and the returned FHIR Bundle is parsed and checked for member consistency
(single matched Patient, no foreign `Patient/…` reference) before acceptance;
anything unparseable, empty, or inconsistent is rejected whole. Accepted packages
are stamped with a `Provenance` naming the source payer, so another payer's data
is never mistaken for CHO-originated.

Outcomes are structured, not free text (`TargetPayerNotConfigured`,
`NotAuthorized`, `LocalCoverageAmbiguous`, `MemberNoMatch`, `MemberAmbiguous`,
`RemoteUnauthorized`, `RemoteUnavailable`, `InvalidRemoteResponse`) and are
recorded on a `PayerToPayerOutboundExchange` with an idempotency key
(tenant | member | target payer | transition), so a repeated initiation replays
one exchange and a retry after a failure resumes it. Audit carries tenant,
member, target payer, endpoint key, exchange id, outcome, and resource count —
no demographics, payload, URL, or credential. Real acceptance tests
(`PayerToPayerOutboundTests`, `[Trait("Backend","Replace")]`, 24 scenarios) drive
the production orchestration with only the far side of the wire faked, asserting
call ordering and request content, missing consent, unconfigured/non-HTTPS payer,
no-match, ambiguous match, remote auth/transport failure, malformed and
cross-member packages, cross-tenant refusal, overlapping local coverage, and
idempotent retry. `HttpPayerToPayerRemoteClientTests` pins the transport seam's
own contract: peer status → structured outcome (422 is the anti-enumeration
no-match signal; a 404 is a route/configuration error, not a member no-match),
calls only to the resolved endpoint URIs, no fabricated Authorization header, and
an oversized or empty body refused rather than buffered.

**Scope, stated plainly.** P2P-02 CHO Replace moves GAP → **PASSABLE** and CHO
Replace now declares 14 PASSABLE / 7 PARTIAL / 0 GAP — which is not full
CMS-0057-F compliance, completeness, or certification. **P2P-03 remains
PARTIAL**: opt-in is still a generic Active consent with no dedicated
Payer-to-Payer `ConsentType`. Received packages are retrieved, validated, and
audited but **not ingested** into the CHO member record; exchange state lives in
an in-process store; and connecting to any named payer needs that payer's
onboarding — a directory entry plus transport credentials (SMART Backend
Services / UDAP client registration, mTLS) behind
`IPayerToPayerCredentialProvider`, which supplies none by default rather than
fabricating one. QNXT/external-core P2P integration (including outbound
initiation from a QNXT-backed deployment) remains **GAP**.


### Implement Cloud Health Office-native Payer-to-Payer member match (P2P-04)

Closed CMS-0057-F acceptance gap **P2P-04** (Payer-to-Payer member-match /
concurrent coverage) with the FHIR `Patient/$member-match` operation as real
Cloud Health Office Replace-mode capability — cross-payer identity resolution
over CHO-owned data, distinct from the P2P-01 known-member respond. New
fhir-service code: `PayerToPayerMemberMatchService` (application service) resolves
the transitioning member within the tenant from normalized identity attributes
and returns the relevant **member + coverage context**; a thin
`PayerToPayerMemberMatchController` (`POST fhir/r4/Patient/$member-match`, under
the SMART-enforced surface, tenant from the authenticated context) only routes.
Matching is **deterministic and fail-safe** (`MemberMatchPolicy`): a positive
assertion needs a strong identifier (member/subscriber id or SSN) or the
family-name + birth-date pair, and any contradicting attribute — wrong DOB, wrong
member id, different sex — fails the candidate closed. Zero candidates → no match,
more than one → ambiguous, cross-tenant → never visible, and a weak single
attribute is refused before any search (anti-enumeration). `MemberIdentityNormalizer`
makes equivalent formatting (casing, whitespace, accents, phone/ZIP punctuation,
identifier hyphens) compare equal without merging distinct people.
`PayerToPayerCoverageSelector` picks the relevant **concurrent/prior/current**
coverage by requested payer/subscriber context and effective date; genuinely
overlapping coverages without a discriminator return an ambiguity rather than a
guess. The match reuses the same CHO member/coverage store via a new
`IChoMemberDirectory` on `MockPatientAccessDataProvider` (no duplicate store; the
Patient Access contract is unchanged), and its resolved member id feeds the P2P-01
export path directly (proven by an acceptance test). Member-match is identity only
and does **not** gate on or introduce consent. Real acceptance tests
(`MemberMatchTests`, `[Trait("Backend","Replace")]`) exercise the production
service/policy/selector/normalizer/source: exact strong-id and demographic
matches, prior-payer subscriber id, no-match, ambiguous identity narrowed by given
name/gender, conflicting id/DOB/given name, cross-tenant, insufficient criteria,
normalization, concurrent-coverage selection (prior/current/overlapping), and the
P2P-01 hand-off. The prior P2P-04 GAP-assertion test is replaced by this behavioral
coverage; the adapter-status report's PayerToPayer source now names `$member-match`
(mode stays Demo). The scenario manifest moves P2P-04 `replace` `GAP → PASSABLE`.
**P2P-02** (outbound initiation, stays GAP), **P2P-03** (dedicated P2P ConsentType,
stays PARTIAL), and **QNXT Augment** are unchanged and independently truthful. The
CI evidence pipeline derives the new result automatically — CHO Replace counts move
`PASSABLE 12 / PARTIAL 7 / GAP 2` → `PASSABLE 13 / PARTIAL 7 / GAP 1` with no
evidence-tooling change and no manual edit of generated evidence. Synthetic data
only; no PHI or payer configuration. Probabilistic matching, member enumeration,
and production P2P transport security (mTLS/UDAP) remain engagement work.

### Implement CHO-native Payer-to-Payer member data export (P2P-01)

Closed CMS-0057-F acceptance gap **P2P-01** (Payer-to-Payer inbound respond) with
the first production-shaped Payer-to-Payer vertical slice, Cloud Health Office
acting as the authoritative prior payer in Replace mode. New fhir-service
`PayerToPayer` domain: `PayerToPayerExchangeService` (application service) resolves
the transitioning member via a **tenant-scoped, deterministic** resolver
(`PayerToPayerMemberResolver` over `PatientAccessPayerToPayerMemberSource`, which
reuses the existing `IPatientAccessDataProvider` — no duplicate store), enforces
the member's opt-in **authorization** (fail-closed), and assembles a member-scoped
FHIR export with `PayerToPayerExportBuilder` reusing the existing CARIN/US Core
`PatientAccessMapper` (Patient + Coverage + ExplanationOfBenefit). A
`PayerToPayerExportPolicy` applies the locked 5-year date-of-service lookback (the
remittance/cost-sharing/drug exclusions are represented as predicates, gated on
data-model markers that do not exist yet). A thin `PayerToPayerController`
(`POST fhir/r4/PayerToPayer/$member-data-export`, under the SMART-enforced FHIR
surface, tenant taken from the authenticated context) routes to the service; all
logic lives in the service, not the controller. Member matching is safe:
insufficient criteria, no candidate, more than one candidate, a demographic
mismatch, a cross-tenant request, or a missing opt-in each fail explicitly and
never return another member's or another tenant's data. Every exchange yields an
audit entry. Real acceptance tests (`PayerToPayerExportTests`,
`[Trait("Backend","Replace")]`) exercise the production service/resolver/source/
builder/mapper: happy path, wrong member, no/ambiguous match, tenant boundary,
missing consent, empty-but-valid member, and the 5-year lookback. The prior P2P-01
GAP-assertion test is replaced by this behavioral coverage; the adapter-status
report moves PayerToPayer `OutOfScope → Demo` (inbound respond). The scenario
manifest moves P2P-01 `replace` `GAP → PASSABLE`. **P2P-02** (outbound initiation),
**P2P-03** (dedicated P2P ConsentType — stays PARTIAL), **P2P-04** (`$member-match`
/ concurrent coverage), and **QNXT Augment** are unchanged and independently
truthful. The CI evidence pipeline derives the new result automatically — CHO
Replace counts move `PASSABLE 11 / GAP 3` → `PASSABLE 12 / GAP 2` with no
evidence-tooling change and no manual edit of generated evidence. Synthetic data
only; no PHI or payer configuration. Production P2P transport security (mTLS/UDAP)
remains engagement work.

### Enforce drug exclusions in the CMS-0057-F prior-auth workflow

Closed CMS-0057-F acceptance gap **PAS-08** by implementing benefit drug/service
exclusion as real Cloud Health Office **Replace-mode** product capability, in the
authorization/benefit decision path (not a FHIR-controller or test-only check). New
`authorization-service` benefit-exclusion domain — `BenefitExclusion` model, a
configuration-driven, tenant-scoped `IBenefitExclusionCatalog` (no hard-coded
codes), a `DrugServiceCodeNormalizer` (NDC/RxNorm/HCPCS/CPT/service-type),
a pure `DrugExclusionEvaluator`, and an `AuthorizationExclusionService`.
`ChoAuthorizationBackend.CreateAsync` now consults it before the ordinary path: a
request for a drug/service the member's applicable plan excludes (or the pharmacy
service type, out of the CMS-0057-F medical scope) is recorded as a coded denial
(278 A3, structured `DenialReasonCode`) and persisted in the authoritative CHO
record with an auditable status history — never auto-approved by a generic rule.
A non-excluded request is unaffected. `RequestedService` gained an optional
`ProductOrServiceSystem` so a drug identity can be normalized. Real acceptance
tests (`DrugExclusionTests`, `[Trait("Backend","Replace")]`) exercise the
production backend + catalog + evaluator over a repository fixture — excluded drug,
non-excluded comparator, no-catalog, coverage scoping, code normalization, unknown
code, multiple exclusions, pharmacy service type, and PAS denied-response mapping
via `PasResponseBuilder.BuildDeniedResponse`. The prior PAS-08 GAP-assertion test is
replaced by this behavioral coverage. The scenario manifest moves PAS-08 `replace`
`GAP → PASSABLE` (rationale updated); QNXT Augment stays `N/A` (no external-core
drug-exclusion integration is claimed). The CI evidence pipeline derives the new
result automatically — CHO Replace counts move `PASSABLE 10 / GAP 4` →
`PASSABLE 11 / GAP 3` with no change to the evidence tooling and no manual edit of
generated evidence. Synthetic data only; no PHI, formulary, or payer configuration.

### Harden and publish CMS-0057-F acceptance evidence

Made the CMS-0057-F evidence pipeline fresh, traceable, and externally
understandable without exposing CI internals. The evidence workflow now also runs
when runtime/domain code that can affect CMS-0057-F behavior changes (the FHIR,
authorization, member, provider, claims, benefit-plan, consent, and smart-auth
services, plus the operating-mode and prior-auth-rule engines), not only when the
acceptance suite or evidence tooling changes. A new allow-list projection in
`tools/Cms0057Evidence` (`--public-output`) emits a sanitized
`cms0057-public-evidence.json` — built field by field, so it carries only schema
version, evidence status, commit SHA/short/URL, timestamp, synthetic
classification, framework, FHIR version, scenario count, a test-execution summary,
independent Replace (product) and per-backend Augment (integration) declared-status
counts, a per-scenario declared-status matrix, and disclaimers — never test names,
rationales, run identity, PHI, secrets, tenant data, or QNXT field mappings. The
projector refuses to publish a run with any failed test. Declared capability status
stays separate from execution: a passing GAP-assertion test remains **GAP** in the
public snapshot, never a pass. The workflow now splits into an `evidence` job
(PR + main; validates and uploads artifacts only) and a main-only `publish` job
(narrow `contents: write`) that commits the sanitized snapshot to the site tree; the
acceptance-scenarios page renders it under **Latest published evidence** with the
tested source revision and generation date, Replace shown as product capability and
each external core as separate integration capability. Public reporting avoids any
CMS-certification or universal-production-readiness claim, and known gaps stay
visible. New projector unit tests cover allow-list sanitization, GAP-stays-GAP,
independent Replace/Augment counts, deterministic ordering, fail-safe on missing or
failed input, and unknown future backends. No runtime service behavior changed.

### Versioned CMS-0057-F acceptance evidence in CI

Turned the CMS-0057-F acceptance suite into auditable, reproducible evidence
tied to a source revision. `tests/Cms0057Acceptance.Tests/scenarios.json`
(`schemaVersion: 1`) is now the machine-readable source of truth for scenario
status (`PASSABLE | PARTIAL | GAP | N/A`), scored on two independent axes —
Cloud Health Office **Replace** (product capability) and external-core
**Augment** (integration capability). New `ScenarioManifestTests` reconcile the
suite's `[Trait]`s against the manifest (unknown/duplicate ids, invalid statuses,
a scenario silently losing all its tests, or a PASSABLE-for-a-backend scenario
backed only by GAP-assertion tests all fail the build). New generator
`tools/Cms0057Evidence` reads the manifest, the acceptance TRX, and the suite's
traits and emits deterministic `cms0057-evidence.json`/`.md`/`.html` bound to the
full tested commit SHA, keeping **declared capability status** separate from
**test execution status** (a passing GAP-assertion test confirms the gap and is
never promoted to PASSABLE). New `CMS-0057-F Acceptance Evidence` workflow runs
the suite, generates the evidence, writes a job summary, and uploads the
`cms0057-acceptance-evidence-<sha>` artifact; it fails on test failure or
manifest/test drift. Evidence contains only synthetic identifiers, repository
metadata, and test results — no PHI, tenant data, secrets, or QNXT field
mappings. Generator has its own unit tests (`tools/Cms0057Evidence.Tests`). No
runtime service behavior changed.

### Cloud Health Office as the native CMS-0057-F authorization backend

Made Cloud Health Office the authoritative (Replace-mode) backend for the
prior-authorization CMS-0057-F vertical slice, distinct from external-core
(Augment-mode) integration. New authorization-service backend seam:
`Backends/IAuthorizationBackend` selected by operating mode via
`AuthorizationBackendSelector` (`Cms0057:Authorization:OperatingMode`,
default Replace). `ChoAuthorizationBackend` (Replace) is the CHO-native system
of record — a thin application layer over the existing `IAuthorizationRepository`
(Cosmos/Mongo) that persists submission, retrieval, status/decision lifecycle,
stable id, and an append-only `Authorization.StatusHistory`.
`QnxtAuthorizationBackend` (Augment) is a documented stub (throws; no fake SOAP)
selected only when configured — never a silent fallback to CHO; selection fails
loudly if the configured external backend is unregistered. Replaces PR #1143's
flat `IAuthorizationAdapter`. `AuthorizationsController` routes create through
the selected backend and exposes `GET /api/authorizations/backend-status`
(active mode/backend, no sensitive config). Reuses the OperatingMode engine's
`EngineOperatingMode`. The FHIR/PAS layer is unchanged and depends on no
vendor-specific abstraction.

The acceptance suite now distinguishes **product capability** (CHO Replace) from
**integration capability** (QNXT Augment): PAS-03 is product PASSABLE on
`ChoAuthorizationBackend` (exercised via an in-memory repository *fixture* so the
real production backend, not an acceptance-only path, is proven) and integration
GAP on QNXT Augment. Scenarios carry `[Trait("Backend","Replace"|"Augment")]`.
METRICS-01 product moved PARTIAL → PASSABLE (metrics derive from the persisted
CHO record). Inventory and the public acceptance page now score the two
dimensions separately and clarify Demo (synthetic) vs Replace (CHO authoritative)
vs Augment (external core). No runtime behavior change to shipped services beyond
the additive backend routing (Replace is the default and preserves prior
behavior).

### CMS-0057-F acceptance scenario suite

Executable acceptance harness (`tests/Cms0057Acceptance.Tests/`, in
`cloudhealthoffice-main.sln`) proving the CMS-0057-F scenario set against the
real C# services in Demo/Cho mode. Scenarios are tagged
`[Trait("Scenario","…")]` (PAS-01..08, PROV-01..03, P2P-01..04, PAT-01..03,
SEC-01, CONSENT-01, METRICS-01), each with a happy path and, for prior auth, a
negative path. GAP scenarios are tests that assert the still-unimplemented seam
(the QNXT source-system adapters, the out-of-scope Payer-to-Payer surface, the
absent drug-exclusion and retention paths) rather than papering over stubs.
Added `IAuthorizationAdapter` + `QnxtAuthorizationAdapter` documented stub in
authorization-service (mirrors the existing `Qnxt*Adapter` pattern) so the
PAS-03 QNXT create-auth seam is explicit and testable; it throws
`NotImplementedException` and is not wired into DI. Honest PASSABLE / PARTIAL /
GAP inventory and traceability table at
`docs/compliance/CMS0057-ACCEPTANCE-INVENTORY.md`; public definition-of-done
guide at `src/site/insights/cms-0057-f/acceptance-scenarios.html`. No change to
runtime behavior of shipped services.

### Layer 1 commercial packet (first-customer motion)

Founding-partner CMS-0057-F Compliance Accelerator offer ($90k / 6–8 weeks), CISO diligence binder (BAA template, security one-pager, adapter-status table, data-handling rules, 25-name target list), and a labeled synthetic demo tenant. `fhir-service` now exposes `GET /fhir/r4/adapter-status` and stamps `X-CHO-Adapter-Mode` / `X-CHO-Data-Class` / `X-CHO-Adapter-Label` on every response so mock adapters cannot look live.

### 835 remittance payment posting

`IRemittancePoster` posts a stored, matched 835 (`AvailableForPosting`) onto
claim financials and member benefit accumulators and marks the receipt
`Posted`. Source of the ERA is the remittance store — this does not invent
835s, change 277CA or 276/277, or reconcile EFT. Tenant comes from the
matched transmission. Duplicate posts replay. Failed claim or accumulator
writes abort without marking `Posted`. Gateway-only claims (no domain
claim) skip the claim sink. Accumulators use 835 PR deductible/copay/
coinsurance deltas with AdjustmentId `835|{remittanceId}|{claimId}`, not
`claims.finalized.v1`. Development: `POST /api/dev/gateway/remittance/{receiptId}/post`.

### Claim intelligence API

Vendor-neutral `IClaimIntelligenceComposer` composes 837 submission, 277CA
acknowledgment, 276/277 claim status, 275 attachments, and 835 remittance
into a tenant-scoped read model. Lifecycle status is derived without letting
one transaction overwrite another (277CA accepted is not paid; 276/277 paid
does not invent an 835). Financial and attachment summaries are
informational. Timeline event ids are stable, so duplicate deliveries do not
duplicate history. `GET /api/claims/{claimId}/intelligence`. The view is not
the system of record and does not post payment.

### 835 ERA remittance ingestion through Stedi

Vendor-neutral `IRemittanceGateway.RetrieveRemittanceAsync` with canonical
`GatewayRemittance`. Stedi transport is the 835 ERA Report
`GET https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/835`
after webhook or poll discovery. `IRemittanceProcessor` matches claims
deterministically (payer claim control number, then patient control number),
persists receipts, and emits identifier-only events. It does not post
payment, change 277CA, or overwrite 276/277 status. Development:
`POST /api/dev/gateway/remittance`.

Contract-tested against Stedi's documented 835 API; live ERA retrieve pending
production/test capability.

### 276/277 claim status inquiry through Stedi

Vendor-neutral `IClaimStatusGateway.CheckClaimStatusAsync` with canonical
`ClaimStatusRequest` / `ClaimStatusResponse`. Callers pass `ClaimId` or
`TransmissionId`; the coordinator derives payer, provider, subscriber, dates,
and control numbers from the original 837 snapshot and from a matched 277CA
payer claim control number when present. Stedi transport is Real-Time Claim
Status JSON `POST https://healthcare.us.stedi.com/2024-04-01/change/medicalnetwork/claimstatus/v2`.
276/277 status is a separate dimension from 277CA acknowledgment,
adjudication, and 835 payment. HTTP 200 with no matching claim is a business
`NoRecordFound`, not a transport failure. Mock returns deterministic
statuses for tests. Development:
`POST /api/dev/gateway/claims/{transmissionId}/status`.

Stedi test keys are not supported for this endpoint. Contract-tested against
the documented API; live inquiry pending production/test capability.

### Payer-side inbound 275 claim attachment receiver

Vendor-neutral `IClaimAttachmentReceiver` so Cloud Health Office can receive
a 275-equivalent attachment as the payer. Distinct from outbound
`IClaimAttachmentGateway`. Canonical `InboundClaimAttachment`, deterministic
claim/service-line matching, SHA-256 content store, durable receipts with
outbox, quarantine for unmatched attachments. Development:
`POST /api/dev/payer/claims/{claimId}/attachments`.

Stedi inbound payer-side 275 is **adapter-ready / pending Stedi payer
connectivity**, not implemented. Raw X12 275 ingress is deferred. Receipt
does not adjudicate or pay the claim.

### 275 claim attachment submission through Stedi

Vendor-neutral `IClaimAttachmentGateway.SubmitAttachmentAsync` with canonical
`ClaimAttachmentSubmissionRequest`. Bytes live in `IClaimAttachmentContentStore`
(existing `IDocumentStore` / Azure Blob when configured) as a content
reference plus SHA-256 — never on the claim aggregate. Attachments associate
deterministically to an existing 837 transmission (optional service line).
Stedi transport is Create Claim Attachment JSON
`POST https://claims.us.stedi.com/2025-03-07/claim-attachments/file` plus PUT
to the pre-signed URL. Unsolicited 275 only. MIME/size validated before
send. Attachment lifecycle is independent of 837 / 277CA / adjudication /
payment. Idempotency is
tenant+transmission+attachment+checksum+type+line+version.

Synchronous gateway acceptance is not payer review or claim payment. Live
275 is not claimed for sandbox accounts. Development:
`POST /api/dev/gateway/claims/{transmissionId}/attachments`.

### 277CA acknowledgment production hardening

Durable Mongo persistence for transmissions, 277CA acknowledgments, outbox,
and poll cursors. Non-Development hosts fail closed unless Mongo is
configured. `TryCreateAsync` is unique-index atomic. Outbox publication is
retried by a hosted dispatcher. Transmission state transitions are guarded
so malformed/duplicate events cannot rewind or overwrite a completed 277CA
outcome. Same-key 837 submit after 277CA is a replay.

### 277CA claim acknowledgment lifecycle through Stedi

Vendor-neutral `GatewayClaimAcknowledgment` plus `IClaimAcknowledgmentGateway`
retrieve and `IClaimAcknowledgmentProcessor`. Stedi discovers 277CAs via
`transaction.processed.v2` webhooks or Poll Transactions
(`core.us.stedi.com/2023-08-01`) and retrieves JSON from
`GET /2024-04-01/change/medicalnetwork/reports/v2/{transactionId}/277`.
Acknowledgments match deterministically to `#1111` transmission records.
Tenant comes from the matched transmission. 277CA accepted/rejected stays
separate from adjudication and payment. Duplicate webhooks are idempotent.

Stedi does not HMAC-sign claim-response webhooks; CHO authenticates the
configured credential-set header. Live 277CA testing is not claimed for
sandbox accounts.

Development: `POST /api/dev/gateway/claims/{transmissionId}/277ca`.
Production webhook: `POST /api/integrations/stedi/claim-responses`.

### Outbound 837 claim submission through Stedi

`IClaimSubmissionGateway.SubmitClaimAsync` is a real capability. Mock and
Stedi implement 837P / 837I / 837D against Stedi's documented JSON APIs
(`professionalclaims/v3`, `institutionalclaims/v1`, `dental-claims`).
Canonical `GatewayClaimSubmissionRequest` stays vendor-neutral. Payer
readiness reuses `IPayerReferenceService`. Durable
`IClaimTransmissionStore` records transmission state separately from
adjudication/payment. Idempotency is tenant+claim+version+type+frequency.

Synchronous gateway acceptance is not 277CA, adjudication, or payment.
Live 837 calls are not claimed for sandbox accounts (Stedi test claims
require a production-account test key). Development:
`POST /api/dev/gateway/claims`.

### Payer-side eligibility responder (inbound 270/271)

Vendor-neutral `IEligibilityResponder` so Cloud Health Office can act as the
payer/information source for an inbound eligibility inquiry. Canonical
`PayerEligibilityInquiry` / `PayerEligibilityResponse`, exact-match member
and dependent resolution, read-only coverage / benefit / accumulator access,
and a Development-only `POST /api/dev/payer/eligibility` ingress.

Stedi does not currently document a self-service inbound 270 payer-hosting
API. The Stedi inbound adapter is **adapter-ready / pending Stedi payer-side
connectivity**, not implemented. Existing outbound `IEligibilityGateway` /
Stedi eligibility is unchanged.

### Stedi dependent eligibility (270/271)

Canonical `GatewayEligibilityPerson` subscriber/patient model. Dependent
inquiries emit Stedi `dependents[]`; subscriber-only requests do not. Opt-in
live sandbox smoke covers the documented UHC 87726 John/Jane Doe Active
Coverage path.

### Stedi payer reference directory

Canonical, vendor-neutral payer identity for Cloud Health Office. Stedi List
Payers JSON (`GET https://payers.us.stedi.com/2024-04-01/payers`) synchronizes
into `IPayerReferenceService`. `StediHealthcareGateway` resolves eligibility
payers through that service; `PayerMap`/`TenantPayerMap` are deprecated
fallbacks. Arbitrary payer ids are no longer passed through to Stedi.

### v5.0 - Planned

**Enhanced Provider Management & Multi-Market Expansion**

- Enhanced practice management features for small-to-medium practices
- Advanced provider network analytics and reporting
- Expanded core system integrations (Epic Tapestry, additional CAPS)
- Mobile provider app (React Native) for iOS and Android
- Provider-facing scheduling and patient communication tools
- Enhanced claims scrubbing with AI-powered validation
- Multi-location practice support with centralized billing

---

## [4.4.0] - May 2026

### Claims Phase 1 — Closure

Closes the Cloud Health Office Claims-domain Phase 1 effort spanning 14 capabilities (5.1a–5.12b). Phase 1 delivers a full claim lifecycle end-to-end: submit → adjudicate (7-stage pipeline: Scrubbing 100 / Network 200 / BenefitCalculation 300 / NCCI 400 / CoB 500 / AI Examination 600 / Persistence 999) → pay (operator-initiated batched 835) → adjust (re-adjudication via predecessor chain) → reverse (operator-initiated batched negative 835).

**No new functionality.** This release is documentation-driven: capability matrix, end-to-end narrative, architectural-pattern index, Phase 2 backlog catalog, CMS-0057-F readiness assessment, canonical V1 API surface reference, and a portfolio module-status register.

#### Capabilities closed (PRs)

- **5.1a** ([#725](https://github.com/aurelianware/cloudhealthoffice/pull/725)) — Claim Identity & Versioning (versioning fields + Mongo event chain)
- **5.1b** ([#743](https://github.com/aurelianware/cloudhealthoffice/pull/743)) — Cosmos partition-key migration to `/tenantId`
- **5.2** ([#728](https://github.com/aurelianware/cloudhealthoffice/pull/728)) — Adapter pattern foundation
- **5.3** ([#729](https://github.com/aurelianware/cloudhealthoffice/pull/729)) — Claim Submission API (canonical V1 surface)
- **5.4** ([#734](https://github.com/aurelianware/cloudhealthoffice/pull/734)) — Pre-adjudication scrubbing + claims-scrubbing-service decommission
- **5.5** ([#731](https://github.com/aurelianware/cloudhealthoffice/pull/731), [#732](https://github.com/aurelianware/cloudhealthoffice/pull/732)) — Adjudication pipeline foundation
- **5.6** ([#733](https://github.com/aurelianware/cloudhealthoffice/pull/733)) — Network & credentialing enforcement
- **5.7** ([#736](https://github.com/aurelianware/cloudhealthoffice/pull/736)) — NCCI / MUE edits enforcement + projection bypass extension
- **5.8** ([#737](https://github.com/aurelianware/cloudhealthoffice/pull/737)) — Coordination of Benefits + Phase 2 hook stub
- **5.9** ([#738](https://github.com/aurelianware/cloudhealthoffice/pull/738)) — AI-Backed Examination pipeline stage
- **5.10** ([#740](https://github.com/aurelianware/cloudhealthoffice/pull/740)) — Operator-initiated batched 835 remittance + cross-service finalize
- **5.11** ([#739](https://github.com/aurelianware/cloudhealthoffice/pull/739)) — FHIR ExplanationOfBenefit projection
- **5.12a** ([#741](https://github.com/aurelianware/cloudhealthoffice/pull/741)) — Adjustment Workflow chain + re-adjudication
- **5.12b** ([#742](https://github.com/aurelianware/cloudhealthoffice/pull/742)) — ReversalRun batched 835 reversal + lifecycle wiring

#### Documentation surfaces shipped (this release)

- `docs/architecture/claims-phase-1-closer.md` — Phase 1 closer narrative (capability matrix, end-to-end lifecycle, 14-pattern architectural index, diligence-readiness checklist)
- `docs/roadmap/claims-phase-2-backlog.md` — Phase 2 backlog (48 items across 10 categories: inbound EDI, FHIR completeness, CMS-0057-F, COB priorEob, AI examiner, cross-service event-stream depth, operational, trading-partner transmission, reference-data workflows, infrastructure follow-ups)
- `docs/compliance/claims-cms-0057-f-readiness.md` — CMS-0057-F readiness posture (Phase 1 shipped vs Phase 2 required vs January 2027 mandate)
- `docs/api/claims-v1-surface.md` — canonical V1 API surface (8 controllers / 47 verbs across claims-service + payment-service customer-facing surfaces)
- `docs/status/MODULE-STATUS.md` — portfolio module-status register (initialized at Claims Phase 1 close; format mirrorable for future service-level closures)

#### Closer pattern established

Claims 5.13 establishes the **closer pattern** for service-level / domain-level Phase 1 / Phase 2 closures across Cloud Health Office. Future Provider Phase 2, BenefitPlan Phase 2, and other domain closures can mirror the structure: capability matrix → operational narrative → pattern index → phase boundary → diligence-readiness posture, with separate registries for backlog, compliance posture, and API surface.

#### No code changes

OpenAPI / Swagger surfaces continue to be served by both claims-service (via shared `AddChoInfrastructure`) and payment-service (via direct `AddSwaggerGen`) in development environments. payment-service Swagger pattern parity migration and XML-doc-driven Swagger surface enrichment are tracked as Phase 2 follow-ups.

#### Outstanding follow-ups (post-close)

- Legacy `Claims` Cosmos container deletion (~30-day retention window from 5.1b cutover; Bicep PR)
- Phase 2 sequencing per `docs/roadmap/claims-phase-2-backlog.md`. Primary near-term driver: CMS-0057-F unauthenticated patient access (January 2027 mandate).

## [4.3.0] - March 2026

### Capitation Service — PMPM Provider Payments

New microservice enabling per-member-per-month (PMPM) capitation payments from health plans to capitated providers. Structurally mirrors premium-billing-service (which collects premiums FROM sponsors) but pays TO providers.

**New Service: `capitation-service`**
- **CapitationContract** — provider agreements with 12-tier age-sex rate schedules, risk adjustment (HCC/RAF), quality withhold percentages, incentive pools, per-member and aggregate stop-loss thresholds
- **CapitationRunService** — monthly batch orchestration that fetches PCP panel rosters from coverage-service, risk scores from risk-adjustment-service, calculates proration for mid-month adds/terms, applies withholds, and generates provider payment statements
- **CapitationStatement** — provider-facing payment detail with member-level line items (base PMPM, risk score, adjusted PMPM, proration factor, gross/withhold/net), retroactive adjustments, and RecalculateTotals()
- **CapitationDisbursementService** — EFT payment lifecycle supporting NACHA ACH credits, Stripe Connect transfers, and paper checks, with ACH return handling (R01-R29 codes) and auto-retry logic
- **CapitationEraService** — X12 005010X221A1 835 ERA generation for capitation payments (CLP02=22, CLP06=CP, no SVC service lines, CAS CO-45 for withholds, PLB with WO/72/L6/FB adjustment codes)
- **NachaCreditFileService** — NACHA credit file generation (transaction codes 22/32 for checking/savings credits, SEC code CCD, service class 220, entry description CAPITATION)
- **StripeConnectService** — Stripe Transfer API integration for Connected Account payouts with webhook processing (transfer.created, transfer.reversed, payout.paid, payout.failed)
- Dual Cosmos DB / MongoDB repositories (8 files) with tenant isolation
- Kubernetes deployment manifest, Dockerfile, docker-compose entry (port 5012)

**Supporting Service Changes:**
- **coverage-service** — PcpNpi, PcpName, PcpAssignmentDate, PcpAssignmentMethod, PreviousPcpNpi fields on Coverage model; new PcpAssignmentMethod enum (AutoAssigned, MemberSelected, PlanDefault); `GET /api/v1/coverage/by-pcp/{npi}` endpoint for panel roster queries; compound indexes on (TenantId, PcpNpi, Status)
- **provider-service** — ProviderBankAccount model with EFT/Stripe Connect/check disbursement support, W-9/1099 compliance fields; DisbursementMethod, BankAccountType, TaxIdType enums; `GET/PUT /api/providers/npi/{npi}/bank-account` endpoints

**Portal — Capitation Management (3 new pages):**
- Capitation Contracts — data grid with contract#/provider/type/LOB/status/tiers/withhold, inline rate tier editor, activate/terminate actions
- Capitation Runs — create/execute runs with period selector, run list with provider count/member-months/net payable/duration, drill into statements
- Capitation Statements — filterable list, member-level breakdown with age/gender/PMPM/risk score/proration/withhold, approve/hold/void workflows, batch "Pay Approved" disbursement
- ICapitationService API client (16 methods), Capitation navigation group in sidebar

**Seed Data:**
- `seed-capitation.sh` — 3 demo contracts, 20 member PCP assignments, completed capitation run
- `seed-capitation-pcp-assignments.js` — mongosh script for Coverage.PcpNpi updates

**Tests: 176 new (163 unit + 13 smoke)**
- CapitationRunService, CapitationDisbursementService, CapitationEraService (28 X12 835 tests), NachaCreditFileService, StripeConnectService, all 4 controllers, TenantMiddleware, CosmosSerializer
- WebApplicationFactory smoke tests for full HTTP pipeline

### Metrics

| Metric                | Previous | Current  |
|-----------------------|----------|----------|
| Portal pages          | 47       | 50       |
| Microservices         | 23       | 24       |
| Service interfaces    | 20       | 21       |
| C# application lines  | ~74,800  | ~86,800  |
| Total code lines      | ~192,000 | ~204,000 |
| Automated tests       | 797      | 973      |

---

## [4.2.0] - March 2026

### Portal — Operations Depth

**New Pages:**
- Work Queues — claims examiner workflow with pend queue management by reason (NCCI, missing auth, provider not contracted, COB, medical review), priority tracking, and examiner assignment
- Appeals — search-first appeal tracking with regulatory deadline monitoring (MA 30-day standard, 72-hour expedited), appeal detail dialog with full lifecycle review
- Correspondence — outbound letter queue management (adverse determinations, EOBs, RFAIs, welcome letters) with RFAI response tracking and deadline monitoring
- Enrollment Operations — daily 834 file processing dashboard with transaction counts, adds/terms, and rejection detail

**Enhanced Pages:**
- Dashboard — added operational alerts (work queue count, pending RFAIs, appeals due), EDI transaction volume summary, and system health indicators
- Member Detail — added Accumulators tab with plan year deductible and OOP max progress bars, service-specific accumulator tracking, and recent claim activity affecting accumulators
- Claims — consolidated ClaimsNew into primary Claims page with advanced search (Claim ID, Member ID, Provider, status, date range)
- Settings — added Operating Mode tab showing per-engine Augment/Replace configuration with mode descriptions

**Portal Architecture:**
- Navigation reorganized into 6 collapsible groups (Operations, Members & Providers, Configuration, Finance, Monitoring, Admin)
- All PHI pages changed from [AllowAnonymous] to [Authorize]
- Search-first pattern enforced on all pages displaying member, claim, or authorization data (HIPAA minimum necessary)
- 5 new service interfaces and implementations (WorkQueue, Appeals, Correspondence, EnrollmentOperations, OperatingMode)
- Dashboard metrics corrected (approval rate and claims trend math)

### Engine & Infrastructure
- ClaimsScrubEngine — C# port of TypeScript validation rules with 20+ rules across 6 categories, wired into AdjudicationController
- OperatingMode engine — per-engine, per-tenant Augment/Replace toggle with AugmentResult<T> and discrepancy logging
- Seed scripts corrected to lowercase database name (cloudhealthoffice)
- Tenant onboarding checklist (TENANT_ONBOARDING_CHECKLIST.md) with Azure AD multi-tenant admin consent flow documentation
- Parameterized seed-demo-data.js (1,345 lines) for any-tenant seeding
- Parameterized seed-tenant.js for tenant provisioning

### Documentation
- README updated with accurate platform metrics and new sections
- Architecture diagram (SVG, Sentinel theme) replacing ASCII art
- Adjudication pipeline diagram showing 8-stage processing with latency
- Operating mode diagram illustrating Augment/Replace architecture
- Channel Partners section for implementation firm distribution model
- Adoption path rewritten to reference Operating Mode by name
- Codebase Scale section with line-count breakdown by language

### Metrics

| Metric                | Previous | Current  |
|-----------------------|----------|----------|
| Portal pages          | 43       | 47       |
| Calculation engines   | 7        | 9        |
| Service interfaces    | 15       | 20       |
| Portal Razor lines    | 14,622   | 16,279   |
| C# application lines  | ~72,900  | ~74,800  |
| Total code lines      | ~160,000 | ~192,000 |
| Total lines (w/ docs) | ~240,000 | ~303,000 |
| Automated tests       | 1,018    | 1,295    |

---

## [4.1.0] - February 16, 2026

### 📚 FHIR API Prominence & Developer Experience

**Commercial positioning and developer discoverability improvements**

Based on comprehensive repository assessment, this release transforms FHIR APIs from buried technical features into prominently featured commercial products.

#### OpenAPI Specifications
- **Patient Access API**: Full OpenAPI 3.1 spec for CMS-9115-F Patient Access API (FHIR R4, US Core 3.1.1+, CARIN BB)
- **Claims Scrubbing API**: Commercial pre-validation API with ROI metrics (95%+ first-pass rates)
- **Provider Access API**: Fixed filename typo (provider-accerss-api.yaml → provider-access-api.yaml)
- **Interactive Documentation**: Swagger UI embedded viewers for all APIs

#### Quickstart Guides (3 new guides)
- **CMS-0057-F Compliance** (15 min): Deploy → Test → Verify compliance before Jan 1, 2027 deadline
- **Patient Access API** (30 min): OAuth setup → Authentication → Build member portal (JS/Python/C# examples)
- **Claims Scrubbing API** (20 min): EHR integration patterns, batch validation, ROI calculator

#### Portal Redesign
- **Homepage**: CMS-0057-F deadline banner (Jan 1, 2027 urgency messaging)
- **Featured Section**: FHIR APIs now 2x grid space in prime dashboard position
- **New Page**: Dedicated API documentation hub (`api-docs.html`) with interactive viewers
- **Navigation**: Added "FHIR APIs" to main menu
- **Commercial Card**: Claims Scrubbing positioned alongside core compliance APIs

#### Impact
- **Before**: No OpenAPI specs, no quickstarts, APIs buried in src/fhir/
- **After**: 3 OpenAPI specs, 3 quickstart guides, prominent portal presence, 15-min onboarding
- **Files Changed**: 16 files, 2,895 insertions
- **Time to First API Call**: Reduced from hours to < 30 minutes

#### Documentation
- [Full Release Notes](docs/releases/v4.1.0-FHIR-API-PROMINENCE.md)
- [OpenAPI Specifications](api/openapi/)
- [Quickstart Guides](api/quickstarts/)

---

## [4.0.0] - February 11, 2026

### 🔒 Zero-Vulnerability Security Hardening

**100% vulnerability elimination** from 86 high-severity issues to absolute zero.

#### Security Fixes
- **CVE-2024-43485**: Fixed System.Formats.Asn1 RCE (8.0.0 → 8.0.1)
- **CVE-2024-21907**: Fixed Newtonsoft.Json deserialization attack (10.0.2 → 13.0.3)
- **Directory.Build.props**: Global transitive dependency enforcement
- **59 package updates**: Azure.Identity, Azure.Core, Microsoft.Azure.Cosmos, MudBlazor, Stripe.net, Swashbuckle, and 50+ more

#### Multi-Tenant SaaS Isolation
- **TenantContextService**: Maps Azure AD tenant → CHO tenant via subscription lookup
- **TenantHttpMessageHandler**: Injects `X-Tenant-ID` header on all backend API calls
- **Portal Isolation**: Prevents cross-tenant data leakage (CRITICAL security fix)
- **Dynamic UI**: Shows actual tenant name with demo/production badges
- **Logout Functionality**: Proper Microsoft Identity sign-out flow

#### Cloud Portability Infrastructure (Feature Branch)
- **CloudHealthOffice.Infrastructure Package**: Cloud-agnostic `IDocumentStore<T>` interface
- **Azure Implementation**: `CosmosDocumentStore<T>` (current production)
- **DigitalOcean Implementation**: `MongoDocumentStore<T>` (65% cost savings)
- **Reference Implementation**: member-service compiles with multi-cloud support
- **GitHub Actions Workflow**: 3-click toggles for Azure/DigitalOcean deployment
- **Status**: Available in `feature/multi-cloud-infrastructure` branch for testing

#### SFTP Trading Partner Integration
- **New Tenant**: clouddentaloffice (dental claims EDI)
- **Endpoint**: 20.115.193.245:22 (pending DNS: sftp.cloudhealthoffice.com)
- **Folder Structure**: /dental-claims/inbound/837/, /outbound/835/, /outbound/277/
- **Credentials**: Stored in Azure Key Vault

#### Infrastructure Updates
- **Azure Permissions**: Added Application Administrator & User Access Administrator roles
- **Deployment Gates**: Pre-approval checks in GitHub Actions
- **PII/PHI Scanner**: Configured to allow test data patterns
- **Logic Apps Migration**: Disabled deployment (moved to Argo workflows)

#### Package Highlights
- Azure.Identity: 1.12.1 → 1.13.1
- Azure.Core: 1.42.0 → 1.44.1
- Microsoft.Azure.Cosmos: 3.42.0 → 3.45.0
- MudBlazor: 7.20.0 → 8.4.0
- Stripe.net: 46.4.0 → 47.0.0
- Swashbuckle.AspNetCore: 6.5.0 → 10.1.2

#### Breaking Changes
- **Logic Apps Deployment**: Disabled in deploy.yml (use Argo workflows)
- **Multi-Tenant Headers**: Portal now sends `X-Tenant-ID` on all API calls (all services already compliant)

#### Known Issues
- DNS Configuration: sftp.cloudhealthoffice.com not yet pointed to 20.115.193.245
- Mock Data Fallback: Portal shows mock data when backend unavailable (configurable via `Portal.UseMockDataFallback`)
- Stripe.net Warning: NU1603 - Package 46.4.0 not found, resolved to 47.0.0 (non-breaking)

**Production Readiness**: ✅ Multi-Tenant Isolation | ✅ Security Hardening | ✅ HIPAA Controls | ✅ Zero Vulnerabilities

**Documentation**: [RELEASE-v4.0.0.md](RELEASE-v4.0.0.md), [MULTI-CLOUD-SETUP.md](MULTI-CLOUD-SETUP.md), [MULTI-CLOUD-DEPLOYMENT-GUIDE.md](MULTI-CLOUD-DEPLOYMENT-GUIDE.md)

---

## [3.0.0] - February 2026

### Dual-Market Healthcare Integration Platform

Cloud Health Office v3.0.0 is production-ready for both **health payers** (legacy system augmentation) and **healthcare providers** (practice management with direct EDI). This release delivers multi-cloud independence, CMS-0057-F compliance, and commercial launch readiness.

### Added

#### Multi-Cloud & Cloud Independence (December 2025)
- **Kubernetes/Helm Deployment**: Deploy Cloud Health Office to AKS, EKS, GKE, or any Kubernetes cluster
- **Argo Workflows Migration**: Cloud-native workflow orchestration replacing Azure Logic Apps
- **Apache Kafka Integration**: Cloud-agnostic messaging replacing Azure Service Bus
- **HashiCorp Vault Support**: Open-source secrets management as alternative to Azure Key Vault
- **Multi-Cloud Deployment Guide**: Comprehensive documentation for deploying across cloud providers

**Documentation**: [MULTI-CLOUD-DEPLOYMENT.md](./docs/MULTI-CLOUD-DEPLOYMENT.md), [ARGO-MIGRATION-GUIDE.md](./docs/ARGO-MIGRATION-GUIDE.md)

#### Argo Workflows for X12 EDI Processing (December 2025)
- **X12 275 Attachment Ingest Workflow**: Kubernetes-native SFTP polling and processing
- **X12 278 Authorization Request Workflow**: Cloud-agnostic prior auth handling
- **X12 277 RFAI Response Workflow**: Event-driven response generation via Kafka
- **X12 278 Replay Workflow**: Deterministic replay from Kafka offsets
- **Container Images**: X12 parser, encoder, SFTP fetcher, metadata extractor, Kafka publisher
- **Argo Events Configuration**: SFTP polling and Kafka event sources with sensors

**Documentation**: [ARGO-OPERATIONS.md](./docs/ARGO-OPERATIONS.md)

#### Azure Marketplace Readiness (December 2025)
- **Managed Application Plan**: ARM template deploying full Cloud Health Office stack
- **SaaS Plan with Meter-Based Billing**: Per-transaction pricing (837, 278, 275, FHIR API calls)
- **3-Tier Pricing**: Starter, Professional, Enterprise — [Contact sales](mailto:sales@cloudhealthoffice.com) for pricing
- **Partner Center Metadata**: Complete offer listing and marketing assets
- **Legal Documents**: Privacy policy, SLA (99.5%-99.95% uptime), support terms
- **Marketplace Icons**: Sentinel-branded SVG assets for all required sizes

**Documentation**: [marketplace/README.md](./marketplace/README.md)

#### Commercial Launch Materials (December 2025)
- **Sales Product Overview**: 2-page executive summary with competitive positioning
- **ROI Calculator**: TCO analysis and 5-year savings projections
- **Case Study Template**: Reusable template for pilot customer success stories
- **Financial Model**: 3-year projections with unit economics
- **Pitch Deck Content**: 15-slide framework for investor/customer presentations
- **Pilot Program**: 60-day structured pilot with success criteria
- **Sales Email Templates**: 5 targeted outreach templates
- **Marketing Landing Page Copy**: Conversion-optimized content

**Documentation**: [sales-materials/README.md](./sales-materials/)

#### VC Fundraising Strategy (December 2025)
- **VC Target List**: 12+ prioritized healthcare and SaaS VCs with investment thesis fit
- **Investor One-Pager**: Single-page investment summary
- **Due Diligence Checklist**: Legal, financial, technical, commercial preparation
- **Strategic Partner Targets**: 50+ partners including Microsoft, SIs, technology vendors
- **Investor Meeting Script**: 30-minute pitch framework
- **Warm Intro Templates**: 4 introduction request templates
- **Alternative Funding**: Grants (SBIR), RBF, venture debt, strategic investors
- **PR Strategy**: Thought leadership, podcasts, conferences, LinkedIn

**Documentation**: [fundraising/README.md](./fundraising/)

#### Microservices Architecture (December 2025)
- **Eligibility Service**: Azure Container Apps + Dapr with dual X12 270/271 and FHIR interface
- **ClaimRiskScorer Azure Function**: ML-powered fraud/abuse scoring (0-100) with PyTorch
- **Provider Directory API Logic App**: FHIR endpoints with NPPES NPI integration
- **Prior Auth API Logic App**: Da Vinci PAS CDex flow with 72-hour SLA tracking
- **Cosmos DB Integration**: PriorAuthorizations and ProviderDirectory containers

**Documentation**: [services/eligibility-service/README.md](./services/eligibility-service/)

#### CMS-0057-F Compliance Dashboard (December 2025)
- **Azure Monitor Workbook**: Real-time compliance metrics visualization
- **Patient Access API Tracking**: Enablement percentage with daily trends
- **Prior Auth SLA Monitoring**: 72-hour urgent and 7-day standard response tracking
- **Error Rate Analysis**: Transaction-level error tracking for 270/271, 278, 837
- **PHI Audit Trail**: Security operations monitoring via Application Insights

**Documentation**: [docs/AZURE-MONITOR-DASHBOARDS.md](./docs/AZURE-MONITOR-DASHBOARDS.md)

#### Migration Wizard (December 2025)
- **Blazor Web App**: `/tools/migration-wizard` for legacy system migration
- **Claims Backend SOAP Integration**: Paginated export via Open Access APIs
- **Cosmos DB Export**: Batch upsert for Members, ProviderDirectory, BenefitPlans
- **Mapping Report Generator**: 95%+ auto-match with field-level validation
- **One-Click API Cutover**: Routing key flip via Azure API Management
- **Azure Key Vault Integration**: Secure credential management

**Documentation**: [tools/migration-wizard/README.md](./tools/migration-wizard/)

#### 2026 Product Roadmap (December 2025)
- **Quarterly Milestones**: Q1-Q4 2026 with CMS compliance timeline
- **Microservice Releases**: eligibility-service v2.0, prior-auth-service v2.0, claims-service v1.0, remittance-service v1.0
- **Community Targets**: 500→7,500 GitHub stars, 15→150 contributors
- **OKRs**: Measurable success criteria for compliance, adoption, community, and AI

**Documentation**: [ROADMAP-2026.md](./ROADMAP-2026.md)

#### CMS-0057-F Whitepaper (December 2025)
- **Executive Whitepaper**: 7-page document for payer CIOs/CTOs
- **ROI Analysis**: 522% Year 1 ROI, 4.2-month payback period
- **TCO Comparison**: $16.7M legacy vs $2.6M Cloud Health Office (5-year)
- **Implementation Roadmap**: 12-16 week phased timeline
- **Mermaid Visualizations**: Gantt charts, TCO comparison, cost breakdown

**Documentation**: [docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.md](./docs/WHITEPAPER-CMS-0057-F-COMPLIANCE.md)

#### Community Governance (December 2025)
- **CONTRIBUTING.md**: Enhanced with DCO and CLA instructions
- **CODE_OF_CONDUCT.md**: Contributor Covenant 2.1
- **GOVERNANCE.md**: Steering committee election process
- **Issue Templates**: Feature request and bug report YAML forms
- **PR Automation**: Auto-labeling and reviewer assignment workflows

#### Platform Improvements (December 2025)
- **Vendor-Agnostic Refactoring**: Removed 1,295 vendor-specific references across 185 files
- **Container Build Workflow Fix**: Corrected image tags for vulnerability scanning
- **patient_access_api Workflow Fix**: Added missing `kind` and `parameters` keys

### Changed

- Updated README.md with Kubernetes deployment badge and dual architecture options
- Updated ARCHITECTURE.md with deployment options section
- Updated ROADMAP.md to reflect multi-cloud strategy progress (40% complete)
- Helm charts updated with HashiCorp Vault integration settings

### Fixed

- Container build workflow image tag mismatch for Trivy scanner
- patient_access_api workflow.json missing required keys
- PHI compliance issues with HTTPS enforcement for Vault URLs

### Security

- Storage Account networkAcls defaultAction set to "Deny" for HIPAA compliance
- Key Vault networkAcls defaultAction set to "Deny" for HIPAA compliance
- Managed Identity exclusively used for Cosmos DB/Event Grid access (no keys)
- All 424 tests pass with zero security vulnerabilities

### PRs Merged (17 since v2.0.0)

| PR | Title | Category |
|----|-------|----------|
| [#116](https://github.com/aurelianware/cloudhealthoffice/pull/116) | Remove vendor-specific references | Platform |
| [#115](https://github.com/aurelianware/cloudhealthoffice/pull/115) | Add multi-cloud deployment documentation and HashiCorp Vault integration | Multi-Cloud |
| [#114](https://github.com/aurelianware/cloudhealthoffice/pull/114) | Fix image tag mismatch in container build workflow | CI/CD |
| [#113](https://github.com/aurelianware/cloudhealthoffice/pull/113) | Migrate X12 EDI processing to Argo Workflows and Kafka | Multi-Cloud |
| [#112](https://github.com/aurelianware/cloudhealthoffice/pull/112) | Add VC fundraising strategy and materials | Commercial |
| [#111](https://github.com/aurelianware/cloudhealthoffice/pull/111) | Add comprehensive commercial launch materials | Commercial |
| [#110](https://github.com/aurelianware/cloudhealthoffice/pull/110) | Enhance CMS-0057-F whitepaper with ROI analysis and visualizations | Documentation |
| [#109](https://github.com/aurelianware/cloudhealthoffice/pull/109) | Add CMS-0057-F compliance whitepaper for payer executives | Documentation |
| [#108](https://github.com/aurelianware/cloudhealthoffice/pull/108) | Add 2026 product roadmap with CMS compliance milestones | Roadmap |
| [#107](https://github.com/aurelianware/cloudhealthoffice/pull/107) | Add community governance files, issue templates, and PR automation | Governance |
| [#106](https://github.com/aurelianware/cloudhealthoffice/pull/106) | Add Blazor migration wizard for legacy platforms to Cloud Health Office | Tools |
| [#105](https://github.com/aurelianware/cloudhealthoffice/pull/105) | Add Azure Marketplace offer structure with managed app and SaaS plans | Marketplace |
| [#104](https://github.com/aurelianware/cloudhealthoffice/pull/104) | Add ClaimRiskScorer Azure Function for 837 fraud/abuse risk scoring | Microservices |
| [#103](https://github.com/aurelianware/cloudhealthoffice/pull/103) | Add eligibility-service with dual X12 270/271 and FHIR interface | Microservices |
| [#102](https://github.com/aurelianware/cloudhealthoffice/pull/102) | Add CMS-0057-F Compliance Dashboard workbook for Azure Monitor | Compliance |
| [#101](https://github.com/aurelianware/cloudhealthoffice/pull/101) | Fix patient_access_api workflow missing required keys | Bug Fix |
| [#100](https://github.com/aurelianware/cloudhealthoffice/pull/100) | Add ProviderDirectoryApi and PriorAuthApi Logic Apps with NPPES integration | Microservices |

---

## [2.0.0] - 2025-11-28

### FHIR Frontier Forge

Complete CMS-0057-F compliance with production-ready FHIR R4 APIs, delivered 18 months ahead of the January 1, 2027 deadline.

### Added

#### V2 Release Notes Infrastructure (November 2024)
- **Release Notes Portal**: New `site/release-notes.html` with delivered features, sandbox testing, and early adopter signup
- **Documentation Updates**: Enhanced CMS-0057-F compliance documentation with post-FHIR implementation status
- **Site Navigation**: Added release notes links across all platform landing pages
- **V2 Announcements**: Updated site/index.html with v2 banners and CMS-0057-F/FHIR API announcements

**Documentation**: [Release Notes](./site/release-notes.html), [CMS-0057-F Compliance](./docs/CMS-0057-F-COMPLIANCE.md)

#### Complete FHIR R4 API Coverage (November 2024)
- **X12 837 → FHIR Claim**: Professional, Institutional, and Dental claims with Da Vinci PDex profiles
- **X12 278 → FHIR ServiceRequest**: Prior authorization with Da Vinci PAS/CRD compliance
- **X12 835 → FHIR ExplanationOfBenefit**: Remittance advice with complete adjudication details
- **X12 275 → FHIR DocumentReference**: Clinical attachments and supporting documentation
- **CMS-0057-F Compliance Checker**: Automated validation of data classes and timeline requirements
- **Azure FHIR Validator**: Profile validation integration with Azure API for FHIR
- **US Core + Da Vinci IGs**: Full PDex, PAS, CRD, DTR implementation guide conformance
- **45 Comprehensive Tests**: All FHIR mappers validated with 100% pass rate
- **Zero External Dependencies**: Secure core mappers with no runtime vulnerabilities

**Compliance Status**: Ready for January 1, 2027 CMS-0057-F deadline  
**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md), [CMS-0057-F-COMPLIANCE.md](./docs/CMS-0057-F-COMPLIANCE.md)

#### Provider Access API (November 2024)
- **Real-Time Patient Data Access**: FHIR R4 API for providers with patient authorization
- **SMART on FHIR Scopes**: `user/*.read`, `system/*.read` for provider/system access
- **NPI-Based Authorization**: Provider identity verification and access control
- **Consent Management**: Patient authorization tracking and revocation support

**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md#provider-access-api)

#### Payer-to-Payer Data Exchange (November 2024)
- **Bulk FHIR Export**: `$export` operation for efficient data exchange
- **5-Year Historical Data**: Configurable retention via Azure Data Lake lifecycle policies
- **Enrollment-Triggered Transfers**: Automated data exchange on member transitions
- **USCDI v1/v2 Coverage**: Complete data class support for interoperability

**Documentation**: [CMS-0057-F-COMPLIANCE.md](./docs/CMS-0057-F-COMPLIANCE.md#payer-to-payer-api)

#### Config-to-Workflow Generator (November 2024)
- **Zero-Code Payer Onboarding System**: Transform JSON configuration into complete deployment artifacts
- **Interactive Configuration Wizard**: Guided setup experience completing in <5 minutes
- **TypeScript-Based Generator**: 700+ lines of automation code with comprehensive validation
- **30+ Handlebars Template Helpers**: String, array, conditional, JSON, math, date, type checking utilities
- **Workflow Templates**: Automatic generation of Logic App workflow.json files
- **Infrastructure Templates**: Bicep templates with parameters and deployment scripts
- **Documentation Generation**: Payer-specific DEPLOYMENT.md, CONFIGURATION.md, TESTING.md
- **Example Configurations**: Medicaid MCO and Regional Blues templates included
- **23-Test Comprehensive Suite**: All passing with 100% validation coverage
- **CLI Tool**: Command-line interface with generate, validate, template, list commands

**Documentation**: [CONFIG-TO-WORKFLOW-GENERATOR.md](./docs/CONFIG-TO-WORKFLOW-GENERATOR.md), [IMPLEMENTATION-SUMMARY.md](./IMPLEMENTATION-SUMMARY.md)

#### FHIR R4 Integration (November 2024)
- **X12 270 → FHIR R4 Mapping**: Transform eligibility inquiries to Patient & CoverageEligibilityRequest
- **CMS Patient Access API Compliance**: Ready for CMS-9115-F requirements (14 months ahead of roadmap)
- **US Core Implementation**: US Core Patient profile v3.1.1 compliant
- **Standards Support**: HIPAA X12 270 (005010X279A1), HL7 FHIR R4 (v4.0.1)
- **Zero External Dependencies**: Core mapper with no runtime vulnerabilities
- **19 Comprehensive Tests**: 100% pass rate, covers all mapping scenarios
- **Production-Ready Security**: Secure examples using native fetch and Azure Managed Identity
- **Service Type Mapping**: 100+ X12 service type codes supported
- **Subscriber & Dependent Support**: Complete demographics handling

**Documentation**: [FHIR-INTEGRATION.md](./docs/FHIR-INTEGRATION.md), [FHIR-SECURITY-NOTES.md](./docs/FHIR-SECURITY-NOTES.md), [FHIR-IMPLEMENTATION-SUMMARY.md](./FHIR-IMPLEMENTATION-SUMMARY.md)

#### ValueAdds277 Enhanced Claim Status (November 2024)
- **60+ Enhanced Response Fields**: Comprehensive claim intelligence beyond basic status
- **Financial Fields (8)**: BILLED, ALLOWED, PAID, COPAY, COINSURANCE, DEDUCTIBLE, DISCOUNT, PATIENT_RESPONSIBILITY
- **Clinical Fields (4)**: Diagnosis codes, procedure codes, service dates, place of service
- **Demographics (4 objects)**: Patient, subscriber, billing provider, rendering provider details
- **Remittance Fields (4)**: Check/EFT details, payment date, trace numbers
- **Service Line Details**: 10+ fields per service line with configurable granularity
- **Integration Flags (6)**: Cross-module workflows for Appeals, Attachments, Corrections, Messaging, Chat, Remittance
- **Unified Configuration**: Complete valueAdds277 configuration in payer config schema
- **Premium Product Capability**: $10k/year additional revenue per payer
- **Provider ROI**: 7-21 minutes saved per claim lookup ($69,600/year for 1,000 lookups/month)

**Documentation**: [VALUEADDS277-IMPLEMENTATION-COMPLETE.md](./VALUEADDS277-IMPLEMENTATION-COMPLETE.md), [ECS-INTEGRATION.md](./docs/ECS-INTEGRATION.md)

#### Security Hardening (November 2024)
- **Premium Key Vault Infrastructure**: HSM-backed keys with FIPS 140-2 Level 2 compliance
- **Private Endpoints**: Complete network isolation for Storage, Service Bus, Key Vault
- **VNet Integration**: Logic Apps deployed in private virtual network
- **PHI Masking**: DCR-based transformation rules for Application Insights
- **Customer-Managed Keys**: Optional BYOK for regulatory requirements
- **Data Lifecycle Management**: 7-year retention with automated tier transitions (Hot → Cool → Archive)
- **Storage Cost Optimization**: 94% reduction ($463/mo → $29/mo) with lifecycle policies
- **HTTP Endpoint Authentication**: Azure AD Easy Auth for replay278 endpoint
- **Audit Logging**: 365-day retention with compliance queries
- **4 Bicep Modules**: keyvault.bicep, networking.bicep, private-endpoints.bicep, cmk.bicep (649 lines)
- **HIPAA Compliance**: 100% technical safeguards (§ 164.312) documented and implemented

**Security Score**: 9/10 (Target achieved)

**Documentation**: [SECURITY-HARDENING.md](./SECURITY-HARDENING.md), [HIPAA-COMPLIANCE-MATRIX.md](./docs/HIPAA-COMPLIANCE-MATRIX.md), [SECURITY-IMPLEMENTATION-SUMMARY.md](./SECURITY-IMPLEMENTATION-SUMMARY.md)

#### Gated Release Strategy (November 2024)
- **Pre-Approval Security Validation**: TruffleHog secret detection, PII/PHI scanning, artifact validation
- **UAT Approval Workflow**: 1-2 required approvers, triggers on `release/*` branches
- **PROD Approval Workflow**: 2-3 required approvers, manual dispatch from `main` only
- **Security Context for Approvers**: Scan results visible before approval decision
- **Automated Audit Logging**: Complete deployment history with compliance queries
- **Communication Strategy**: Stakeholder notification matrix with pre/post-deployment templates
- **Emergency Procedures**: Hotfix approval process with 30-minute SLA
- **Rollback Automation**: Automatic rollback-on-failure for UAT, documented procedures for PROD
- **Health Checks**: Post-deployment validation of Logic Apps, Storage, Service Bus, Application Insights
- **Metrics & Reporting**: Deployment success rate, approval times, rollback incidents

**Documentation**: [DEPLOYMENT-GATES-GUIDE.md](./DEPLOYMENT-GATES-GUIDE.md), [GATED-RELEASE-IMPLEMENTATION-SUMMARY.md](./GATED-RELEASE-IMPLEMENTATION-SUMMARY.md)

#### Onboarding Enhancements (November 2024)
- **Interactive Configuration Wizard**: Step-by-step guided configuration with validation (scripts/cli/interactive-wizard.ts)
- **Synthetic 837 Claim Generator**: PHI-safe test data for 837P and 837I claims (scripts/utils/generate-837-claims.ts)
- **Azure Deploy Button Template**: One-click sandbox deployment via azuredeploy.json
- **E2E Test Suite**: Comprehensive health checks with JSON reporting (scripts/test-e2e.ps1)
- **CI/CD PHI Validation**: 18 automated tests prevent PHI exposure (.github/workflows/phi-validation.yml)
- **Troubleshooting FAQ**: 60+ solutions across 9 categories (TROUBLESHOOTING-FAQ.md)
- **Documentation Suite**: QUICKSTART.md, enhanced ONBOARDING.md with 3 deployment options

**Onboarding Time Reduction**: 96% (2-4 hours → <5 minutes)
**Configuration Error Reduction**: 87.5% (40% error rate → <5%)
**Test Coverage Increase**: 41% (44 tests → 62 tests)

**Documentation**: [QUICKSTART.md](./QUICKSTART.md), [ONBOARDING.md](./ONBOARDING.md), [ONBOARDING-ENHANCEMENTS.md](./ONBOARDING-ENHANCEMENTS.md)

#### Sentinel Branding (November 2024)
- **Complete Visual Identity**: Sentinel logo with holographic/neon circuit veins aesthetic
- **Branding Guidelines**: Comprehensive standards document (BRANDING-GUIDELINES.md)
- **Absolute Black Design**: Primary color palette with neon cyan (#00ffff) and green (#00ff88)
- **Segoe UI Bold Typography**: Consistent font usage across all materials
- **Landing Page Transformation**: Complete redesign with Sentinel aesthetic
- **Repository-Wide Enforcement**: Updated all references and documentation

**Documentation**: [BRANDING-GUIDELINES.md](./docs/BRANDING-GUIDELINES.md), [BRANDING-IMPLEMENTATION-SUMMARY.md](./BRANDING-IMPLEMENTATION-SUMMARY.md)

### Changed

- Enhanced README.md with comprehensive features section and new capabilities
- Expanded QUICKSTART.md with post-v1.0.0 feature details
- Updated DEPLOYMENT.md with security hardening deployment section
- Enhanced DEPLOYMENT-SECRETS-SETUP.md with Key Vault migration procedures

### Fixed

- Null safety improvements in configuration validator
- JSON validation for all generated artifacts
- Workflow structure validation for Logic Apps Standard requirements

## [1.0.0] - 2025-11-21

### The Sentinel Has Awakened

This is the first production release of Cloud Health Office — the source-available, Azure-native, HIPAA-engineered platform that ends decades of payer EDI pain.

### Added

#### Core Platform
- **Multi-Tenant SaaS Architecture**: Configuration-driven platform supporting unlimited health plans
- **CLI Onboarding Wizard**: Complete deployment from worksheet to production in <45 minutes
- **Zero-Code Payer Onboarding**: Add new payers via JSON configuration without custom development
- **Backend-Agnostic Design**: Works with any claims system (core admin systems, custom platforms, modern cloud solutions)

#### EDI Transaction Processing
- **275 Attachments**: Clinical and administrative attachment processing with file validation
- **277 RFAI**: Request for Additional Information outbound workflow
- **278 Authorizations**: Prior authorization requests (inpatient, outpatient, referrals)
- **278 Authorization Inquiry (X215)**: Real-time status checks for existing authorizations
- **278 Replay Endpoint**: HTTP endpoint for deterministic 278 transaction replay
- **837 Claims**: Professional, Institutional, and Dental claims submission support
- **270/271 Eligibility**: Real-time eligibility verification with 6 search methods
- **276/277 Claim Status**: Claim status inquiries with date range filtering
- **Appeals Processing**: Appeals submission and tracking with 8 sub-statuses
- **ECS (Enhanced Claim Status)**: Advanced claim status with extended data and 4 query methods

#### Clearinghouse Integration
- **Clearinghouse Integration**: Native SFTP and API connectivity
- **Change Healthcare Support**: Ready for integration
- **Optum 360 Support**: Ready for integration
- **Inovalon Support**: Ready for integration
- **Direct Payer Endpoints**: Configuration-driven connectivity

#### Security & Compliance
- **Zero-Trust Architecture**: Private-endpoint-only, no public IPs
- **Azure Key Vault Premium**: HSM-backed keys (FIPS 140-2 Level 2)
- **Private Endpoints**: VNet integration for Storage, Service Bus, Key Vault
- **PHI Masking**: DCR-based redaction in Application Insights
- **HIPAA Compliance**: 100% technical safeguards addressed
- **Automated Secret Rotation**: API keys and credentials rotate automatically
- **7-Year Data Retention**: Automated lifecycle management with tier transitions
- **Audit Logging**: 365-day retention in Log Analytics

#### Infrastructure as Code
- **Complete Bicep Templates**: All Azure resources defined in source
- **Logic Apps Standard Workflows**: 15+ production-ready workflows
- **Modular Security Components**: Key Vault, networking, private endpoints, CMK
- **Multi-Environment Support**: DEV/UAT/PROD configurations
- **GitHub Actions Pipelines**: Automated deployment with approval gates

#### Developer Experience
- **Configuration Schema**: JSON Schema Draft-07 with 200+ validation rules
- **TypeScript Interfaces**: Type-safe configuration handling
- **OpenAPI Specifications**: Complete API documentation
- **Example Configurations**: Medicaid MCO and Regional Blues templates
- **Comprehensive Documentation**: 20+ detailed guides

#### claims backend Integration (First in Source-Available)
- **Real-Time Correlation APIs**: Link attachments to claims
- **Appeals Registration**: Direct integration with claims backend Appeals API
- **Authorization Processing**: Complete authorization lifecycle management
- **Eligibility Verification**: Member eligibility checks with retry logic
- **Retry Logic**: 4 retries @ 15-second intervals for API calls

#### Monitoring & Observability
- **Application Insights Integration**: Telemetry and distributed tracing
- **PHI-Safe Logging**: Automated masking of sensitive data
- **Custom Metrics**: Authorization decisions, claim status, appeal tracking
- **Health Checks**: Automated verification post-deployment
- **Dead-Letter Queues**: Failed message handling and replay

### Key Highlights

- **Onboarding time reduction**: 6–18 months (legacy) → <1 hour
- **Professional services cost elimination**: $500k–$2M → $0 (bring-your-own-subscription)
- **First production-grade claims backend REST correlation** in source-available healthcare IT
- **Complete source code transparency**: No black boxes, fully auditable
- **Azure Marketplace ready**: Prepared for Managed Application publishing

### Platform Specifications

- **Deployment Target**: Azure (Logic Apps Standard, Data Lake Gen2, Service Bus)
- **Runtime**: Logic Apps Standard (WS1+ SKU)
- **Storage**: Azure Data Lake Storage Gen2 with hierarchical namespace
- **Messaging**: Service Bus Standard tier with topics
- **Security**: Premium Key Vault with HSM-backed keys
- **Monitoring**: Application Insights with PHI masking
- **Language**: TypeScript (generator), Bicep (infrastructure), JSON (workflows)

### Documentation

Complete documentation suite includes:
- **CONTRIBUTING.md**: Development workflow and setup
- **ARCHITECTURE.md**: System architecture and data flows
- **DEPLOYMENT.md**: Step-by-step deployment procedures
- **SECURITY.md**: HIPAA compliance and security practices
- **TROUBLESHOOTING.md**: Common issues and solutions
- **BRANDING-GUIDELINES.md**: Sentinel brand identity standards

### Breaking Changes

N/A - First release

### Security

- All dependencies audited and up-to-date
- No known vulnerabilities in production dependencies
- HIPAA compliance validated for all PHI handling paths
- Security hardening guide included in SECURITY.md

### Known Limitations

- Azure-only deployment (AWS/GCP support planned for Q1 2025)
- Integration Account X12 schemas must be manually imported post-deployment
- API connections require manual authentication configuration
- Azure AD Easy Auth configuration required for replay endpoints

### Migration Guide

N/A - First release

### Contributors

Special thanks to all contributors who made this release possible.

### License

BSL 1.1 - see [LICENSE](LICENSE) file

---

## The Sentinel Has Awakened

The monolith has landed.  
Legacy EDI integration is now optional.

**Just emerged from the void.**

Star ★ the repo if you believe payers deserve better than 1990s technology in 2025.

---

[4.2.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.2.0
[4.1.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.1.0
[4.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v4.0.0
[3.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v3.0.0
[2.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v2.0.0
[1.0.0]: https://github.com/aurelianware/cloudhealthoffice/releases/tag/v1.0.0

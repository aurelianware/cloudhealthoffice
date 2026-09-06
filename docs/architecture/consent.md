# Consent

How Cloud Health Office decides whether a member has authorized a particular
disclosure. One registry, one policy, evaluated server-side at the moment the
disclosure is attempted.

Acceptance scenarios: **CONSENT-01** (single registry, both purposes enforced),
**P2P-03** (Payer-to-Payer authorization), **PROV-03** (opt-out honored).

## The two axes

A consent record answers two different questions, and CHO keeps them separate:

| Axis | Type | Question |
| --- | --- | --- |
| `ConsentType` | `ConsentService.Models.ConsentType` | *What regulatory instrument is this record?* — `TpoDisclosure` (§164.506), `GeneralAuthorization` (§164.508), `SensitiveCategoryAuthorization` (42 CFR Part 2 / state-law categories) |
| `PurposeOfUse` | `CloudHealthOffice.Consent.Contracts.ConsentPurposeOfUse` | *What does it authorize the plan to DO?* — `PayerToPayerExchange`, `ProviderAccess`, or `Unspecified` |

These are orthogonal. A §164.508 authorization *for* Payer-to-Payer exchange and
a §164.508 authorization *for* Provider Access are the same instrument and
different permissions. Modelling the permission as a new `ConsentType` value
would have conflated the two, and would have meant a sensitive-category
authorization could not also be purpose-scoped.

`PurposeOfUse` follows the FHIR `Consent.provision.purpose` axis (HL7 v3
PurposeOfUse), so the record projects onto FHIR Consent when that projection
lands, rather than needing a CHO-specific extension.

### Payer-to-Payer has its own purpose

`ConsentPurposeOfUse.PayerToPayerExchange` is the *only* value that authorizes a
Payer-to-Payer exchange. `ConsentRegistryPayerToPayerConsentGate.RequiredPurpose`
names it as a constant and it is deliberately not configurable — a deployment
cannot widen which purpose satisfies P2P.

### Provider Access has its own purpose, and the two do not imply each other

`ConsentPurposeOfUse.ProviderAccess` is the only value that authorizes a provider
to read an attributed member's record, named as a constant on
`ProviderAccessAuthorizationService`.

The separation runs both ways and is structural, not a routing convention. A
member with an Active `ProviderAccess` consent and nothing else is **denied** for
Payer-to-Payer with `NoConsentForPurpose`; a member with only a
`PayerToPayerExchange` consent is **denied** for Provider Access with the same
reason. Nothing about the calling controller, route, or client changes that: the
purpose is compared as data inside one policy, so the two permissions cannot
drift together by accident.

### Generic consent satisfies neither purpose

`ConsentPurposeOfUse.Unspecified` authorizes **nothing** that requires an
explicit purpose. It is the default on new records and the value historical
records deserialize to.

## Lifecycle

`ConsentStatus` (Draft → Active → Revoked / Expired) is unchanged;
`ConsentStateMachine` still owns the legal transitions, and consents are still
never hard-deleted. `ConsentLifecycleStatus` in the contracts project mirrors it
by value for cross-service use, with a drift guard in the policy tests.

Two lifecycle facts are evaluated rather than trusted:

* **effective period** — evaluated at the **disclosure attempt**, never at an
  instant supplied by the caller. `PayerToPayerExchangeRequest.ExchangeDateUtc`
  anchors the export's lookback window and arrives on the request, so it is
  deliberately not the authorization instant: a back-dated value would otherwise
  be judged against consent that has since lapsed, and a forward-dated one
  against consent that has not started. A record is in force only when
  `EffectiveAt <= asOf`
  and (`ExpiresAt` is null or `> asOf`). The policy applies the period itself, so
  a record persisted as `Active` past its `ExpiresAt` still denies, and a future
  `EffectiveAt` denies with `NotYetEffective`;
* **status** — anything other than `Active` denies, with the specific reason
  (`Revoked`, `Expired`, `NotActivated`).

When a member holds several qualifying records for a purpose, the policy picks
deterministically: the latest-expiring record wins (unbounded before bounded),
then the highest `Version`, then the lowest `ConsentId` ordinal. The id is the
last tie-break and the one that makes the choice reproducible — without it two
otherwise-equal consents would resolve by whatever order the source happened to
return, so the same registry state could name a different authorization on a
second evaluation. The same ordering decides which near miss a refusal names.
Two evaluations of the same registry state at the same instant always return the
same consent id.

## The decision

`ConsentAuthorizationPolicy.Evaluate(tenant, member, purpose, snapshots, asOfUtc)`
is a pure function and the single place any purpose-scoped authorization is
decided. It returns a `ConsentDecision`: `Allowed`, a
`ConsentAuthorizationReason`, the purpose, the authorizing `ConsentId` and
version, and the evaluation instant.

Refusals carry *which* refusal it was — `NoConsentOnRecord`,
`NoConsentForPurpose`, `NotActivated`, `Revoked`, `Expired`, `NotYetEffective` —
because "they revoked it", "it lapsed" and "they never granted this purpose" are
different operational facts. Near-miss reasons are reported most-specific-first,
so a member whose P2P consent was revoked reports `Revoked`, not
`NoConsentForPurpose`.

Every snapshot is re-checked against the requested tenant **and** member before
it can authorize anything; a snapshot for another member cannot leak across.

## One registry

`consent-service` is the authoritative store. There is no second consent
collection, and no service keeps its own copy of a consent decision as a
standing grant.

`fhir-service` reads the registry through `IPayerToPayerConsentSource`:

* `HttpConsentRegistryConsentSource` — the production path. GETs
  `api/v1/members/{memberId}/consents/authorization-snapshots` on the named
  client `ChoConsentService`, with tenant and correlation headers propagated.
  Selected whenever `Services:ConsentServiceUrl` is configured;
* `ConfiguredPayerToPayerConsentSource` — Demo/test fallback. Holds records in
  the *same* shape the registry serves (purpose, status, period), so a Demo
  deployment exercises the real policy instead of a boolean allow-list. An empty
  catalog authorizes no one.

The source returns everything on record for the member; filtering by purpose and
evaluating lifecycle are the policy's job, so a source cannot widen
authorization by returning the wrong subset.

### PHI stays in the registry

`ConsentAuthorizationSnapshot` carries only what a decision needs: tenant,
member, consent id, purpose, status, period, version. The encrypted narrative
fields (`Reason`, `GrantedToName`, `GrantedToContact`, `Purpose`) never cross the
service boundary, and the snapshot type has no field to put them in.

## Enforcement

Two capabilities enforce through this registry, and both reach their answer
through the same `IConsentEvaluator` and the same pure
`ConsentAuthorizationPolicy`. The evaluator owns the fail-closed registry read;
the policy owns every lifecycle and purpose rule. Adding a purpose adds no
logic to either.

### Provider Access

A provider reading an attributed member's record requires
`ConsentPurposeOfUse.ProviderAccess` — **and** an adequate SMART scope, **and**
provider/member attribution, **and** an authenticated caller. Each control is
independent and mandatory; none implies another, and the composed decision fails
closed. Enforcement is a global MVC filter covering every member-scoped FHIR
resource, placed after tenant resolution and before any action body, so an
unauthorized request never assembles member PHI. Refusals are externally uniform
so they cannot be used to enumerate the membership.

See [Provider Access](provider-access.md) for the full flow.

### Payer-to-Payer

Enforcement is server-side and identical in both directions. Neither a receiving
payer, an initiating payer, nor an internal caller can assert consent: no request
type in the Payer-to-Payer surface has a consent field, and a test asserts that
by reflection over all four request types.

**Inbound** (`PayerToPayerExchangeService`, CHO as prior payer) evaluates before
any member data is assembled. A denial is a terminal `NotAuthorized` outcome, and
the audit entry records the reason.

**Outbound** (`PayerToPayerOutboundService`, CHO as new payer) evaluates
**twice**:

1. before the remote `$member-match` — so an unauthorized member's identity never
   leaves CHO at all;
2. again immediately before the export request — so a revocation that lands while
   the match is in flight stops the data request.

Both directions call the same gate against the same registry with the same
policy, so one direction cannot drift more permissive than the other.

### Revocation semantics

Consent is evaluated at the moment of the disclosure attempt, not carried forward
from an earlier answer:

| When revocation lands | Effect |
| --- | --- |
| Before the exchange starts | Exchange never begins; `NotAuthorized` |
| Between the match and the export | Export is not requested; the exchange fails `NotAuthorized` and no member data is retrieved |
| After the export has been received | That package is already retrieved; it is not retroactively unwound. The next exchange is denied |
| Before a retry of a failed exchange | `ResetForRetry` clears the recorded decision, so the retry re-asks and is denied |

The exchange is not, and does not try to be, cancellable mid-HTTP-response.
The guarantee is that **no new disclosure crosses a boundary on a stale
decision**.

### The exchange records what authorized it

Both the outbound exchange record and both audit entries carry
`AuthorizingConsentId`, `ConsentDecisionReason`, and (on the exchange)
`ConsentEvaluatedAtUtc`. A completed exchange names the specific consent record
that permitted it; a refused one names the reason it did not.

## Migration and backward compatibility

**No existing consent is reinterpreted.** Records written before `PurposeOfUse`
existed deserialize to `Unspecified` and authorize nothing purpose-specific.
There is no backfill, no inference from `ConsentType`, and no "treat Active as
P2P" fallback anywhere in the policy.

The practical consequence is deliberate: **a deployment upgrading to this code
authorizes zero Payer-to-Payer exchanges until members' consents are recorded
with `PurposeOfUse = PayerToPayerExchange`.** Failing closed on a live
disclosure path is the correct default; the alternative would silently widen
every historical authorization.

Existing behavior that does **not** change: `ConsentType`, the state machine and
its transitions, the event stream (the payload gains a `purposeOfUse` field), the
encrypted fields, and every existing endpoint's contract. `PurposeOfUse` is
optional on create.

## API

All under `api/v1/members/{memberId}/consents`; tenant always comes from the
request context, never from the body.

| Endpoint | Change |
| --- | --- |
| `POST /` | accepts optional `purposeOfUse`; defaults to `Unspecified` |
| `GET /`, `GET /{consentId}`, `GET /{consentId}/history` | unchanged shape, now include `purposeOfUse` |
| `POST /{consentId}/activate`, `POST /{consentId}/revoke` | unchanged |
| `GET /authorization-snapshots?purposeOfUse=` | **new.** PHI-free projection for service-to-service authorization. Optional purpose filter |

`authorization-snapshots` exists so another service can make an authorization
decision without reading consent narrative. It returns snapshots only — no
reason, grantee, or purpose text.

## Limitations

* Provider attribution — one of the four Provider Access controls — is served
  from a **configured panel catalog**. It enforces for real and fails closed, but
  no live roster feed from a payer source system is wired up; that remains
  engagement integration behind `IProviderAttributionSource`.
* No FHIR `Consent` resource projection yet — `PurposeOfUse` is aligned to
  `Consent.provision.purpose` in anticipation of it.
* Consent versioning is modelled on the snapshot (`Version`) and used for
  deterministic selection; the registry does not yet mint versions on amendment.
* Zero GAPs in the acceptance suite is **not** complete CMS-0057-F compliance.
  This is implementation evidence, not attestation.

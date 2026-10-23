# ADR 028: Provider Access Serves Encounters as the Claim They Came From

## Status

Accepted, 2026-10-22. Narrows CMS-0057-F scenario **PROV-01**, which had
recorded "encounters are not reshaped onto the non-financial
`ExplanationOfBenefit`" as an outstanding limitation. It is not one.

## Context

Da Vinci PDex points the Provider Access API at CARIN Blue Button's
**non-financial ("Basis") `ExplanationOfBenefit`** profiles, and puts *both*
claims and encounters inside them. There is no FHIR `Claim` and no FHIR
`Encounter` in a Provider Access response.

CHO built the claims half of that in
[`CarinNonFinancialEobProjector`](../../src/services/claims-service/Fhir/CarinNonFinancialEobProjector.cs),
validated in CI against the four Basis profiles (see
`provider-access-non-financial-eob.md`). The matrix then recorded the encounter
half as remaining work: a second projector, over encounter-service's records,
emitting the same profiles.

Before building it we asked what a CHO "encounter" actually is. The answer
changes the decision.

## What the code shows

**`encounter-service` is outbound only.** `EncounterService.Models.Encounter`
is an 837 submission envelope addressed to a trading partner: it carries
`PayerId` ("Payer ID / Trading partner that receives the 837"), batch dispatch
(`BatchId`, `POST api/encounters/batch/dispatch`), interchange control numbers,
and 999/277CA acknowledgment state. `ClaimId` — "Source claim ID (from Claims
Service) that this encounter wraps" — is `[Required]` on the model. Nothing
ingests encounters into this service.

**`encounter-submission-service` is generated from adjudicated claims.**
`AdjudicationCompletedConsumer` listens on the `adjudication-completed` topic
and creates an `EncounterSubmission` when a claim is Approved, Paid or
PartiallyPaid and the tenant has a `StateCode` — the 60-day FMMIS window. When
`BuildFmmisSubmissionBatchAsync` builds the file, it fetches each **claim**
from claims-service (`GET /api/claims/{claimId}`) and submits that. It never
reads encounter-service.

**Inbound 837s become claims.** `X12837ClaimMapper` in claims-service maps an
inbound 837 — professional, institutional or dental, capitated submitters
included — to a `Claim`. There is no path by which a rendered service reaches
CHO as an encounter and not as a claim.

So CHO is the MCO *reporting* its paid claims onward as encounters. claims-service
is the single record of what was rendered; the encounter stores are downstream
of it.

## Decision

**Do not build a second non-financial EOB projector over encounter records.**
An attributed provider receives the member's encounter data as the
`ExplanationOfBenefit` projected from the claim that encounter was generated
from.

Three reasons, in order of weight:

1. **It would duplicate.** Every encounter names a claim CHO already serves on
   this path. Projecting both hands a provider two EOBs describing one
   real-world visit, with no way for the provider to tell they are the same
   service.
2. **The duplicate would be worse than the original.** The encounter record
   carries a subset of the claim: no type of bill (so every institutional
   encounter would have to declare the *Outpatient* Institutional Basis profile
   regardless of the actual setting), no coordination of benefits, no admission
   detail beyond dates. A provider who read the encounter EOB instead of the
   claim EOB would get less.
3. **It would expose a lifecycle the provider has no interest in.** The
   encounter record's statuses are EDI dispatch states — queued, batched,
   999-acknowledged, 277CA-rejected — which describe CHO's regulatory reporting,
   not the member's care.

PDex's "claims and encounter data" is therefore satisfied on this path, because
in CHO the encounter data **is** the claims data.

The FHIR `Encounter` projection (ADR 024) stays exactly where it is. It is a
legitimate CHO-native resource for consumers who want the contact itself, and
it remains the source record behind the submission pipeline. It is simply not
the Provider Access payload.

## Consequences

- PROV-01 keeps **profile conformance alone** (G-14, G-60) as the reason it is
  not PASSABLE. "Encounters are not reshaped" is struck from its grounds.
- `ProviderAccessEncounterSourcesTests` (architecture suite) fails if an
  encounter upstream is ever added to the Provider Access EOB routing, so the
  next person to consider this reaches this ADR rather than rediscovering it.

## What would overturn this

The decision rests on a factual claim about CHO's data, and that claim has one
soft edge worth naming rather than hiding:

**Nothing enforces that `Encounter.ClaimId` resolves to a real claim.**
encounter-service never calls claims-service, so a client posting directly to
`POST api/encounters` could create an encounter for a visit claims-service never
saw. The duplication argument rests on the data model's stated intent plus the
behaviour of the only automated producer — strong evidence, but not an
invariant the code enforces.

So: **if CHO ever ingests encounters directly** — a capitated provider
submitting to encounter-service rather than through the claims intake, or an
inbound 837 path that writes encounters — then encounters become an independent
clinical record and this decision must be revisited. Enforcing the reference at
write time would turn the soft edge into an invariant; it was not done here
because it adds a cross-service call to a hot write path for a guarantee the
Provider Access path does not currently depend on.

# ADR 022: Payer-to-Payer Consent Comes From the Requesting Payer, Bound to It

## Status

Accepted. Closes the D-13 remainder (gap-matrix G-37, G-38).

## Context

Under CMS-0057-F the **new** payer asks the member to opt in to Payer-to-Payer
exchange and then requests data from the previous payer, which must respond. The
Da Vinci PDex guide carries that opt-in to the previous payer as an HRex
`Consent` resource with `$member-match`. The Consent names the requesting payer
as the information recipient and the previous payer as the source.

CHO, answering as the previous payer, did two things wrong:

- `$member-match` checked no consent at all.
- `$member-data-export` checked CHO's own registry for a Payer-to-Payer opt-in.

The registry check missed in both directions:

- **Too strict:** a member leaving CHO rarely records an opt-in with CHO, so
  legitimate requests failed.
- **Too loose:** an opt-in on record named no payer. Any correctly registered
  payer client could match any member, then export every opted-in member's
  data.

## Decision

### 1. `$member-match` requires the member's consent, naming the requesting payer

The request carries a `consent`: a FHIR Consent following HRex `hrex-consent`.
Before any member is looked at, CHO accepts it only when it:

- is `active`, with scope `patient-privacy` and category `IDSCL`, for a patient;
- carries the HRex `#sensitive` policy and not `#regular`;
- restricts nothing CHO would have to segment out of an export: no nested
  provision, `class`, `code`, `data`, `dataPeriod` or `securityLabel`;
- has `provision.type` `permit`, and its `provision.period` is in force now;
- names the **requesting payer** as an information recipient (an `IRCP`
  actor). The requesting payer is the payer the authenticated client is
  registered to, never a body value;
- names no party other than that payer and this plan;
- if it lists actions, includes `disclose`.

Parties are compared by identifier value. The identifier comes from the
actor's logical reference or from a contained Organization the actor points
to. Case, spaces and hyphens are ignored. Codes are read only when their
system is the HL7 one or not given. Period bounds must be FHIR dates or
zoned dateTimes. A partial date as an end runs through the end of its span.
The start may be up to five minutes ahead, to allow for clock skew between
payers.

**The Consent must be the member's.** Its patient, an identifier or a contained
Patient, must name the member who is matched (or exported). That means one of
the member's identifiers (their member id, or a subscriber id on their
coverage), or the contained Patient's family name and birth date both agreeing
with theirs. A bare reference CHO cannot resolve names no one. Otherwise one
Consent could be replayed for any member. A match the Consent does not name is
answered as an ordinary no-match.

An unacceptable consent answers 422. That decision is made before any member
lookup, so it reveals nothing about membership. A malformed Consent (a repeated
property, say) is refused the same way and never errors.

**`#regular` is refused.** That policy asks the plan to withhold sensitive
data, and CHO cannot segment sensitive data out of an export. Refusing is
better than over-disclosing.

### 2. A match records the consent for that payer

A successful match stores an attestation for the requesting payer, keyed by
(tenant, member, payer), in `p2p_consent_attestations`. It holds:

- the policy;
- the consent period;
- a SHA-256 fingerprint of the Consent as parsed;
- when it was recorded, and which client recorded it.

It holds no consent content. A newer consent from the same payer replaces the
older one.

### 3. The export is served only to a payer whose consent is in force

`$member-data-export` serves a member to the requesting payer only when that
payer's attestation is in force now.

A payer that skips `$member-match` can send the consent with the export
instead. It is checked the same way and recorded.

Another payer's consent, or a consent that has ended, opens nothing. CHO's
own registry no longer decides inbound requests.

### 4. Outbound sends the consent a peer can bind

When CHO is the new payer, the member's opt-in is still read from CHO's
registry. Both outbound calls now carry it as an HRex Consent:

- the member as a contained Patient (their id at the prior payer when known,
  family name and birth date), so the peer can bind it;
- CHO is the information recipient;
- the prior payer is the source;
- the period starts at the consent decision, backdated by the skew allowance;
- the policy is `#sensitive`.

## Consequences

Positive:

- A registered payer client can neither match nor export a member without
  attesting the member's consent: a Consent naming both that payer and that
  member. CHO relies on the requesting payer's attestation, as PDex intends.
  It cannot verify that the member actually signed.
- One payer's consent never opens the member to another payer.
- Inbound requests no longer depend on members having opted in with the plan
  they are leaving.

Tradeoffs:

- Requesting payers that send `#regular` or otherwise restricted consents are
  refused until CHO can segment sensitive data.
- CHO's `$member-match` input is still CHO's JSON shape, carrying the Consent
  as a field, not the PDex `Parameters` resource (P2P-04 conformance).
- A recorded consent stays usable until its period ends. If the member revokes
  with the requesting payer, that payer must stop asking. CHO is not told.
- An opt-out recorded in CHO's registry does not refuse inbound requests that a
  requesting payer's consent permits. **This needs sign-off from the plan's
  compliance owner.** A follow-up can make an in-force CHO opt-out refuse
  inbound exchange as well.
- A payer with no consent can still tell a known member (403) from an unknown
  one (404) on the export. That predates this change.

## Scope boundaries

- Per-issuer scope allowlists (G-39) are unchanged.
- PA data in the export (G-35) is still missing.
- Bulk Payer-to-Payer transfer is unchanged.

## References

- `src/services/fhir-service/Services/PayerToPayer/PdexConsent.cs`
- `src/services/fhir-service/Services/PayerToPayer/PayerToPayerConsentAttestations.cs`
- `src/services/fhir-service/Services/PayerToPayer/PayerToPayerMemberMatchService.cs`
- `src/services/fhir-service/Services/PayerToPayer/PayerToPayerExchangeService.cs`
- `src/services/fhir-service/Services/PayerToPayer/Outbound/PayerToPayerOutboundService.cs`
- `docs/epa-gap-matrix.md` D-13, T-1, G-37, G-38, §11 P2P-01, P2P-03, P2P-04

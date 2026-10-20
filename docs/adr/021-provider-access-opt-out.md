# ADR 021: Provider Access Shares Unless the Member Opts Out

## Status

Accepted. Fixes gap-matrix defect D-11 (G-09).

## Context

CMS-0057-F requires impacted payers to share a member's data with the member's
attributed providers through the Provider Access API **unless the member opts
out** (42 CFR 422.121(b), 431.61(b), 457.731(b), 45 CFR 156.222(b)). Payer-to-Payer
is the opposite: it needs the member's opt-in.

CHO decided both through the same consent registry and the same policy, and the
policy answered one question only: "is there an active consent for this
purpose?" So Provider Access needed an opt-in. An attributed provider got
nothing for a member who had never recorded anything, which is most members.

## Decision

### 1. A consent record permits or refuses its purpose

Consent records gain a **provision**, `Permit` (the default) or `Deny`, as FHIR
`Consent.provision.type` has. A `Deny` record is an opt-out. It has the ordinary
lifecycle:

- activating it opts the member out from its effective date;
- revoking it opts them back in;
- it lapses at its expiry.

Records written before the field existed read as `Permit`, so none changes
meaning. consent-service refuses a `Deny` that names no purpose, because
refusing "no purpose" refuses nothing.

### 2. Opt-out purposes have their own evaluation

`ConsentAuthorizationPolicy.EvaluateOptOut` decides a purpose that is allowed
unless refused. It keeps the tenant, member and purpose matching of the opt-in
policy. Then, in order:

1. An in-force opt-out refuses (`OptedOut`). It wins over any permit.
2. An in-force permit allows, and is named as the authorization.
3. A permit **the member revoked** refuses (`Revoked`) unless the member has
   spoken since. Under the opt-in model, revoking was how a member said no, so
   that refusal is honoured, not silently reversed.
   - The member's newer word is a permit activated later, or an opt-out
     revoked later.
   - A revocation for an administrative error, a supersession or an expiry is
     not a refusal.
   - When there are no timestamps to compare, the refusal stands.
4. Otherwise the purpose is allowed (`NotOptedOut`), and no consent is named.

The opt-in policy (Payer-to-Payer) also honours opt-outs. An in-force `Deny`
refuses the purpose whatever permits exist, and a `Deny` never grants. Any
provision value other than a recognised `Permit` counts as an opt-out.

The authorization snapshot carries what this needs and nothing narrative: the
provision, the activation and revocation instants, and whether a revocation
was the member's own choice.

### 3. An unreadable registry refuses

For an opt-out purpose, "no records" means "never opted out". So a registry
that cannot be read must never look like an empty one. The HTTP registry
source now throws on any answer other than a readable 200: unreachable, 401/403,
5xx, 404, or an unreadable or empty body. The evaluator turns that into a
refusal (`RegistryUnavailable`) for every purpose.

With no registry URL configured, fhir-service uses its configured consent
catalog only in Development and Testing, or where a deployment deliberately
configured one (a demo). Anywhere else it uses a source that refuses every
decision, so an empty catalog can never mean "nobody opted out". The Kubernetes
manifest now sets `Services__ConsentServiceUrl`.

### 4. Provider Access uses the opt-out evaluation

`ProviderAccessAuthorizationService` asks `EvaluateOptOutAsync` for the
`ProviderAccess` purpose, after attribution. Attribution is unchanged. Sharing
by default is sharing only with the member's attributed providers, and an
unattributed provider is refused whatever the consents say. Purposes stay
independent: a Payer-to-Payer record neither opens nor closes Provider Access.

## Consequences

Positive:

- An attributed provider gets an attributed member's data unless the member
  has opted out, as CMS-0057-F requires.
- A member can opt out and back in through consent-service's existing create,
  activate and revoke endpoints, with the existing audit trail.
- A registry outage cannot turn sharing by default into sharing without
  checking.

Tradeoffs:

- Members who revoked an earlier opt-in themselves stay opted out until they
  record a newer choice.
- A demo's configured catalog shares with every attributed provider it does not
  list as opted out.
- A fhir-service deployed without the consent registry URL refuses all
  Provider Access and Payer-to-Payer decisions until the URL is set.
- The `consent.status-changed.v1` event still carries neither purpose nor
  provision. Nothing consumes it yet.

## Scope boundaries

- Notifying members about Provider Access and their right to opt out is the
  plan's process. CHO records the choice but sends no notices.
- Member-facing opt-out screens (portal) are not part of this change.
- Attribution still comes from static configured panels (PROV-02, G-33).
- Provider Access still carries no PA data (PROV-01, G-33).

## References

- `src/services/shared/CloudHealthOffice.Consent.Contracts/ConsentAuthorizationPolicy.cs`
- `src/services/fhir-service/Services/Consent/ConsentEvaluator.cs`
- `src/services/fhir-service/Services/PayerToPayer/HttpConsentRegistryConsentSource.cs`
- `src/services/fhir-service/Services/ProviderAccess/ProviderAccessAuthorizationService.cs`
- `src/services/consent-service/Models/Consent.cs` (`Provision`)
- `docs/epa-gap-matrix.md` D-11, G-09, §11 PROV-03, CONSENT-01

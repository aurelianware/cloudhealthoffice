# Provider Access attribution

**Scenario:** CMS-0057-F PROV-02. **Status:** claims history built 2026-10-22;
no live roster feed from a payer source system.

## What attribution is, and what it is not

Provider Access shares a member's record with a provider who is treating them.
Attribution is the control that decides who that is. It is not consent, and
neither control stands in for the other: a member may have opted out of
Provider Access and still be this provider's patient, and a provider may hold
a valid SMART token and have no relationship with the member at all.
`ProviderAccessAuthorizationService` asks both, independently, and refuses if
either says no.

## Two sources, because they answer different questions

`IProviderAttributionSource` has two implementations and a composite over them.

**`ConfiguredProviderAttributionSource`** reads panels from configuration. A
panel is an *assignment*: a member allocated to a PCP, which is true from the
moment the plan makes it and before any service is rendered. No claim can
evidence that, which is why this source is not replaced.

**`ClaimsHistoryProviderAttributionSource`** derives the relationship from the
plan's own claims. A member is this provider's patient when the tenant holds a
current, non-voided claim for them on which the provider is the **billing or
the rendering** NPI, with a service period ending inside a configured lookback.
This is *treatment that happened*, and it needs no roster feed to be true,
which matters because no payer source-system integration is wired up.

**`CompositeProviderAttributionSource`** composes them as a disjunction, not a
precedence order: either kind of relationship is a relationship. It is
deliberately not a `.Any()` over the sources — a source that throws has not said
"no", it has said nothing, so a fault propagates rather than letting another
source's answer stand in for the one that was never given.

Claims history is **off by default**. Enabling it is a deployment decision that
shows up in configuration review, and `/fhir/r4/adapter-status` names which
sources are live so a demo cannot be mistaken for a plan running on its own
data.

```jsonc
"Cms0057": {
  "ProviderAttribution": {
    "ClaimsHistory": { "Enabled": true, "LookbackMonths": 24 }
  }
}
```

A non-positive `LookbackMonths` fails start-up. It would attribute no one,
which is indistinguishable from a plan whose providers have genuinely treated
nobody — exactly the kind of misconfiguration that looks like correct
behaviour.

## Where each decision lives

fhir-service owns the **policy** — who may ask, and how far back treatment
counts — and resolves the lookback to an absolute instant before calling.
claims-service owns the **data** and answers one boolean:

```
GET /api/provider-attribution?provider={npi}&member={memberId}&since={utc}
→ { "attributed": true }
```

The window is therefore configured in one place rather than drifting between
two services, and `since` is required: defaulting it in claims-service would
silently widen attribution.

The response is a boolean and nothing else — no claim, no date, no count. A
caller permitted to ask about a member does not thereby get to read that
member's history, and the yes/no is all the authorization decision consumes.
An endpoint that could list a provider's whole panel would be a new bulk-PHI
surface nothing on this path asks for.

## The rule, and why each part of it

`ProviderAttributionRepositoryMongo.BuildFilter` is the rule. Proved against a
real mongod in `ProviderAttributionRepositoryTests`.

| Condition | Why |
|---|---|
| Tenant equals the asking tenant | A provider treating the same person under another plan has no access to *this* plan's record of them. |
| Billing **or** rendering NPI | A group bills and an individual renders; the clinician who saw the member is the commonest Provider Access caller and a billing-only rule would lose them. |
| Current chain head (not superseded, not draft) | A superseded version describes the same visit as its head, so counting it changes nothing but the cost. |
| Not `Voided` | A void withdraws the assertion that the service happened. A **denied** claim still counts: the plan refusing to pay is not the plan saying the visit did not occur. |
| `ServiceDateTo >= since` | The window is judged on the **end** of the service period, so an admission that began before the window and ran into it still counts — which is exactly where the relationship is strongest. |

## Failing closed

Every unreadable answer is a refusal, and the refusal is arranged so it cannot
be mistaken for a negative result:

- claims-service answers **503**, never `false`, when its own lookup faults.
- The fhir-service source **throws** rather than returning `false` on an
  unreachable, refusing, non-success or unparseable upstream.
- `ProviderAccessAuthorizationService` catches it and denies with
  `NotAttributed`.

The case that motivates all three is in the acceptance suite: a member who **is**
on the configured panel, with claims-service unreachable, is refused rather than
quietly falling back to whatever the configuration file happened to say.

## Limits

- **No live roster feed.** Panels are configuration. Wiring a payer's
  attribution file in is engagement integration behind the same interface.
- **Nothing enforces an NPI is real.** Attribution matches the NPI on the
  claim; whether the caller is entitled to that NPI is the identity layer's
  job (see `docs/architecture/idp-integration-contract.md`).
- **Claims history cannot see care that produced no claim.** For a wholly
  capitated arrangement with no encounter claim, the panel is the only source
  — another reason the two compose rather than replace.

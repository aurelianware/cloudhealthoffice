# Provider Access

CMS-0057-F Provider Access as Cloud Health Office implements it: how a provider
reads an attributed member's data, and the four controls that must all agree
before any member data is assembled.

Acceptance scenarios: **PROV-01** (attributed member data pull), **PROV-02**
(attribution enforcement), **PROV-03** (opt-out honored), **CONSENT-01**
(enforced through the one consent registry), **SEC-01** (SMART/OAuth).

## Provider Access is a caller shape, not a route

There is no `/provider-access` endpoint. Provider Access is the ordinary
`/fhir/r4/{Resource}` read surface reached by a **provider-shaped token** — one
carrying `user/…` or `system/…` scopes, meaning a provider or backend service
reading *someone else's* record.

A **patient-scoped token** (`patient/…` with a `patient` claim) is Patient
Access: the member reading their own record. It is governed by the SMART patient
binding, not by Provider Access consent — a member does not need to authorize a
disclosure to themselves.

This distinction is drawn from the token, never from a controller name or route
string, so it cannot be lost by adding an endpoint.

## The four controls

```
request
  └─ authentication            middleware   — is the caller who they say they are?
  └─ SMART scope               middleware   — may this client read this resource type?
  └─ provider/member attribution  filter    — is this member on this provider's panel?
  └─ ProviderAccess consent       filter    — has the member authorized this disclosure?
  └─ action body                            — member data is read only past all four
```

Every control is **independent and mandatory**:

| | does not imply |
| --- | --- |
| a correct SMART scope | attribution, or consent |
| attribution | consent |
| consent | attribution |
| a Payer-to-Payer consent | Provider Access |

The composed decision **fails closed**: any one refusal denies, and so does a
missing tenant, a missing member context, an unidentified caller, or an
unreadable registry.

## Where it is enforced

`ProviderAccessAuthorizationFilter`, a **global MVC action filter**.

*Why a filter and not middleware.* Provider Access needs the tenant, and
`TenantMiddleware` runs **after** `SmartScopeEnforcementMiddleware` in the
pipeline — a middleware sitting with the SMART check would have no tenant to
isolate on. A filter runs after the whole middleware pipeline, so
authentication, SMART scope and tenant are all established facts, and it still
runs **before any action body**, so an unauthorized request never assembles or
retrieves member PHI. That is the narrowest boundary that reliably holds all
four properties.

*Why global.* Registered once in `AddControllers`, not as a per-controller
attribute. A new member-scoped FHIR controller is governed the moment it exists;
there is nothing to remember to opt into. The filter decides for itself which
requests it governs, so non-Provider-Access traffic passes straight through.

### What it governs

Every member-scoped resource the FHIR surface serves — `Patient`, `Coverage`,
`ExplanationOfBenefit`, `Encounter`, `Claim`, `Task`, `Communication`,
`DocumentReference`, `ClaimResponse`. An acceptance test pins this inventory to
the SMART layer's own resource list, so a resource added to one cannot quietly
escape the other. Protecting `Patient` alone would leave the claims history
readable.

Not governed, deliberately: FHIR **operations** (`POST …/$member-match`,
`$member-data-export`). Those are a different surface with their own
authorization — Payer-to-Payer runs its own gate for the `PayerToPayerExchange`
purpose — and the operation name is not a member id.

### Resolving the member

`Patient/{id}` names the member directly; otherwise the member comes from an
explicit `?patient=` or the SMART `patient` binding. A resource id alone is
**not** resolved to a member, because resolving it means reading the resource —
which is the access being authorized. No member context therefore **denies**
rather than guessing, which is why a provider-shaped search across the whole
membership is refused.

## Attribution

`IProviderAttributionSource` answers "is this member on this provider's panel?"

Attribution is served from a **configured panel catalog**
(`Cms0057:ProviderAttribution`). It enforces for real — an empty catalog
attributes no one, and unknown provider, unknown member, or blank ids all return
false — but this is the honest state of the capability: **no live
roster/attribution feed from a payer source system is wired up.** That remains
engagement integration behind this same seam, and no code claims otherwise.

## Consent

Provider Access requires `ConsentPurposeOfUse.ProviderAccess` — see
[Consent](consent.md) for the purpose model, the lifecycle rules, the registry
and migration.

What matters here: it is the **same registry and the same policy** Payer-to-Payer
uses. Both call one `IConsentEvaluator` over one pure
`ConsentAuthorizationPolicy`; the only difference is the purpose asked for.
Neither direction can drift more permissive than the other, and there is no
second consent store.

The required purpose is a constant on `ProviderAccessAuthorizationService`, not
configuration. A Payer-to-Payer consent, an `Unspecified` consent, and a
historical generic Active consent all authorize nothing here.

Consent is evaluated at the **authorization instant**, chosen by the plan — never
a timestamp from the request.

## Refusals

One uniform external response: `403` with a FHIR `OperationOutcome` reading
*"Provider Access is not authorized for this request."*

"Not attributed", "no consent", and "no such member" are deliberately
**indistinguishable** from outside. A differentiated refusal would confirm which
members exist and let a caller enumerate the membership. An acceptance test
asserts the response bodies are byte-identical.

The structured category (`NotAttributed`, `ConsentDenied`, `NoMemberContext`,
`NoCallerIdentity`, `NoTenantContext`) is kept in the audit record instead, where
an operator can act on it.

## Audit

Every decision is recorded with PHI-free identifiers only: tenant, member id,
caller id, resource type, the authorizing consent id, the decision category, and
the evaluation instant. Grants log at information, refusals at warning.

Never logged: demographics, clinical payloads, consent narrative, tokens or
credentials, or endpoint URLs carrying identifying data. CR/LF is stripped from
any id that reaches a log line (CWE-117).

## Limitations

* **No live attribution feed.** Panels come from configuration; a payer's roster
  integration is engagement work behind `IProviderAttributionSource`.
* Attribution is membership-level, not date-scoped: a panel entry does not yet
  carry an effective period the way a consent does.
* Audit is emitted to the log, not to a durable audit store — fhir-service has no
  audit sink, and Payer-to-Payer has the same limitation.
* Provider Access does not yet surface Payer-to-Payer **imported** data; that is
  held in the import store and not projected into these reads.
* Zero GAPs in the acceptance suite is **not** complete CMS-0057-F compliance.
  This is implementation evidence, not certification.

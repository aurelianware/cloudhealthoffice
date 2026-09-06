# SMART on FHIR / OAuth trust

How Cloud Health Office validates tokens from an externally managed identity
provider, what it will and will not believe from one, and how a deployment
configures that trust.

Scope: CHO's FHIR **resource server**. CHO does not issue production tokens and
hosts no production authorization flow — the bundled `smart-auth-service` is a
development and acceptance-suite issuer only.

Related: [`provider-access.md`](provider-access.md) ·
[`fhir-conformance.md`](fhir-conformance.md) ·
[`idp-integration-contract.md`](idp-integration-contract.md)

---

## 1. Two identity modes, stated rather than inferred

`SmartAuth:Mode` is explicit:

| Mode | Trusts | Permitted where |
| --- | --- | --- |
| `Demo` | the bundled `smart-auth-service` | development hosts only |
| `ExternalIssuer` | the configured `TrustedIssuers` | anywhere |

**A `Demo` deployment on a non-development host fails startup.** The mode is not
derived from "is any external issuer configured?", because that would turn a
missing config file into a silent downgrade to demo trust — the one failure
nobody notices, since everything keeps working and CHO simply trusts the wrong
authorization server.

There is no production fallback to Demo. `SmartTrustOptions.Validate` throws, the
host does not start, and the error names the setting.

## 2. Trusted issuer configuration

```jsonc
"SmartAuth": {
  "Mode": "ExternalIssuer",
  "ClockSkewSeconds": 30,
  "TrustedIssuers": [
    {
      "Issuer": "https://payer-idp.example.com/oauth2/default",
      "Audiences": ["https://api.cloudhealthoffice.com"],
      "DiscoveryUrl": null,             // defaults to {Issuer}/.well-known/openid-configuration
      "JwksUri": null,                  // set to skip discovery entirely
      "Tenants": ["tenant-a"],          // empty = any tenant
      "AllowedAlgorithms": ["RS256"],   // empty = every supported asymmetric algorithm
      "AdditionalJwksHosts": [],        // hosts other than the issuer's own that may serve JWKS
      "Claims": {
        "TenantClaim": "cho_tenant",
        "ProviderNpiClaim": null,       // unset = no verified provider identity
        "PractitionerClaim": null,
        "FhirUserClaim": "fhirUser",
        "PatientClaim": "patient"
      }
    }
  ]
}
```

Trust is **administrator-controlled**. Nothing in a token can add an issuer,
change an audience, or redirect key retrieval.

The legacy single-issuer fields (`SmartAuth:Issuer`, `Audience`,
`RequireHttpsMetadata`) still work in `Demo` mode and are **ignored** in
`ExternalIssuer` mode, so a half-migrated configuration cannot keep trusting the
demo issuer by accident.

## 3. Issuer validation

A token is validated only if its `iss` **exactly** matches a configured issuer —
ordinal string comparison, after the registry has resolved that one entry.

Refused: unknown issuer, missing issuer, trailing-slash and case variants,
`https://idp.example.com.attacker.test`, `https://idp.example.com/../evil`, and
any non-HTTPS issuer outside development. An issuer carrying a query string or
fragment is refused at startup.

**The issuer is resolved first, and supplies everything else** — its keys, its
audiences, its algorithms, its claim mapping, its permitted tenants. The
alternative (a global key set, a global audience list) quietly makes trust the
*union* of every configured issuer's, so a token from issuer A would be accepted
bearing issuer B's audience and verified against B's keys. With one IdP that is
invisible; with two it is the difference between per-customer trust and one
shared trust blob.

CHO never fetches keys for an issuer it was not configured with. Trust-on-first-use
is not a weaker form of trust; it is the absence of it.

## 4. OIDC discovery

For a configured issuer, CHO fetches `{Issuer}/.well-known/openid-configuration`
(or the explicit `DiscoveryUrl`) and validates before anything becomes trust
material:

1. the document's own `issuer` equals the configured issuer **exactly**;
2. `jwks_uri` is present and HTTPS outside development;
3. the `jwks_uri` host passes the origin policy (§8).

An explicit `JwksUri` skips discovery — one less network-supplied value to
validate.

Discovery is **not** fetched per request. Results are cached in the key ring (§5).

## 5. Signing keys, rotation, and outage

| Setting | Value | Why |
| --- | --- | --- |
| Routine refresh | 12 h | keeps keys current without polling |
| Minimum refresh interval | 5 min | bounds attacker-triggered refreshes |
| Maximum stale age | 24 h | bounds trust in keys the issuer may have revoked |

**Rotation.** An issuer rotates on its own schedule and does not tell CHO. The
only signal is a token whose `kid` is unknown, so an unknown `kid` triggers a
refresh.

**That signal is attacker-controlled.** Anyone can present a token with a random
`kid`, so refreshes are rate-limited per issuer and **single-flighted**: a burst
of requests carrying one new `kid` produces exactly one fetch and the rest wait
for its result. No thundering herd, and a forged `kid` costs at most one fetch
per interval.

**Outage semantics.** Keys already retrieved stay usable while the issuer is
unreachable, up to the staleness bound. Within that window previously-seen `kid`s
keep working and unknown ones fail closed — an IdP outage degrades *rotation*,
never signature validation. Past the bound the keys are dropped, because
indefinitely stale trust would keep honouring a revoked key.

Failing to retrieve keys never disables authentication. It fails closed: no keys,
no validated tokens.

## 6. Algorithms

Accepted: `RS256/384/512`, `PS256/384/512`, `ES256/384/512`.

- **`none` is absent**, and `RequireSignedTokens` is set, so an unsigned token
  cannot validate.
- **HMAC is absent deliberately.** A symmetric verifier will accept a token
  signed with the issuer's *public* key as the shared secret — the classic
  alg-confusion attack. A resource server validating a third-party IdP only ever
  holds public keys, so admitting HMAC has no legitimate use and one
  catastrophic misuse. Symmetric keys are also filtered out of a JWKS at
  ingestion rather than relied on being unreachable later.

`AllowedAlgorithms` narrows this per issuer. Because the handler's own
`ValidAlgorithms` is the union across issuers, the per-issuer check is applied
again after signature validation.

## 7. Audience and lifetime

**Audience** is validated against the audiences of the issuer that actually
signed the token. A correctly signed token from a trusted issuer that was minted
for another API is refused — issuer plus signature alone would have accepted it.

**Lifetime**: `exp` required, `nbf` honoured, clock skew configurable but capped
at 300 s. Skew large enough to meaningfully extend a token's life is lifetime
validation switched off wearing a clock-drift costume, so the cap is enforced at
startup.

## 8. Discovery / JWKS SSRF protection

A fetch target is refused **unless configuration already named it**:

- the issuer's own host, or a host listed in `AdditionalJwksHosts`;
- HTTPS outside development;
- never a literal loopback, link-local (`169.254.0.0/16`, i.e. cloud instance
  metadata), or RFC1918 address outside development — an explicit allow-list
  entry does **not** override this bar.

A discovery document pointing `jwks_uri` at an unapproved origin is refused
*before* the request is made, because a refusal after the fetch has already
performed the SSRF.

Only literal IPs are checked. A DNS name resolving into private space is a
network-egress concern owned by the platform; re-resolving here would be a
TOCTOU check that reads as protection without being any.

## 9. Caller identity

One resolution per request, into `AuthenticatedCaller`: issuer, subject,
client id, authorized party, caller type, scopes, and — when the trusted issuer
maps them — provider NPI, practitioner id, `fhirUser`, patient, tenant.

Caller type follows the granted scopes. A token holding both `patient/` and
`system/` scopes is treated as **patient** context, because patient context is
the one that *constrains*: reading it as system would drop the patient binding
and widen the token, whereas reading it as patient only narrows it.

### Provider identity — the rule that makes it safe

An NPI is public information, so a claim merely *named* `npi` proves nothing.
It becomes authoritative only when a named issuer CHO already trusts was
configured, by an administrator, to assert it. CHO therefore:

- reads **only** the configured `ProviderNpiClaim`, never a conventionally named
  claim;
- validates the shape (ten digits) and drops anything else;
- treats absence as *"no issuer has vouched for this caller's identity"* — not
  as "the caller has no NPI".

**Default is unset.** With no mapping configured, `$submit-attachment` and
`$inquire` keep the corroborating-key behaviour documented in
[`prior-authorization.md`](prior-authorization.md): weaker, and honestly so.

Where an issuer **does** assert provider identity, CDex `$submit-attachment`
compares the *token's* NPI against the provider the request was addressed to.
That closes the substitution a corroborating key cannot detect — a caller who
knows another provider's public NPI and puts it in the payload. This only ever
tightens: deployments without a provider identity claim are unchanged.

## 10. Patient identity binding

Unchanged and preserved: patient identity comes from the token, never a query
parameter. A token bound to patient A cannot search `?patient=Patient/B` **or**
`?subject=Patient/B` — both member-naming parameters are enforced.

## 11. Tenant binding

Precedence:

1. the tenant claim from the token (the issuer's mapped claim, else `tenant_id` /
   `extension_TenantId`);
2. the `X-Tenant-ID` header, for service-to-service calls.

The header **may fill a vacuum, never contradict a token**. Previously it was
consulted whenever the token carried no tenant claim, so any authenticated caller
whose issuer did not map a tenant could name any tenant and be believed. Now a
header that disagrees with the token is a **403 tenant conflict**, not an
override.

`X-Dev-Tenant-ID` is honoured on development hosts only — on a production host it
would be an unauthenticated tenant selector.

Where an issuer declares `Tenants`, the resolved tenant must be one of them, so
customer A's IdP cannot authenticate into customer B's data however its claims
are shaped.

Tenant is never taken from a FHIR body or query parameter.

## 12. SMART discovery metadata

`/fhir/r4/.well-known/smart-configuration` advertises the **configured**
authorization server. In `ExternalIssuer` mode the authorization, token and JWKS
endpoints come from that issuer's own discovery document — real issuers disagree
about paths (Okta `/v1/authorize`, Entra `/oauth2/v2.0/authorize`, OpenIddict
`/connect/authorize`), so synthesizing them would point every client at endpoints
that do not exist. Before discovery completes the fields are omitted rather than
guessed.

CHO advertises only what it supports, and never an authorization flow it does not
host.

## 13. Readiness and fail-closed behaviour

| Condition | Effect |
| --- | --- |
| Demo mode on a non-development host | **startup fails** |
| No trusted issuer in ExternalIssuer mode | **startup fails** |
| Invalid issuer URI, missing audience, unsupported algorithm, bad skew, duplicate issuer, disallowed JWKS host | **startup fails** |
| IdP unreachable at startup | starts; readiness reports it |
| No issuer has keys | readiness **unhealthy** |
| Some issuers have keys | readiness **degraded** |
| Keys past refresh interval, inside staleness bound | readiness **degraded** |

Configuration errors fail startup; network conditions fail readiness. The
`smart-identity-trust` check reports **operational** trust state — a flood of
401s from expired tokens is a healthy resource server doing its job, and
conflating the two would make the signal fire during an attack and stay silent
during an outage.

The check exposes issuer names, key **counts**, retrieval times, and a failure
*category*. Never keys, never discovery payloads, never the IdP's error text.

## 14. Authentication failures

| Condition | Status |
| --- | --- |
| No token | 401 |
| Invalid signature, unknown issuer, wrong audience, expired, not-yet-valid | 401 |
| Valid token, insufficient SMART scope | 403 |
| Valid provider, no attribution or no consent | 403 (Provider Access, unchanged) |
| Token/header tenant conflict | 403 |

Authentication and authorization stay distinct internally as well as on the wire.

## 15. Audit and logging

Recorded: trusted issuer, opaque subject/client id, tenant, caller type, result,
failure category, timestamp. CR/LF stripped from every caller-influenced value
(CWE-117).

Never logged: bearer/refresh tokens, authorization codes, JWT payloads, client
secrets, private keys, raw IdP responses, patient demographics. A rejected token
is still a live credential, so failures log a *category*, not the token.

## 16. Secrets

CHO is a resource server and needs **no client secret and no private signing key**
to validate tokens — it holds public keys only. No OAuth client secret or
production signing key exists in this repository. The demo issuer's keys are
synthetic and development-only.

## 17. Limitations

1. **No production IdP is connected in this repository.** Every test issuer here
   is synthetic. Connecting a specific customer's IdP is deployment
   configuration plus the [integration contract](idp-integration-contract.md);
   the product-side capability is complete and tested, but a given engagement's
   IdP still has to be configured and validated against.
2. **Token introspection (RFC 7662) is not implemented.** CHO validates JWTs
   locally. An IdP issuing opaque reference tokens is not supported.
3. **No dynamic client registration.** Client registration belongs to the
   external IdP.
4. **Provider identity depends entirely on the IdP.** Where an issuer asserts no
   provider claim, PAS/CDex caller binding remains a corroborating key.
5. **Cross-origin JWKS requires explicit configuration.** An IdP serving keys
   from an undeclared host will fail closed until `AdditionalJwksHosts` names it.
6. **Only `fhir-service` uses this trust model.** Other services retain their own
   JWT configuration; extending them is follow-on work.
7. **Private-address detection covers literal IPs only** (see §8).

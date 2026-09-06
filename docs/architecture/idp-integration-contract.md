# Identity provider integration contract

What a payer's identity platform must provide for Cloud Health Office to
validate its tokens, and what CHO does with each value.

CHO is a **resource server**. It validates tokens and authorizes FHIR access; it
does not issue tokens, host an authorization flow, or register clients. Those
belong to your IdP.

No vendor-specific code exists in the product. The examples below are
configuration only.

Related: [`smart-oauth-trust.md`](smart-oauth-trust.md)

---

## 1. What you provide

| Item | Required | Notes |
| --- | --- | --- |
| Issuer URL (`iss`) | yes | HTTPS, no query or fragment; matched **exactly** |
| OIDC discovery document | yes¹ | `{issuer}/.well-known/openid-configuration`; its `issuer` must equal the configured issuer exactly |
| JWKS endpoint | yes | HTTPS; same host as the issuer unless you declare otherwise |
| Audience / resource identifier for CHO | yes | a value distinct from your other APIs |
| Signing algorithm | yes | one of `RS256/384/512`, `PS256/384/512`, `ES256/384/512` |
| `kid` on every signing key | strongly recommended | how CHO detects rotation |
| SMART scopes | yes | see §3 |
| Tenant claim | recommended | see §5 |
| Provider identity claim | optional | see §6 — changes what CHO can enforce |

¹ Or configure `JwksUri` directly to skip discovery.

**Not required:** client secret, private key, dynamic registration, introspection
endpoint. CHO holds public keys only.

## 2. Issuer and audience

```jsonc
{
  "Issuer": "https://payer-idp.example.com/oauth2/default",
  "Audiences": ["https://api.cloudhealthoffice.com"]
}
```

The audience must be **specific to CHO**. Reusing an audience shared with your
other APIs means a token minted for any of them would be accepted here.

If your JWKS is served from a different host than the issuer (some managed
platforms use a sibling CDN), declare it:

```jsonc
"AdditionalJwksHosts": ["keys.payer-cdn.example.com"]
```

Undeclared cross-origin JWKS **fails closed** — this is the SSRF boundary, not a
convenience check.

## 3. Scopes

Issue SMART v1-style scopes. CHO enforces resource and access separately; a read
scope never authorizes a write.

| Caller | Examples |
| --- | --- |
| Patient app | `patient/Patient.read`, `patient/Observation.read`, `patient/*.read` |
| Provider (user) | `user/Patient.read`, `user/Claim.write`, `user/Task.write` |
| Backend service | `system/Claim.write`, `system/Task.write`, `system/*.read` |

Writes CHO's surface actually serves: `Claim/$submit` (PAS),
`$submit-attachment` (CDex), DTR questionnaire authoring. There is deliberately
**no patient-context write scope** — every write here is a provider/payer
transaction.

Scopes may arrive in a space-delimited `scope` claim or repeated `scp` claims.

## 4. Patient context

For patient-facing apps, assert the member the token is bound to:

```json
{ "patient": "Patient/pat-001" }
```

Map a differently named claim with `Claims.PatientClaim`. CHO enforces this
binding against both `patient` and `subject` search parameters — a token for
member A cannot read member B's data by either route.

## 5. Tenant

```jsonc
"Claims": { "TenantClaim": "cho_tenant" }
```

The claim value must be the CHO tenant identifier. A token asserting a tenant
that disagrees with the request's `X-Tenant-ID` header is **refused** (403), not
reconciled.

For a multi-customer deployment, confine each issuer to its tenants:

```jsonc
{ "Issuer": "https://customer-a.example.com", "Tenants": ["tenant-a"] }
```

Customer A's IdP then cannot authenticate into customer B's data however its
claims are shaped.

## 6. Provider identity (optional, and consequential)

```jsonc
"Claims": { "ProviderNpiClaim": "https://payer.example/npi" }
```

**Only configure this if your IdP authoritatively verifies the NPI** — for
example from your credentialing system or provider directory at token issuance.

An NPI is public information. A claim named `npi` that your IdP copies from a
self-asserted profile field is not identity, and configuring it as such would
convert a public number into an authorization decision.

| Configured and verified | Not configured |
| --- | --- |
| CDex `$submit-attachment` binds the submitter to the request's provider using the **token's** NPI | Corroborating-key behaviour: the payload's NPI must match the request's |
| A caller who is not the requested provider is refused even with a correct payload | A caller who knows the public NPI can submit |

CHO validates the shape (ten digits) and ignores anything else. It never reads a
provider claim that was not configured.

## 7. Key rotation

- Publish new keys with a distinct `kid` **before** signing with them.
- Keep the retiring key in the JWKS until all tokens signed with it have expired.

CHO refreshes on an unknown `kid` (rate-limited to once per 5 minutes per issuer,
single-flighted), routinely every 12 hours, and stops trusting cached keys after
24 hours without a successful refresh.

## 8. Validating an integration

The `SmartOAuthTrustTests` suite is the executable form of this contract. To
validate against a real IdP, configure `SmartAuth:TrustedIssuers` for a
non-production host and confirm:

1. a token from your IdP is accepted;
2. a token for another audience is rejected (401);
3. an expired token is rejected (401);
4. a read-scoped token is refused a write (403);
5. a patient token cannot read another member (403);
6. a rotated signing key is picked up without a restart;
7. `/health/ready` reports `smart-identity-trust` healthy;
8. if provider identity is mapped, a mismatched submitter is refused.

## 9. Worked examples

Configuration only — no product code changes.

**Okta**
```jsonc
{ "Issuer": "https://example.okta.com/oauth2/ausXXXX",
  "Audiences": ["https://api.cloudhealthoffice.com"],
  "AllowedAlgorithms": ["RS256"] }
```

**Microsoft Entra ID**
```jsonc
{ "Issuer": "https://login.microsoftonline.com/<tenant-guid>/v2.0",
  "Audiences": ["api://cloudhealthoffice"],
  "AllowedAlgorithms": ["RS256"],
  "Claims": { "TenantClaim": "tid" } }
```
Entra's discovery `issuer` includes the tenant GUID and must match exactly.

**Auth0**
```jsonc
{ "Issuer": "https://example.auth0.com/",
  "Audiences": ["https://api.cloudhealthoffice.com"] }
```
Note Auth0's trailing slash — the match is exact.

**Keycloak**
```jsonc
{ "Issuer": "https://sso.example.com/realms/cho",
  "Audiences": ["cloudhealthoffice-fhir"] }
```

**Local Keycloak for development** (development hosts only):
```jsonc
{ "Issuer": "http://localhost:8080/realms/cho",
  "Audiences": ["cloudhealthoffice-fhir"],
  "RequireHttpsMetadata": false }
```
`RequireHttpsMetadata: false` fails startup outside development.

## 10. What CHO will not do

- Trust an issuer that is not configured, however valid its signature.
- Fetch keys from a URL a token or an undeclared discovery document names.
- Accept `alg: none` or an HMAC-signed token.
- Accept a token with no audience, or one minted for another API.
- Disable lifetime validation.
- Read a provider NPI from an unmapped claim.
- Take tenant from a FHIR body or query parameter.
- Fall back to demo token validation on a production host.

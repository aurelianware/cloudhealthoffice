# smart-auth-service: who a SMART token's tenant and patient belong to

`src/services/smart-auth-service` issues SMART on FHIR tokens. A token's
`tenant_id`, and a member token's `patient`, now come only from bindings stored
on the server. If a person or client has no binding, no token is issued.
Nothing in the request can choose the tenant or the patient: not the authorization
request, not the token request, not the launch body, and not a header.

fhir-service takes a SMART caller's tenant only from the token. It reads the
issuer's mapped claim, then `tenant_id`, then the one tenant the issuer is
confined to (see `SmartTenant`). Before this change, smart-auth-service tokens
carried no tenant. The Development confinement of the demo issuer to
`demo-tenant` was the only thing that made them usable. That confinement still
works as a fallback, and tokens now carry `tenant_id` themselves.

## Who signs in, before and after

| Caller | Before | Now |
|---|---|---|
| **Member** (Patient Access) | Development login: any username with password `Password123!`, enabled by `SmartAuth:DevMode`, which was `true` in the base `appsettings.json`. A standalone `launch/patient` token used the **username** as `patient`. There was no tenant. | Same Development login, but only on a Development host with `DevMode=true` (the base setting is now `false`). A token is issued only if the identity is bound to a member: `patient` and `tenant_id` come from that binding. **No production member IdP exists in the repo** (member-service, sponsor-service and personal-representative-service have placeholder `b2clogin` authorities; the member/provider portals exist only as issue templates). |
| **Provider user** (Provider Access, EHR launch) | Same Development login. `fhirUser` came from `PractitionerId` in the unauthenticated `POST /launch` body. | Same Development login. A token is issued only if the identity is bound to a provider of one tenant. `sub` and `fhirUser = Practitioner/{providerId}` come from the binding, and so does `npi`. Patient context comes from an EHR launch registered in the same tenant for the same client. |
| **App** (backend, payer-to-payer) | client_credentials for the seeded `cho-payer-system` client. Every environment, including Production, had a hard-coded secret. The token had no tenant. | client_credentials. The token's `tenant_id` is the tenant the client is registered to. Clients are registered by a tenant administrator through the admin API. The demo clients are seeded only on a Development host. |
| CHO staff | Entra ID through the portal and token-service (CHO tokens, not SMART). | Unchanged. Staff use CHO tokens to call the new admin API and `POST /launch`. |

## Bindings (MongoDB, database from `MongoDb:DatabaseName`)

| Collection | Document | Invariant |
|---|---|---|
| `smart_member_links` | `{ tenantId, issuer, subject, memberId, status, enrolmentId, createdBy, createdAt, revokedBy, revokedAt }` | A partial unique index on `(issuer, subject)` where `status = Active`. An identity is bound to exactly one member id and tenant at a time. Revoked links remain as history. |
| `smart_provider_users` | `{ tenantId, issuer, subject, providerId, npi, status, … }` | The same index: one active provider binding per identity. |
| `smart_enrolments` | `{ kind: Member\|Provider, tenantId, memberId \| providerId+npi, codeHash, status, createdBy, expiresAt, redeemedBy }` | Only the SHA-256 of the code is stored. A code is used once and expires after `SmartAuth:EnrolmentCodeTtlHours` (default 72). |
| `smart_client_tenants` | `{ _id: clientId, tenantId, kind, displayName, createdBy, createdAt }` | Each client registration belongs to one tenant. A vendor app that serves two payers is registered by each payer under a different client id. |

An identity is the pair (issuer, subject) of whatever authenticated the person.
The subject alone is never used, because two IdPs can both have an `alice`.

### How an identity gets bound (enrolment and linking)

1. A tenant user with the right permission issues an enrolment code.
   - `POST /api/admin/smart/member-enrolments { memberId }` needs `members:write`.
   - `POST /api/admin/smart/provider-enrolments { providerId, npi }` needs `providers:write`. The NPI check digit is validated.

   The tenant comes from the CHO token, and so does the actor. The response
   shows the code once.
2. The tenant delivers the code to the person over a channel it has already
   verified, such as a mailed letter, an authenticated member portal session,
   or a provider onboarding packet.
3. The person signs in to smart-auth-service and redeems the code at
   `POST /account/link` (form field `code`). The identity bound is the signed-in
   session's (issuer, subject). The request names no tenant, member or provider.

An identity that already has an active binding of that kind gets `409`, and the
code is left unused. The admin API never binds an identity it was only told
about.

## Token issuance (`SmartTokenContextResolver`)

| Request | Binding required | Token claims |
|---|---|---|
| Authorize with `patient/*` or `launch/patient` scopes, no `launch` (member) | An active member link, and the client is a `patient-app` registered to the **same tenant** | `sub` = IdP subject, `tenant_id`, `patient` = memberId, `fhirUser` = `Patient/{memberId}` |
| Authorize with `user/*`, `launch` scope or a `launch` parameter (provider) | An active provider link, and a `provider-app` client of the same tenant. A launch must be registered in the same tenant for the same client. | `sub` = providerId (fhir-service attributes Provider Access by `sub`), `tenant_id`, `fhirUser` = `Practitioner/{providerId}`, `npi`, plus `patient`/`encounter` **only** from the launch |
| Provider asks for patient scopes with no launch | — | Refused. There is no patient picker. |
| `system/*` scopes in an interactive flow | — | Refused |
| client_credentials | A `backend` client registration. Only `system/` scopes are allowed. | `sub` = client_id, `tenant_id` = the registration's tenant |
| Code or refresh exchange | The same binding must still be active and unchanged, and the client must still be registered to the same tenant | Rebuilt from the stored grant |

If the binding is missing, the authorize response is `access_denied` and the
token endpoint returns `invalid_grant` or `unauthorized_client`. The private
claims that record the binding (`cho_ctx`, `cho_idp_iss`, `cho_idp_sub`) are
stored only in the authorization code and refresh token. They never appear in
an access or identity token. Revoking a link or deleting a client stops refresh
straight away. Access tokens that were already issued stay valid until they
expire (default 60 minutes).

`offline_access` is now a registered scope, so interactive clients can be
issued refresh tokens.

### EHR launch (`POST /launch`)

- It needs a CHO token with `members:read`, and the launch belongs to that
  token's tenant. An `X-Tenant-ID` header is accepted only as an echo of the
  token's tenant. If they differ, the request gets `403`.
- `clientId` must be a `provider-app` registered to that tenant.
  `PractitionerId` was removed from the body, because `fhirUser` now comes from
  the provider's binding.
- A launch never sets a member's patient. A member who presents a `launch` is
  refused.

The local `Middleware/TenantMiddleware.cs` was deleted. It read `X-Tenant-ID`
and `X-Dev-Tenant-ID`. Tenant resolution is now the shared CHO
`TenantMiddleware`, which takes the tenant from the token only.

## Admin API (CHO tokens)

smart-auth-service now calls `AddChoAuthentication`, which needs `ChoAuth` to be
configured in every environment. The default scheme is the CHO bearer scheme.
Unannotated actions are denied. The SMART OAuth endpoints (`/connect/*`,
`/account/*`, `/.well-known/*`) are `[AllowAnonymous]` and authenticate
explicitly, as they did before. `/connect/userinfo` keeps OpenIddict validation
through a named policy.

| Endpoint | Permission |
|---|---|
| `POST/GET /api/admin/smart/member-enrolments`, `DELETE …/{id}` | `members:write` |
| `GET /api/admin/smart/member-links`, `DELETE …/{id}` (revoke) | `members:write` |
| `POST/GET /api/admin/smart/provider-enrolments`, `DELETE …/{id}` | `providers:write` |
| `GET /api/admin/smart/provider-users`, `DELETE …/{id}` (revoke) | `providers:write` |
| `POST/GET /api/admin/smart/clients`, `GET/DELETE …/{clientId}` | `settings:manage` |
| `POST /launch` | `members:read` |

- Every read and write is filtered by the CHO token's tenant. Another tenant's
  id returns `404`, exactly like a missing id.
- `createdBy`, `revokedBy` and `cancelledBy` come from the token's `sub`. Body
  fields with those names are ignored.
- A CHO service token gets `403` on the binding and client endpoints. A person
  has to make bindings.
- `POST /clients` takes the fields `kind` (`backend` | `patient-app` |
  `provider-app`), `displayName`, `redirectUris` and `scopes`. It also takes
  `clientId` (optional; generated if absent) and `confidential`. A client
  secret is returned once. Redirect URIs must use HTTPS. Plain-HTTP loopback
  addresses are allowed only on a Development host. Scopes must be registered
  and suitable for the client's kind.
- Each change writes one audit line on log category
  `CloudHealthOffice.SmartAuth.Audit`, recording the action, tenant, actor and
  target. Token issuance, refusals with a reason code, and code redemptions are
  logged on the same category. Codes and tokens are never logged.

## Development-only

- **Development login** (`/account/login`). It works only when the host
  environment is Development *and* `SmartAuth:DevMode=true`. The identity
  issuer is `urn:cho:smart-auth:development-login`, which no production binding
  can refer to by accident.
- **Seeded demo data**, on a Development host only:
  - the demo clients `smart-patient-app` (patient-app), `cho-ehr-app`
    (provider-app) and `cho-payer-system` (backend), with their hard-coded
    secrets, all bound to `demo-tenant`;
  - development login `demo-member` bound to member `pat-001`;
  - development login `demo-provider` bound to `provider-001` with NPI
    `1234567893`.

  Any other development username has no binding until it redeems a code.
- **ChoAuth development issuers** (`cho-portal-dev`, `cho-internal-dev`) in
  `appsettings.Development.json`, with the well-known symmetric key that is
  refused outside Development and Testing.

## What a production member/provider login must supply

A production login is an external OIDC sign-in. After it completes, it must set
the same session that the development login sets. It must:

1. **Validate the ID token in full**: signature from the IdP's published keys,
   `iss` equal to the configured issuer exactly, `aud` equal to
   smart-auth-service's client id, `exp`/`nbf`, and `nonce`. Use the
   authorization code flow with PKCE.
2. **Produce exactly two session claims**:
   - `cho_idp_iss` set to the IdP's `iss`;
   - `ClaimTypes.NameIdentifier` set to an **immutable, non-reassignable**
     subject. For Entra this is `oid` (or `sub`). It must never be an email
     address or a username.
3. **Assert nothing about tenant or member.** smart-auth-service ignores any
   tenant, member or role claim from the IdP. Tenancy comes only from the
   bindings, so the IdP cannot widen access.
4. **Provide assurance suitable for PHI access**: MFA available or required,
   account recovery that is not weaker than the enrolment code's delivery
   channel, and a way to disable an account. Disabling it at the IdP stops new
   tokens, and revoking the binding here stops refresh.
5. **Use one issuer per user population**, or at least issuers whose subjects
   never collide, so that (issuer, subject) is globally unique.

The development login must not be extended for production use. The production
handler goes alongside it, for example
`AddOpenIdConnect("member-idp", …)` with `OnTokenValidated` mapping the two
claims and then signing in to the cookie scheme. Its issuer goes into
configuration. The binding store does not change.

### Recommendation: Microsoft Entra External ID (CIAM)

Use an Entra External ID tenant for **members**. Use it for **provider users**
too, with B2B federation to provider organisations' own IdPs where they have
them.

- **It is already the platform's identity stack.** Staff use Entra ID through
  the portal and token-service, and Key Vault, workload identity and
  Microsoft.Identity.Web are already in use. One vendor, one operations model,
  one audit/BAA relationship.
- **It is the supported successor to Azure AD B2C.** The repo's placeholder
  `b2clogin` authorities and the v4 portal issue templates point at B2C. B2C is
  closed to new customers, so a new build should start on External ID.
- **It provides the assurance list above.** It is standards OIDC with PKCE, it
  has an immutable `oid`, MFA (including passkeys), self-service sign-up and
  recovery, and custom branding per payer. Its sign-in logs feed Sentinel.
- **Multi-payer fit.** One External ID tenant per CHO environment gives a single
  issuer. Payer separation is already enforced by the bindings, so there is no
  need for an IdP tenant per payer. If a payer insists on its own member IdP,
  add it as a second issuer. The (issuer, subject) identity handles that
  without code changes.
- Alternatives considered:
  - **Auth0 / Okta CIC** would work equally well technically, but it adds a
    second identity vendor and BAA.
  - **Self-hosted Keycloak** gives the most control, but CHO would then operate
    an internet-facing PHI-adjacent IdP, including patching and HA.
  - **Login.gov / ID.me** offer identity proofing (IAL2). That could replace the
    mailed enrolment code for self-enrolment, but they are an addition, not the
    primary login.

## fhir-service

No fhir-service code or configuration was changed. With tokens carrying
`tenant_id`:

- fhir-service Development still confines the Demo issuer to `demo-tenant`
  (`SmartAuth:Tenants`). Tokens from demo-tenant bindings are accepted. Tokens
  for any other tenant are refused with `403` by `SmartIssuerTenantMiddleware`
  rather than taking a wrong tenant. That fallback can stay. To try other
  tenants against a local fhir-service, remove the `Tenants` entry or list
  those tenants.
- Production uses `SmartAuth:Mode=ExternalIssuer`. If smart-auth-service is
  used there, list it as a trusted issuer with `Audiences: ["fhir-api"]`,
  optionally `Tenants` (the tenants it serves), and optionally
  `Claims.ProviderNpiClaim: "npi"` so that fhir-service treats the provider's
  NPI as verified.
- smart-auth-service does not call `SetIssuer`. Its `iss` is the request's base
  URL, so the trusted issuer entry must match the public URL that
  smart-auth-service is served at.

## Not done

- **No production login.** See above.
- **The admin API does not check that a member or provider exists.** A member id
  or provider id is accepted if it is a well-formed FHIR id. The administrator
  issuing the code is responsible for it, and a wrong id only binds the person
  to a non-existent record in their own tenant.
- **No patient picker for providers.** Patient context comes only from an EHR
  launch.
- **The launch context store is still in memory**, so it holds for a single pod
  only. That was already the case.
- **No `patient` in the token response body.** SMART's token-response
  `patient` parameter is not emitted. The claim is in the access token.
- **Existing OpenIddict applications outside Development have no tenant
  registration and get no token.** They must be re-registered through the admin
  API. Before this change no environment had any client other than the seeded
  demo ones.

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
| **Member** (Patient Access) | Development login: any username with password `Password123!`, enabled by `SmartAuth:DevMode`, which was `true` in the base `appsettings.json`. A standalone `launch/patient` token used the **username** as `patient`. There was no tenant. | Same Development login, but only on a Development host with `DevMode=true` (the base setting is now `false`). A token is issued only if the identity is bound to a member: `patient` and `tenant_id` come from that binding. **Production:** the external OIDC login (Microsoft Entra External ID, `SmartAuth:ExternalLogin`), disabled until the owner creates the External ID tenant; see "External sign-in" below. |
| **Provider user** (Provider Access, EHR launch) | Same Development login. `fhirUser` came from `PractitionerId` in the unauthenticated `POST /launch` body. | Same Development login; in production the same external login as members. A token is issued only if the identity is bound to a provider of one tenant. `sub` and `fhirUser = Practitioner/{providerId}` come from the binding, and so does `npi`. Patient context comes from an EHR launch registered in the same tenant for the same client. |
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

### Consent (`SmartConsent`)

- Before an interactive app gets an authorization code (and so before any
  refresh token), the signed-in person sees a consent page naming the app
  (its registered display name) and each requested scope, `offline_access`
  spelled out as "keep access when you are not using the app". Allow or Deny.
- The decision posts back to `/connect/authorize` with the original request
  parameters and a `consent_token`: a 10-minute Data Protection-protected proof
  of the exact (identity, client, scope set) the page was shown for. A decision
  without a valid proof for the signed-in identity and the request's client
  and scopes is ignored and the page is shown again, so another site cannot
  post an approval for the person and a proof cannot be reused for wider scopes.
- Allow creates a permanent OpenIddict authorization for (identity `issuer|subject`,
  client, scopes); the page is not shown again for that client while a valid
  authorization covers the requested scopes. Every code and refresh token
  carries that authorization's id, so revoking it (OpenIddict
  `TryRevokeAsync`) ends refresh. Deny returns `access_denied`.
- `prompt=none` without a stored approval returns `consent_required`;
  `prompt=consent` always shows the page.
- Consent runs before the binding checks and before a launch is consumed. A
  `launch` parameter with control characters is refused before the page.
- There is no first-party exemption: client registrations have no first-party
  flag, so every interactive client, the demo ones included, is asked once.
- Every authorization code and refresh token must rest on a valid permanent
  authorization (an approval). One that does not, such as a refresh token
  issued before consent existed (OpenIddict tied it to an ad-hoc
  authorization), is refused at `/connect/token` with `invalid_grant`, so that
  app must send the person through `/connect/authorize` again.
- Withdrawing an approval revokes the authorization and every token issued
  under it (`TryRevokeAsync` + `RevokeByAuthorizationIdAsync`); the app's
  refresh tokens stop working at once and the consent page is shown again.
  - The person: `GET /account/apps` (signed in) lists their approvals (app,
    scopes, date) with a "Withdraw access" form per approval, posting to
    `POST /account/apps/revoke`. Each form carries a 10-minute Data
    Protection proof bound to the signed-in identity and that approval (the
    consent form's pattern), so another site cannot post a revocation and one
    person cannot revoke another's approval.
  - An administrator of the client's tenant: see the admin API table
    (`…/clients/{clientId}/approvals`).
  - Each revocation is an audit line (`SMART consent revoked`, `by=member` or
    `by=admin`).

### Browser security headers

Every response carries `X-Frame-Options: DENY`,
`Content-Security-Policy: frame-ancestors 'none'`,
`X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`, so no
site can frame the login, consent, link or connected-apps pages, and a code or
launch token in a URL never leaves in a Referer. The pages this service renders
get `default-src 'self'; script-src 'none'; style-src 'unsafe-inline';
object-src 'none'; base-uri 'none'; frame-ancestors 'none'`. There is no
`form-action`: the consent form posts to `/connect/authorize`, which redirects
to the client's redirect URI on any origin, and browsers apply `form-action`
to that redirect.

### EHR launch (`POST /launch`)

- It needs a CHO token with `members:read`, and the launch belongs to that
  token's tenant. An `X-Tenant-ID` header is accepted only as an echo of the
  token's tenant. If they differ, the request gets `403`.
- `clientId` must be a `provider-app` registered to that tenant.
- `practitionerId` (required) names the provider id, as bound by a provider
  enrolment, the launch is for. A launch without it is refused with `400`.
  It never sets `fhirUser` or `sub` (those come from the signed-in provider's
  binding); it only restricts who may use the launch: a launch naming a
  practitioner is consumed only by the provider user bound to that provider
  id. Anyone else holding the launch token (another provider of the tenant on
  the same app) is refused and the launch is left in place. The registering
  caller is a CHO user or service, not a SMART provider identity, so the
  practitioner cannot be taken from its token.
- Deprecated escape hatch: `SmartAuth:AllowLaunchWithoutPractitioner=true`
  (default `false`) still accepts a launch without `practitionerId`, with a
  warning in the log for each one. Such a launch can be used by any provider
  user of the tenant on that client who holds the single-use token. Use it
  only while an EHR integration is being changed to send `practitionerId`;
  it will be removed.
- A launch never sets a member's patient. A member who presents a `launch` is
  refused.
- Launches are stored in MongoDB (`smart_launch_contexts`), alongside the
  bindings, so any pod can consume a launch that another pod registered. The
  launch token itself is never stored, only its SHA-256.
- A launch expires after `SmartAuth:LaunchContextTtlMinutes` (default 5). The
  expiry is checked on every use, and a TTL index on `expiresAt` deletes
  expired documents.
- A launch can be used once. It is consumed by a single `findOneAndDelete`
  filtered on the token hash, the provider's tenant, the client, the
  practitioner (when the launch names one) and `expiresAt > now`. However many attempts run at once, only one succeeds.
- A launch presented for another tenant or client is refused and left in place.
  It cannot be used there, and someone who only knows the token cannot burn it.
  The launch is consumed only after every other check has passed (provider
  binding, client kind and tenant).

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
| `GET …/{clientId}/approvals[?subject=]`, `DELETE …/{clientId}/approvals/{approvalId}`, `DELETE …/{clientId}/approvals[?subject=]` (withdraw app approvals of the tenant's own client; `404` for another tenant's) | `settings:manage` |
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

## External sign-in (production members and provider users)

`SmartAuth:ExternalLogin` makes smart-auth-service an OpenID Connect relying
party of one external IdP (Microsoft Entra External ID, see the
recommendation below). It is driven entirely by configuration and is off by
default. The login page shows "Sign in with {DisplayName}" when it is
enabled; the development form stays Development-host + `DevMode` only; with
neither, the page says no sign-in method is configured.

How it meets each requirement of a production login:

1. **The ID token is validated in full.** Authorization code flow with PKCE
   (S256) and a `nonce`; the code is redeemed with the client secret. The
   signature is checked against the IdP's JWKS (from discovery at
   `Authority`), RS256 only; `aud` must equal `ClientId`; `exp`/`nbf` with 2
   minutes skew. `iss` must equal `ExpectedIssuer` **exactly** (ordinal) —
   checked by the token handler and again in `ExternalLogin.MapSession`,
   because the ASP.NET handler also accepts whatever issuer the discovery
   document names.
2. **The session is exactly the development login's shape**
   (`SmartSession`): `cho_idp_iss` = the validated `iss`,
   `ClaimTypes.NameIdentifier` = `oid` (`sub` only if the token has no
   `oid`; never email or username), and `name` for display. Every other IdP
   claim — tenant, roles, email, extension attributes — is dropped, and the
   IdP's tokens are not kept.
3. **The IdP asserts nothing about tenancy.** An identity with no binding gets
   no SMART token; it redeems an enrolment code at `/account/link` exactly
   like a development identity, and the binding records (`ExpectedIssuer`,
   `oid`).
4. **Redirects.** The redirect URI sent to the IdP is
   `{SmartAuth:Issuer}{CallbackPath}` from configuration, never the request's
   Host or `X-Forwarded-*` (the service trusts no forwarding proxy). The
   return URL after sign-in must be local; anything else becomes `/`.
5. **Cookies.** The session cookie and the OIDC correlation and nonce cookies
   are `HttpOnly`, `SameSite=Lax` (the code comes back on a top-level GET,
   `response_mode=query`) and, outside Development, `Secure` even though the
   pod sees plain HTTP behind the TLS-terminating ingress. The Data
   Protection key ring that protects them is shared by all pods through
   MongoDB (`smart_dataprotection_keys`), encrypted with the active encryption
   certificate, so a sign-in that starts on one pod can return to another.
6. **Failures** (bad token, wrong nonce/state, IdP error, cancel) create no
   session and return to `/account/login?error=external`; each sign-in and
   refusal is logged on `CloudHealthOffice.SmartAuth.Audit`.
7. **Logout** (`/account/logout`) clears the local session only. The IdP
   session is not ended, so the next external sign-in may not prompt.

Startup fails when `Enabled` is true and `Authority`, `ClientId`,
`ClientSecret`, `ExpectedIssuer` or `DisplayName` is missing, when
`Authority` or `ExpectedIssuer` is not HTTPS outside Development, or when
`CallbackPath` is not a plain path.

### Configuration keys

| Key | Value | Where |
|---|---|---|
| `SmartAuth:ExternalLogin:Enabled` | `true` | ConfigMap |
| `SmartAuth:ExternalLogin:Authority` | `https://{domain}.ciamlogin.com/{tenantId}/v2.0` | ConfigMap |
| `SmartAuth:ExternalLogin:ExpectedIssuer` | the `issuer` of `{Authority}/.well-known/openid-configuration`, for External ID `https://{tenantId}.ciamlogin.com/{tenantId}/v2.0` | ConfigMap |
| `SmartAuth:ExternalLogin:ClientId` | the app registration's Application (client) ID | ConfigMap |
| `SmartAuth:ExternalLogin:ClientSecret` | the client secret | Key Vault secret `SmartAuth--ExternalLogin--ClientSecret` |
| `SmartAuth:ExternalLogin:DisplayName` | button text, e.g. `Cloud Health Office ID` | ConfigMap |
| `SmartAuth:ExternalLogin:CallbackPath` | `/signin-oidc` (default) | ConfigMap |

Only a client secret is supported as the app credential. A certificate
credential (private_key_jwt) is not implemented.

### Owner checklist: Microsoft Entra External ID

1. **Tenant.** Create an External ID tenant (external configuration) for the
   environment. Note its **tenant ID** and its **`<name>.ciamlogin.com`**
   domain.
2. **User flow.** Create a *sign up and sign in* user flow with **Email with
   password** or **Email one-time passcode**. Collect only **Display Name**
   (it is for display; identity is `oid`).
3. **App registration** (in the External ID tenant): supported account types
   *Accounts in this organizational directory only*; platform **Web**;
   redirect URI **`https://auth.cloudhealthoffice.com/signin-oidc`**
   (`{SmartAuth:Issuer}{CallbackPath}`); no front-channel logout URL; leave
   implicit grant (access and ID tokens) **off**. Add the app to the user
   flow.
4. **Token claims.** Nothing to add: the v2.0 ID token carries `oid`, `sub`,
   `iss`, `aud`, `nonce` and (with `profile`) `name`. Do not rely on optional
   claims; they are ignored.
5. **Credential.** Create a client secret (24 months at most, calendar a
   renewal) and store it in the environment's Key Vault as secret
   **`SmartAuth--ExternalLogin--ClientSecret`**. Grant the smart-auth managed
   identity *Key Vault Secrets User*.
6. **MFA.** Enable MFA for the user flow (email OTP or SMS as second factor)
   or, with Entra ID P1, a Conditional Access policy *require MFA* for the
   app; passkeys where available. Configure self-service password reset; it
   must not be weaker than the enrolment-code delivery.
7. **Branding.** Company branding for the sign-in pages (optional).
8. **Configure smart-auth-service** (table above): ConfigMap values,
   `Enabled: "true"`, restart the deployment. Check that the login page shows
   the button and a test user can sign in, redeem a code and get a token.

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
- smart-auth-service's issuer is fixed by configuration (see "Issuer" below).
  The trusted issuer entry must be that same value.

## Issuer

`SmartAuth:Issuer` is required. Startup fails if it is missing, is not an
absolute http(s) URI, or carries a query, fragment or user info. Outside
Development it must also be HTTPS. smart-auth-service calls `SetIssuer` with
this value. It also rebases OpenIddict's base URI onto the same value
(`SmartIssuer.BaseUriHandler`). The token `iss`, the discovery document and
every endpoint URL in it, including `jwks_uri`, therefore name the configured
issuer. A spoofed `Host` or `X-Forwarded-*` header changes none of them.

The value must be the same everywhere:

| Where | Value |
|---|---|
| smart-auth-service `appsettings.json` and k8s `SmartAuth__Issuer` | `https://auth.cloudhealthoffice.com` (the Ingress host) |
| fhir-service `appsettings.json` `SmartAuth:Issuer` (Demo mode) or `TrustedIssuers[].Issuer` (ExternalIssuer mode) | `https://auth.cloudhealthoffice.com` |
| `docker-compose.yml` (both services) | `http://smart-auth-service:8080` |

`SmartIssuerTests` checks that the smart-auth and fhir-service appsettings
values agree.

## Behind the ingress (TLS termination)

TLS ends at ingress-nginx, so every request reaches the pod as plain HTTP.
OpenIddict refuses non-HTTPS requests to its endpoints, and the session and
sign-in cookies are Secure. The service therefore takes the client's scheme
from `X-Forwarded-Proto`, and **only** that header, **only** from the CIDRs in
`SmartAuth:TrustedProxyNetworks` (`TrustedProxy`):

- Set it to the network the ingress-nginx controller pods connect from: the AKS
  pod CIDR (kubenet), or the node subnet (Azure CNI). An invalid entry fails
  startup.
- With nothing listed, no forwarded header is honoured from anyone (ASP.NET
  would otherwise treat empty proxy lists as "trust every sender"), and every
  `/connect/*` request is refused as plain HTTP.
- `X-Forwarded-Host` and `X-Forwarded-For` are never used: the issuer,
  discovery URLs and the external login's redirect URI come from configuration.

`TrustedProxyTests` keep OpenIddict's HTTPS requirement on (the other suites
turn it off) and show discovery is served only for a trusted
`X-Forwarded-Proto: https`.

## Token signing keys

Outside Development and Testing the OpenIddict server's certificates come from
configuration; startup fails without them. The per-machine development
certificates are used only on Development/Testing hosts with nothing
configured. Every pod must use the same certificates: resource servers check
signatures against the published JWKS, and an authorization code or refresh
token encrypted by one pod is redeemed by another.

| Key | Content |
|---|---|
| `SmartAuth:SigningCertificates:N:Pfx` | base64 PKCS#12 with an RSA (2048+) private key. A Key Vault **certificate** (content type PKCS#12) named `SmartAuth--SigningCertificates--0--Pfx` provides exactly this through the Key Vault configuration provider (`SecretProvider:Provider=AzureKeyVault`). |
| `SmartAuth:SigningCertificates:N:Path` | instead of `Pfx`: a PFX file, e.g. mounted by the Key Vault CSI driver |
| `SmartAuth:SigningCertificates:N:Password` | PFX password, if any (none for Key Vault) |
| `SmartAuth:SigningCertificates:N:Standby` | `true`: published in the JWKS but not used to sign |
| `SmartAuth:EncryptionCertificates:N:…` | the same, for encrypting codes and refresh tokens (and the Data Protection key ring). A standby encryption certificate still decrypts. |

The first entry not in `Standby` is the active one (OpenIddict on its own would
sign with whichever expires last). Every signing certificate is published at
`/.well-known/jwks`. A certificate without a private key, under 2048 bits,
with a key usage that excludes signing/encryption, expired, or not yet valid
(while active) is refused at startup; an expired standby certificate is
skipped. Key Vault configuration is read at startup, so every change below
takes effect on a rollout restart.

**How fhir-service picks up keys.** fhir-service (both `Demo` and
`ExternalIssuer` modes) reads smart-auth's JWKS through discovery
(`SmartSigningKeyRing`): it caches keys for 12 hours, refetches early when a
token has an unknown `kid` (at most once per 5 minutes per issuer), and stops
trusting cached keys 24 hours after a failed refresh. So rotate in three
steps:

1. Create the next certificate in Key Vault as
   `SmartAuth--SigningCertificates--1--Pfx` and set
   `SmartAuth__SigningCertificates__1__Standby=true`. Roll out. Both keys are
   published; the current one still signs.
2. After at least 12 hours (every fhir-service pod has refreshed), set
   `SmartAuth__SigningCertificates__0__Standby=true` and remove the standby
   flag from 1. Roll out. The new certificate signs; tokens signed by the
   old one still validate.
3. After the access-token lifetime (`AccessTokenLifetimeMinutes`, 60), remove
   entry 0 (and its flag). Do not enable Key Vault auto-renewal on these
   certificates: a new version replaces the published key without overlap.

Encryption certificates rotate the same way, without the 12-hour wait. Keep
the old one in standby for the refresh-token lifetime
(`RefreshTokenLifetimeDays`, 7): refresh tokens it encrypted cannot be
redeemed once it is gone. The Data Protection keys are encrypted with the
certificate active when they were created; after the old certificate is
removed, a key it encrypted is skipped (logged as an error) and a new one is
created, so open sign-in sessions (2 hours) end and people sign in again.

## Not done

- **Logout does not end the IdP session** (no `end_session` redirect).
- **Client secret only** for the External ID app registration; no certificate
  credential.
- **One external IdP.** A payer that insists on its own member IdP needs a
  second OIDC scheme (the (issuer, subject) bindings already allow it).
- **The admin API does not check that a member or provider exists.** A member id
  or provider id is accepted if it is a well-formed FHIR id. The administrator
  issuing the code is responsible for it, and a wrong id only binds the person
  to a non-existent record in their own tenant.
- **No patient picker for providers.** Patient context comes only from an EHR
  launch.
- **No `patient` in the token response body.** SMART's token-response
  `patient` parameter is not emitted. The claim is in the access token.
- **Existing OpenIddict applications outside Development have no tenant
  registration and get no token.** They must be re-registered through the admin
  API. Before this change no environment had any client other than the seeded
  demo ones.

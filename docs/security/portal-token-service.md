# Portal token service

`src/services/token-service` (client id `token-service`) issues the CHO user
tokens that the portal sends to backend services. The portal hands it the
signed-in user's Entra ID access token. The service validates that token itself,
works out the user's tenant and roles itself from tenant-service, and returns a
CHO token that lasts 5 minutes.

A compromised portal can therefore only get tokens for users who actually present
a valid Entra token. It cannot ask for "a token for user X".

This document also describes how deployed services trust CHO tokens in general.
Some `appsettings.Development.json` comments written by
`scripts/security/add-dev-auth.py` mention `docs/security/service-authentication.md`,
which was never written. This document replaces it.

## Flow

```
Browser ──OIDC──▶ Entra ID ──▶ Portal (Blazor Server, multi-tenant app)
                                   │ acquires token for api://cfada1ac-…/Cho.Token (on behalf of user)
                                   ▼
                 POST /v1/token/exchange   Authorization: Bearer <Entra access token>
                                   │
                         token-service ──(validate Entra token: signature, iss↔tid, aud, scp, exp)
                                   │
                                   ├──▶ tenant-service /internal/v1/identity/*  (service token, sub=token-service)
                                   │        memberships by tid+oid, tenant status, first-login link
                                   ▼
                 200 { access_token: <CHO token, iss=cho-token-service, 5 min>, roles, permissions, user }
                                   │
Portal ──Bearer <CHO token>──▶ claims-service, member-service, … (shared ChoAuth validation)
```

## Contract

### `POST /v1/token/exchange`

Request: `Authorization: Bearer <Entra access token>`, JSON body
`{ "tenantId": "<CHO tenant id>" }`. `tenantId` is optional.

| Status | Body | When |
|---|---|---|
| 200 | `{ "access_token", "token_type": "Bearer", "expires_in": 300, "tenant_id", "tenant_name", "roles": [], "permissions": [], "user": { "id", "email", "displayName", "firstName", "lastName", "department" } }` | Token issued. `Cache-Control: no-store`. |
| 401 | `{ "error": "invalid_token" }` | Entra token missing, badly signed, expired, wrong issuer for its `tid`, wrong audience, missing the `Cho.Token` scope, or app-only. |
| 403 | `{ "error": "no_access" }` | No active membership in the tenant, user not Active, tenant unknown or not active. |
| 409 | `{ "error": "tenant_required", "tenants": [{ "tenantId", "tenantName", "azureTenantId" }] }` | `tenantId` omitted and the home tenant is ambiguous. |
| 503 | `{ "error": "unavailable" }` | tenant-service or the signing key could not be reached. No token is issued. |
| 400 | `{ "error": "invalid_request" }` | Body is not JSON. (Not part of the original contract; only malformed bodies get it.) |

When `tenantId` is omitted, the service takes the tenants the user may enter
(see `GET /v1/token/tenants`) and picks:

1. the one whose registered Azure tenant id equals the token's `tid`, if exactly one does;
2. otherwise the user's only tenant, if there is exactly one;
3. otherwise 409 (or 403 if there are none).

`user.id` and the CHO token's `sub` are the TenantUser id. For a platform admin
without a TenantUser in the tenant, both are `platform:<tid>:<oid>`.

### `GET /v1/token/tenants`

`Authorization: Bearer <Entra access token>` returns
`[{ "tenantId", "tenantName", "azureTenantId" }]`, the active CHO tenants this
user may enter:

- tenants where the user has an honoured link (rule 1 below) and is Active;
- tenants registered to the user's own directory that hold an Active, unlinked
  TenantUser with the user's address (rule 2). Listing them does not link them.

Platform admins get every active tenant.

### `POST /v1/invitations/redeem`

Request: `Authorization: Bearer <Entra access token>`, validated exactly as for
the exchange (same issuer, audience and `Cho.Token` scope, delegated only), and
JSON body `{ "code": "<invitation code>" }`. See "Invitations" below.

| Status | Body | When |
|---|---|---|
| 200 | Same as `/v1/token/exchange` | Redeemed. The CHO token is for the invited tenant. `Cache-Control: no-store`. A repeat by the identity that already redeemed the code also gets 200. |
| 400 | `{ "error": "not_found" }`, `"expired"`, `"revoked"` or `"invalid_request"` | Unknown or malformed code, expired, revoked, or no code in the body |
| 403 | `{ "error": "email_mismatch", "invitedEmail": "p***@acme.com" }` | The signed-in username is not the invited address. The address is masked |
| 403 | `{ "error": "no_access" }` | Redeemed, but the normal exchange then refused (for example, the tenant is suspended) |
| 409 | `{ "error": "already_redeemed" }` or `"identity_in_use"` | Redeemed by someone else, or this identity is already another user in the tenant |
| 401 | `{ "error": "invalid_token" }` | As for the exchange (app-only tokens, wrong scope or audience, ...) |
| 429 | `{ "error": "rate_limited" }` | More than `TokenService:InvitationRedeemPermitsPerMinute` (10) attempts by this tid+oid in a minute |
| 503 | `{ "error": "unavailable" }` | tenant-service or signing unavailable |

### Other endpoints

- `/health`, `/health/live`, `/health/ready`: anonymous.
- `GET /.well-known/jwks.json`: anonymous. Returns the public verification key
  (RS256 or ES256, `kid` = RFC 7638 thumbprint). It is empty in Development,
  where the key is symmetric.
- Everything else requires a valid Entra token.

## Entra token validation

The service uses Microsoft.Identity.Web (`AddMicrosoftIdentityWebApi`, the
`AzureAd` section, `TenantId: organizations`) and then applies its own rules,
which take precedence:

- **Signature**: Entra's published signing keys (from the authority's metadata).
- **Issuer bound to `tid`**: `iss` must equal
  `https://login.microsoftonline.com/{tid}/v2.0` or `https://sts.windows.net/{tid}/`,
  with `{tid}` replaced by the token's own `tid` claim. A token can't claim one
  directory while being issued by another. The forms can be changed with
  `TokenService:IssuerTemplates` (for example, for sovereign clouds).
- **Audience**: one of `TokenService:Audiences`. The defaults are
  `api://cfada1ac-f251-48ea-9330-39212aa4c862` (v1 tokens) and
  `cfada1ac-f251-48ea-9330-39212aa4c862` (v2 tokens).
- **Scope**: `scp` must contain `TokenService:RequiredScope` (default `Cho.Token`).
- **Delegated only**: a token with `idtyp=app`, or with no `scp`, is refused.
- **Identity**: the user is `tid` + `oid`. Both must be present.
- Lifetime is required and validated, with 2 minutes of clock skew.

## Membership rules

All of these use only the validated Entra token and tenant-service data.

1. **Link by oid and tid.** A TenantUser is the user when its `azureAdObjectId`
   equals the token's `oid` and its `azureAdTenantId` (new field) equals the
   token's `tid`.
   - A *legacy* link has the oid but no recorded tid. These were written before
     the field existed, including by the portal's email backfill. A legacy link
     is honoured only when the token's `tid` is the tenant's registered Azure
     tenant id. The service then records the tid, so both must match from then on.
     A legacy link presented from any other directory is refused.
   - Two honoured records for one identity in one tenant: refused.
2. **First-login linking by email** happens only when all of these hold:
   - the token's `tid` equals the requested tenant's registered Azure tenant id
     (the customer's own directory vouches for the address);
   - the TenantUser has no `azureAdObjectId`;
   - its email equals the token's `preferred_username` (else `upn`, else `email`),
     compared case-insensitively;
   - its status is `Active`. An `Invited` user (see "Invitations") is linked only
     by redeeming its invitation, and `Disabled` or `Locked` users are not linked.
     tenant-service's `entra-link` endpoint enforces the same rule.

   The service then records oid+tid on the TenantUser through a conditional
   update that never replaces an existing link.
   - **Guests from other directories are never linked by email.** They come in
     through an invitation (see "Invitations").
   - **This replaces the portal's current behaviour.** Today `UserContextService`
     looks users up by email in the tenant and backfills the OID onto the matching
     TenantUser for *anyone* who signs in with that address, from any directory.
     A directory controlled by an attacker can mint a token for any address, so
     that backfill lets an attacker take over a TenantUser. Under this service, an
     oid written by that backfill (with no tid) is honoured only from the tenant's
     own directory. The portal's backfill should be removed when the portal moves
     to CHO tokens.
3. **Status.** The TenantUser's `status` must be `Active`. `Disabled`, `Locked`
   and anything else get 403. The tenant must be active:
   - tenant-service `Tenant.status` must be `active` (`pending`, `suspended` and
     `terminated` are closed);
   - the portal's `TenantSubscription.subscriptionStatus` must be `Active` or
     `Trial` (`Expired` and `Cancelled` are closed).

   Both kinds of document live in the same `Tenants` collection. Every status
   field present must allow sign-in, and a document with neither field is closed.
   `trialEndsAt` is not checked. A trial stays open until its status changes.
4. **Roles.** Roles come from the TenantUser and permissions from
   `ChoRolePermissions.Expand`. An Active user with no roles gets a token with
   no roles and no permissions. `PlatformAdmin` (and `cho.service`) on a
   TenantUser are **dropped**, because a tenant administrator controls a
   TenantUser's roles and must not be able to grant cross-tenant power.
5. **Platform admins.** A user is a platform admin only when:
   - the token's `tid` equals `TokenService:PlatformTenantId` (CHO's own directory), **and**
   - the token's `roles` contains the app role `PlatformAdmin` (`TokenService:PlatformAdminAppRole`).

   A platform admin may request any *active* tenant and gets role
   `PlatformAdmin` there, added to their TenantUser's roles if they have one.
   The subject is their TenantUser id if they have one in that tenant,
   otherwise `platform:<tid>:<oid>`. A platform admin whose TenantUser in that
   tenant is not Active is refused. Leaving `PlatformTenantId` empty turns
   platform administration off.

   **How the portal decides today.** `TenantContextService.SwitchTenantAsync`
   treats any Entra principal with role `PlatformAdmin` (or a `permissions`
   claim containing `platform:admin`) as a platform admin, from any directory.
   `MainLayout` shows platform screens to anyone whose TenantUser has the
   `PlatformAdmin` role in any tenant. Both are weaker than the rule here:
   - in a multi-tenant app, each customer directory's administrators can assign
     the app's roles to their own users;
   - tenant administrators can set TenantUser roles.

   The portal should take platform-admin status from the CHO token this
   service issues.
6. **Fail closed.** Any tenant-service error (unreachable, timeout, non-success
   other than an expected 404/409, unreadable body) or signing error gives
   503 `{ "error": "unavailable" }`. A token is never issued on partial data.

Every issuance, refusal, link and outage is logged on the
`CloudHealthOffice.TokenService.Audit` category with outcome, `sub`, `tid`,
`oid`, tenant and a reason code. Tokens and email addresses are never logged.

## tenant-service endpoints

token-service reads tenant-service over HTTP with the `tenant-service` factory
client. These purpose-built endpoints (`Controllers/InternalIdentityController.cs`)
serve it:

| Endpoint | Purpose |
|---|---|
| `GET /internal/v1/identity/memberships?tid=&oid=` | TenantUsers with this oid whose recorded tid is this tid or empty, each with its tenant summary |
| `GET /internal/v1/identity/tenants[?azureTenantId=]` | All tenants, or those registered to one directory (`tenantId`, `tenantName`, `azureTenantId`, `status`, `isActive`) |
| `GET /internal/v1/identity/tenants/{tenantId}` | One tenant summary |
| `POST /internal/v1/identity/tenants/{tenantId}/users/find-by-email` | One user by email (in the body, kept out of URLs and logs) |
| `POST /internal/v1/identity/tenants/{tenantId}/users/{userId}/entra-link` | Records oid+tid on an unlinked user. 409 if already linked to something else |

| `POST /internal/v1/identity/invitations/redeem` | Redeems an invitation code for `{ code, tid, oid, email }` (see "Invitations") |

`TenantUser` gained `azureAdTenantId`. **Only token-service writes a link**,
through `entra-link` and invitation redemption. The create and update user APIs
ignore `azureAdObjectId` and `azureAdTenantId` in the body, and their writes
never touch those fields. An administrator removes a link with
`POST /api/v1/tenants/{t}/users/{id}/unlink` (`users:manage`). The unlink is
written to `CloudHealthOffice.TenantService.Audit` with the removed oid and tid.

**Only the token-service service identity may call these endpoints.**
tenant-service uses `AddChoAuthentication`, and the controller carries
`[RequireServiceClient("token-service")]`: a service token from an issuer with
`AllowServiceRole`, role `cho.service`, and `sub` = `azp` = `token-service`.
User tokens (platform administrators included) and every other service get 403.
token-service sends that service token on every call, naming:

- the tenant being looked up, for per-tenant calls;
- `cho-platform`, for cross-tenant lookups.

tenant-service authorizes these endpoints on the caller's identity, never on
that tenant value: they are the only routes exempt from tenant-service's route
tenant check. tenant-service must trust the `cho-internal` service-token issuer
(with `AllowServiceRole`) that token-service signs with.

token-service attaches this token with its own handler, not the shared
`ChoOutboundTokenHandler`. Inside a request, the shared handler forwards the
caller's bearer token, and here that is the user's Entra token, which must not
travel further.

## Invitations

Tenant administrators bring in people from other Entra directories (guests) by
invitation. Email linking never links a guest (rule 2), and administrators can
no longer type in an oid and tid, so an invitation is the only way a guest gets
a link.

### Data

tenant-service keeps invitations in the `Invitations` collection:

- `id`, `tenantId`, `userId`;
- `email`, and `emailNormalized` (trimmed, lower case);
- `displayName`, `firstName`, `lastName`, `department`, `roles`;
- `codeHash`;
- `status`: `Pending`, `Redeemed` or `Revoked`. `Expired` is a Pending
  invitation past `expiresAt`. It is computed, not stored;
- `createdBy` (token subject), `createdAt`, `expiresAt` (`Invitations:Lifetime`,
  default 7 days);
- `resentAt`, `resentBy`, `sendCount`;
- `redeemedAt`, `redeemedBy { tid, oid }`, `revokedBy`, `revokedAt`.

The code is 32 random bytes, base64url (43 characters). It is returned once, in
the response to create or resend. Only its SHA-256 (`codeHash`) is stored, and
no endpoint returns `codeHash`.

Creating an invitation also creates the TenantUser it will activate. That user
has:

- status **`Invited`** and no link;
- the invitation's roles and names;
- three fields that are never serialized: `invitationId`, `invitationCodeHash`
  and `invitationExpiresAt`.

If an unlinked user with the address already exists and is `Invited` (with no
live code), `Disabled` or `Locked`, it is reused instead. token-service never
issues a token for an `Invited` user (`IsActive` requires `Active`), never links
one by email, and does not list its tenant.

### Administration

These routes need `users:manage`, and the route tenant must be the token tenant.

| Endpoint | |
|---|---|
| `POST /api/v1/tenants/{t}/invitations` | Body `{ email, displayName?, firstName?, lastName?, department?, roles }`. 201 `{ invitation, code, redemptionUrl }` with `Cache-Control: no-store`. See the refusals below |
| `GET /api/v1/tenants/{t}/invitations` | The tenant's invitations, newest first. Never codes or hashes |
| `POST /api/v1/tenants/{t}/invitations/{id}/revoke` | The code stops working at once. The Invited user stays, unable to sign in, until it is re-invited or deleted. 409 if already redeemed |
| `POST /api/v1/tenants/{t}/invitations/{id}/resend` | A new code and a new expiry. The old code stops working at once. 200 `{ invitation, code, redemptionUrl }`. 409 if redeemed or revoked |

Create refuses with:

- 409 `user_exists` when the address belongs to an Active or linked user (or to
  several users) in the tenant;
- 409 `invitation_pending` when the address already has a live invitation
  (resend that one instead);
- 403 for `PlatformAdmin` or `cho.service`, whoever asks. token-service drops
  both from TenantUser roles anyway;
- 400 for unknown roles.

`redemptionUrl` is `{Invitations:PortalBaseUrl}/invite/{code}`. The portal shows
a link on its own origin. Create, revoke and resend are written to the
`CloudHealthOffice.TenantService.Audit` log with the invitation id, user id and
roles, but no code or address.

An administrator cannot:

- activate an `Invited` user by hand (`PUT`/`PATCH` with a status gives 400);
- change an `Invited` user's email, because the invitation is bound to it;
- set any user's link.

**Email delivery.** CHO has no email service that tenant-service can use:

- the portal's `SmtpEmailNotificationService` lives inside the portal and sends
  only sales-inquiry notifications;
- there is no notification service, SendGrid, ACS or `IEmailSender`.

So nothing is emailed. The portal shows the link once, with a copy button and a
note that the administrator must send it.

### Redemption

1. The invited person opens `https://<portal>/invite/{code}`. The page requires
   sign-in, and Entra sign-in returns to the same URL, so the code survives it.
2. The portal acquires the user's `Cho.Token` Entra token, exactly as for the
   exchange, and calls token-service `POST /v1/invitations/redeem { code }`.
3. token-service validates the Entra token and applies the per-identity rate
   limit.
4. token-service calls tenant-service `POST /internal/v1/identity/invitations/redeem`
   over its service token, with the code and the token's `tid`, `oid` and
   username (`preferred_username`, else `upn`, else `email`).
5. tenant-service redeems (see "Atomicity") and answers `{ tenantId, userId }`
   or an error.
6. token-service runs the normal exchange for that tenant, which re-checks the
   user, its status and the tenant, and returns the CHO token.
7. The portal caches the token as the current tenant, saves the tenant
   preference and reloads `/`.

tenant-service's internal answers are:

- 200 `{ tenantId, userId }`;
- 404 `not_found`;
- 410 `expired` or `revoked`;
- 409 `already_redeemed` or `identity_in_use`;
- 403 `email_mismatch`, with `invitedEmail` masked.

token-service maps them to the public statuses above. Any other answer,
including a 403 from tenant-service's own authorization, counts as an outage
(503).

**The signed-in username must equal the invited address** (trimmed,
case-insensitive), so a forwarded link is useless to anyone signed in as someone
else. The error names the invited address masked (`p***@acme.com`: first
character and domain), so the person can switch accounts. The portal shows a
sign-out link.

### Atomicity

Redemption must do three things: link oid+tid, set `Active`, and close the
invitation. It must also lose cleanly against a concurrent redemption, revoke or
resend.

tenant-service does this **without a multi-document transaction**. Transactions
need a replica set (member-service's family relationships, for example, require
one). tenant-service also runs on Cosmos DB for MongoDB
(`CosmosDb:ConnectionString`), where transactions are limited. Instead:

- **The TenantUser decides.** While a user is `Invited` with a live code, it
  carries `invitationId`, `invitationCodeHash` and `invitationExpiresAt`.
  Redemption is **one conditional update** of that user. It matches only if the
  user has this `_id` and tenant, is `Invited` and unlinked, and carries this
  `invitationId`, this code hash, an unexpired code and this email. The same
  write sets oid, tid and `Active` and removes the code hash. Two concurrent
  redemptions cannot both match.
- **Revoke and resend change the user first.** Revoke removes the code hash and
  resend replaces it. Each is conditional on the user still being `Invited`,
  unlinked and on this invitation. So each of them and a redemption serialize on
  that one document. Whichever lands first wins; the other matches nothing and
  reports `already_redeemed` or `revoked`.
- **The Invitation document is the record.** It is updated second (`Pending` →
  `Redeemed` or `Revoked`, conditional on `Pending`). If that write is lost, the
  user is already in its final state, and any later redeem, revoke or resend sees
  the user and completes the record. No order of failures can leave a user
  linked through a revoked or superseded code, or an invitation marked redeemed
  without a linked user.
- **Identity in use.** Before linking, redemption refuses (`identity_in_use`)
  if this tid+oid is already linked to another user in the tenant. Two
  concurrent redemptions of two invitations by one identity could both pass that
  check, so after linking it counts again and undoes its own link if there are
  two.
- **Admin edits cannot undo a redemption.** User updates are field updates that
  never write the link or invitation fields. They are refused (409) if the stored
  user crossed the `Invited` boundary since it was read.

### Threat model (invitations)

- **Code theft** (from the administrator's channel, browser history, or someone
  looking over a shoulder). A stolen code is limited:
  - it works only for a user signed in as the invited address;
  - it works once, for 7 days, and only until it is revoked or resent.

  A stolen code combined with control of an Entra directory that can mint a
  token for the invited address does get in, because any directory can claim
  any `preferred_username`. That is the residual risk the owner accepted. The
  code is 256 bits and travels to the services only in request bodies. It is
  never logged and is stored only as a hash, so someone who reads the database
  or the logs cannot redeem it. Revoke or resend if a link may have leaked.
- **Forwarded links** are refused with `email_mismatch` unless the recipient
  can sign in as the invited address. The masked address tells the invitee
  which account to use without revealing it in full.
- **Replay.** A redeemed code answers `already_redeemed` to every other
  identity. The same identity gets its token again, which grants nothing new,
  because the exchange still requires an Active, linked user. Revoked, expired
  and superseded codes are refused.
- **Enumeration.** Codes are 256-bit random, so guessing is infeasible. A wrong
  code answers `not_found` and reveals nothing about any invitation. Attempts
  are limited to 10 per minute per Entra identity, and every attempt is
  audited.
- **Tenant administrators** can invite anyone into their own tenant with tenant
  roles (never `PlatformAdmin` or `cho.service`), and can unlink and disable
  users. They can no longer assert an Entra identity for a user.
- **Logging.**
  - token-service audits every attempt on `CloudHealthOffice.TokenService.Audit`
    (outcome, `tid`, `oid`, tenant, reason), without codes or emails.
  - The portal holds the framework categories that log request URLs at Warning,
    so `/invite/{code}` is not written to its logs. They are
    `Microsoft.AspNetCore.Hosting.Diagnostics`, the Blazor navigation manager,
    the OIDC handler and Microsoft.Identity.Web's account controller.
  - The page sets a `no-referrer` referrer policy, so the path is not sent to
    the font and CSS hosts it loads from.
  - The portal sends no request paths to telemetry. It has no Application
    Insights or OpenTelemetry, and the unused Plausible component covers only
    public marketing pages.

### Existing links

Links that administrators set through `azureAdObjectId`/`azureAdTenantId` on the
user APIs before this change look the same as token-service links, so they are
still honoured. To review them:

1. List the tenant's users (`GET /api/v1/tenants/{t}/users` returns
   `azureAdTenantId`) and find links whose directory is not the tenant's own.
2. Unlink and disable any that cannot be vouched for.
3. If the person still needs access, invite them. An unlinked Disabled user is
   reused by the invitation; an unlinked Active user must be disabled first
   (`user_exists`).

Legacy oid-only links remain limited to the tenant's own directory (rule 1).

## Signing and key rotation

| Environment | Key source | Issuer |
|---|---|---|
| Production | `TokenSigning:KeyVaultKeyId`. Required; PEM and symmetric keys are refused at startup | `cho-token-service` |
| Staging and other non-production | Key Vault, or `TokenSigning:PrivateKeyPem` (RSA or EC P-256) | `cho-token-service` |
| Development / Testing | Above, or `TokenSigning:SymmetricKey` (the shared development key) | `cho-portal-dev` in `appsettings.Development.json`, so local services that already trust the development user issuer accept these tokens unchanged |

**Key Vault signing.** The service reads the key's public half once, pins its
version and signs every token with Key Vault's `sign` operation (RS256 for RSA
keys, ES256 for EC P-256). The private key never leaves Key Vault. It
authenticates with `DefaultAzureCredential`, which means workload identity in
AKS. Set `TokenSigning:ManagedIdentityClientId` for a user-assigned identity.
The identity needs **Key Vault Crypto User** on the key (`get` + `sign`).

```bash
az keyvault key create --vault-name <vault> -n cho-token-signing --kty EC --curve P-256 --ops sign verify
az keyvault key show   --vault-name <vault> -n cho-token-signing --query key.kid -o tsv   # versioned id
az keyvault key download --vault-name <vault> -n cho-token-signing --version <v> -e PEM -f cho-token-service.pub.pem
```

Use a **versioned** `KeyVaultKeyId` and do not enable auto-rotation on this
key. Services pin the public key, so a version change must be coordinated.

**Rotation.** The shared `ChoAuth` layer accepts one key per issuer name.
Rotate by moving to a new issuer name:

1. Create a new key version (or a new key) and download its public PEM.
2. Add a second issuer entry to every service, for example
   `{ "Issuer": "cho-token-service-2", "PublicKeyPem": "<new>" }`, keeping the old entry.
3. Point token-service at the new version with `TokenSigning:Issuer=cho-token-service-2`.
4. After the token lifetime plus clock skew (about 7 minutes), remove the old issuer entry.

To rotate under one issuer name, the shared layer would need to accept several
`PublicKeyPem`s per issuer, or an HTTPS JWKS authority. That is a follow-up.

## How services trust the issuer

Every CHO service validates tokens through `AddChoAuthentication` and the
`ChoAuth` section. For deployed environments:

```json
"ChoAuth": {
  "Audience": "cho-api",
  "Issuers": [
    { "Issuer": "cho-token-service", "PublicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----" },
    { "Issuer": "cho-internal",      "PublicKeyPem": "-----BEGIN PUBLIC KEY-----\n...", "AllowServiceRole": true }
  ],
  "ServiceToken": { "Issuer": "cho-internal", "ClientId": "<service-client-id>", "PrivateKeyPem": "<from Key Vault>" }
}
```

Set the values through Key Vault references or environment variables
(`ChoAuth__Issuers__0__Issuer=cho-token-service`,
`ChoAuth__Issuers__0__PublicKeyPem=...`). Never commit them.

- `cho-token-service` must **not** set `AllowServiceRole`. Even then, the shared
  layer ignores `cho.service` from a user issuer, and token-service never writes it.
- token-service also issues Argo workflow tokens under the issuer `cho-workload`
  (same key, `"AllowWorkloadIdentity": true`). See
  `docs/security/argo-service-tokens.md`.
- The PEM is the output of `az keyvault key download … -e PEM` above. It is the
  same key published at `GET /.well-known/jwks.json`.
- Symmetric keys are refused outside Development and Testing, both here and in
  token-service.

## Entra app registration changes (owner action)

On the CHO API app registration (`api://cfada1ac-f251-48ea-9330-39212aa4c862`):

1. **Expose an API → add scope `Cho.Token`.** Admin and user consent, described
   as "Sign in to Cloud Health Office services". Grant the portal's client
   application (`b8975dfe-…` in production, `54f3419d-…` in development) the
   delegated permission. Customer tenants consent when they onboard.
2. **App roles → add `PlatformAdmin`** (allowed member types: Users/Groups), if
   it isn't defined already. Assign it **only in CHO's own directory**. Customer
   directories can assign it too, which is why token-service also requires
   `tid == TokenService:PlatformTenantId`.
3. Optional: set `requestedAccessTokenVersion: 2` in the manifest so `aud` is the
   client id. Both forms are accepted by default.

The portal then requests
`api://cfada1ac-f251-48ea-9330-39212aa4c862/Cho.Token` for the signed-in user
and calls `POST /v1/token/exchange`.

## Deployment configuration

| Setting | Value |
|---|---|
| `AzureAd:Instance` / `AzureAd:TenantId` / `AzureAd:ClientId` | `https://login.microsoftonline.com/` / `organizations` / `cfada1ac-f251-48ea-9330-39212aa4c862` (defaults in `appsettings.json`) |
| `TokenService:Audiences` | App ID URI and client id (defaults) |
| `TokenService:RequiredScope` | `Cho.Token` |
| `TokenService:PlatformTenantId` | CHO's own Entra directory id (**required for platform admins**) |
| `TokenService:TokenLifetime` | `00:05:00` (maximum 1 hour) |
| `TokenService:InvitationRedeemPermitsPerMinute` | `10` redemption attempts per Entra identity per minute |
| tenant-service `Invitations:Lifetime` / `Invitations:PortalBaseUrl` | `7.00:00:00` (1 hour to 30 days) / `https://portal.cloudhealthoffice.com` (for `redemptionUrl`) |
| `TokenSigning:KeyVaultKeyId` | Versioned key id (**required in Production**) |
| `TokenSigning:Issuer` / `Audience` | `cho-token-service` / `cho-api` |
| `ChoAuth:ServiceToken:PrivateKeyPem` | The `cho-internal` service-token key, from Key Vault (secret `token-service-secrets`) |
| `Services:TenantService` | `http://tenant-service` |

The k8s manifest is `src/services/token-service/k8s/token-service-deployment.yaml`.
It uses a workload-identity service account. docker-compose runs the service on
port 5030 in the `core` profile.

## Threat model

**A compromised portal can:**

- exchange the Entra tokens of users who are signed in to it, getting their CHO
  tokens. These last 5 minutes and carry exactly the users' own roles in tenants
  they belong to;
- choose *which* of a user's tenants to request;
- replay a stolen Entra token until it expires, around an hour; Entra's lifetime
  applies;
- hold on to issued CHO tokens until they expire (5 minutes).

**A compromised portal cannot:**

- get a token for a user who hasn't presented a valid Entra token. The portal's
  own client credentials can't produce one: app-only tokens are refused;
- name the user, tenant membership, roles or permissions. All are resolved here;
- reach a tenant the user isn't an Active member of, or an inactive tenant;
- become a platform admin. That needs CHO's directory plus the app role in the
  Entra token;
- take over a TenantUser by email from another directory, since guest email
  linking is refused;
- mint CHO tokens itself. It holds no signing key, and in production the key
  is not even in token-service's memory.

**Other attackers:**

- *An attacker-controlled Entra directory* can mint tokens for any address.
  Email linking is limited to the tenant's own directory, legacy links are
  limited the same way, and the issuer must match `tid`.
- *A customer directory admin* can grant themselves the `PlatformAdmin` app
  role, but it is ignored outside `PlatformTenantId`.
- *A tenant administrator* can set any TenantUser role, but `PlatformAdmin` is
  dropped.
- *A tenant administrator* cannot assert an Entra identity for a user. Only
  token-service links one: by email from the tenant's own directory, or by
  invitation redemption.
- *An attacker who reaches tenant-service* needs the token-service service
  identity to read or link identities. Any other caller, including other CHO
  services and platform administrators, is refused.
- *Key Vault or tenant-service outages* give 503. Nothing is issued on partial data.

## Gaps and follow-ups

- **Invitation email.** Invitations are not emailed, because no email service is
  available to tenant-service. Administrators send the link themselves.
- **Admin-set links made before invitations** are still honoured. See
  "Invitations → Existing links".
- The portal's `UserContextService` email backfill, and its role and
  platform-admin decisions, should be removed once the portal uses these tokens.
- The shared layer accepts one public key per issuer (see "Rotation").
- `trialEndsAt` isn't enforced. The tenant document's status fields are
  authoritative.

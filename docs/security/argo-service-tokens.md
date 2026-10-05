# Argo workflows: CHO workload tokens

Status: **implemented** (option B of the original proposal, owner-approved).

Every CHO service requires a CHO token (see
`docs/security/service-auth-rollout-playbook.md`). Argo workflow steps are not
CHO services: they call CHO services with `curl`. Each workflow that does so now
runs under its own Kubernetes service account and exchanges that account's
projected token at token-service for a short-lived CHO **workload token**. The
token carries exactly the permissions registered for that workflow, and nothing
else.

```
workflow pod (SA wf-837-ingest)
  │  projected SA token: iss = cluster OIDC issuer, aud = cho-token-service, 10 min, pod-bound
  ▼
POST http://token-service.cloudhealthoffice/v1/token/workload   { "tenantId": "acme" }
  │  token-service: validate SA token (signature via cluster JWKS, iss, aud, exp, pod binding)
  │                 registration for cho-workflows/wf-837-ingest → client wf-837-ingest,
  │                 tenants [acme], permissions [claims:work]
  ▼
200 { access_token (iss cho-workload, 5 min), expires_in, tenant_id, client_id, permissions }
  │
curl -H "Authorization: Bearer …" -H "X-Tenant-ID: acme" http://claims-service…/api/claims
```

## Token shape

| Claim | Value |
|---|---|
| `iss` | `cho-workload` (`WorkloadTokens:Issuer`). Signed with token-service's `TokenSigning` key (Key Vault in production), the same key as `cho-token-service` user tokens. |
| `aud` | `cho-api` |
| `sub`, `azp` | the registered client id, e.g. `wf-837-ingest` |
| `tenant_id` | the requested tenant (must be in the registration's allowed tenants) |
| `roles` | `["cho.workload"]`. The role grants nothing by itself |
| `permissions` | exactly the registration's permission list |
| `iat`, `nbf`, `exp`, `jti` | 5-minute lifetime (`WorkloadTokens:TokenLifetime`, at most 15 minutes) |

## How the permissions are narrowed

A service token (role `cho.service` from an issuer with `AllowServiceRole`)
passes every non-platform permission check. Workload tokens are **not service
tokens**:

- They carry role `cho.workload`, which is not in `ChoRolePermissions`, and an
  explicit `permissions` claim.
- `ChoPrincipal.HasPermission` treats a workload identity (role `cho.workload`
  from an issuer with `AllowWorkloadIdentity`) as holding **only** its explicit
  permissions: never a role expansion, never a wildcard match into
  `platform:*`, and never a `platform:*` permission even if one were listed.
- `cho-workload` is not configured with `AllowServiceRole`, so a `cho.service`
  role in a workload token is ignored.
- token-service refuses to start with a registration that lists a wildcard
  (`claims:*`, `*:read`), a `platform:*` permission, a client id not starting
  with `wf-`, an empty permission list, or the reserved tenant `cho-platform`.
  `ChoTokenIssuer.IssueWorkloadToken` refuses the same.

A permission list must also cover what the called service does **on the
workflow's behalf**. Inside a request, the shared `ChoOutboundTokenHandler`
forwards the caller's token to the services it calls, exactly as for a user.
enrollment-import-service, for example, calls member-, coverage-, sponsor- and
benefit-plan-service with the workflow's token while it imports a file, so
`wf-enrollment-import` lists those permissions too.

## `RequireServiceClient` and the workload marker

`[RequireServiceClient("x")]` admits a caller whose `sub` = `azp` = `x` and that
is a recognised client. Recognition comes from **issuer configuration only**:

- Authentication (`AddChoAuthentication`, `OnTokenValidated`) first **removes**
  any `cho_service_issuer` / `cho_workload_issuer` claims that arrived inside the
  token, then adds `cho_service_issuer` if the issuer has `AllowServiceRole` and
  `cho_workload_issuer` if it has `AllowWorkloadIdentity`. (Before this change a
  token that wrote `"cho_service_issuer": "true"` into its own payload was
  accepted as a service by any issuer, including the user-token issuer. That is
  fixed here and covered by tests.)
- A **workload** is role `cho.workload` + the workload marker. A **service** is
  role `cho.service` + the service marker.
- Client ids are split by prefix: a workload's client id starts with `wf-`, a
  service's never does. `ChoPrincipal.ServiceClientId` returns nothing for a
  service token naming a `wf-` client or a workload token naming any other
  client. Every service holds the shared `cho-internal` key, so without this
  split any service could mint `sub = wf-tenant-onboarding`. With it, only
  token-service (the only holder of the `cho-workload` key) can produce a
  `wf-` identity, and only for the Kubernetes service account registered for it.
- An issuer may not set both `AllowServiceRole` and `AllowWorkloadIdentity`
  (startup error). `Kind` (`User` / `Service` / `Workload`) is the explicit
  form; it may not contradict either flag, and an issuer name may appear only
  once.
- One issuer, one kind of actor: a `cho-workload` token without
  `cho.workload`, or a `cho-internal` token without `cho.service`, is rejected
  at authentication (401). Neither can be read as a user token.

Why a dedicated issuer rather than `cho-internal`: the `cho-internal` private key
is held by every service. A marker set for `cho-internal` tokens would be
forgeable by every service; a marker set only for `cho-workload`, whose key only
token-service holds (inside Key Vault in production), is not.

## Kubernetes token validation: OIDC discovery / JWKS

token-service validates the projected token itself, with the cluster's
service-account issuer keys, rather than calling the TokenReview API:

- It works with AKS workload identity's public OIDC issuer
  (`oidcIssuerProfile.issuerUrl`) and needs no Kubernetes API access or RBAC
  (`system:auth-delegator`) for token-service, and no extra round trip to the
  API server per exchange.
- It is configurable: `WorkloadTokens:KubernetesIssuer` (exact `iss`),
  `MetadataAddress` (defaults to `{issuer}/.well-known/openid-configuration`),
  or a static `JwksJson` / `JwksFile` (`kubectl get --raw /openid/v1/jwks`) for a
  cluster whose discovery endpoint token-service cannot reach. Keys are cached
  and refreshed once when an unknown `kid` arrives (key rotation).
- It is testable offline: the tests sign projected-token-shaped JWTs with a
  local key and configure its JWKS.

Checks: RS256/ES256 signature, `iss` exactly, `aud` = `cho-token-service`,
`exp` (30 s skew), `iat` present, `exp − iat` ≤ `MaxSourceTokenLifetime`
(1 hour), `sub` = `system:serviceaccount:<ns>:<name>`, and a `kubernetes.io`
claim whose namespace and service account agree with `sub` and that is bound to
a pod. Legacy secret-based tokens (no audience, other issuer, no pod binding)
are refused.

Trade-off: a deleted pod's token stays usable until it expires (at most the
10-minute projected lifetime, then 5 minutes for the issued token). TokenReview
would catch the deletion; it would also make token-service depend on the API
server. The projected lifetime is the bound.

## `POST /v1/token/workload`

Request: `Authorization: Bearer <projected service-account token>`, body
`{ "tenantId": "<tenant>" }`. The `X-Tenant-ID` header is ignored.

| Status | Body | When |
|---|---|---|
| 200 | `{ "access_token", "token_type": "Bearer", "expires_in": 300, "tenant_id", "client_id", "permissions": [] }` | Issued. `Cache-Control: no-store` |
| 401 | `{ "error": "invalid_token" }` | Missing, malformed, badly signed, wrong issuer or audience, expired, too long-lived, not pod-bound, not a service account |
| 403 | `{ "error": "unknown_workload" }` | No registration for this namespace/service account |
| 400 | `{ "error": "invalid_request" }` | No `tenantId` or unreadable body |
| 403 | `{ "error": "tenant_not_allowed" }` | Tenant not in the registration's list, malformed, or `cho-platform` |
| 503 | `{ "error": "unavailable" }` | Cluster keys or the signing key unavailable. Nothing issued |

The endpoint is mapped only when `WorkloadTokens:Enabled` is true. It is not
behind Entra authentication (Entra validation is skipped for this path).

**Audit.** Every issuance and refusal is one line on
`CloudHealthOffice.TokenService.Audit`:
`CHO workload token issued: client=… sa=<ns>/<sa> pod=… tenant=… permissions=…`
or `… refused: … reason=<code>`. Tokens are never logged.

## Registrations and permissions per workflow

Registrations live in configuration, never in a request:
`WorkloadTokens:Registrations` (appsettings / environment / Key Vault
configuration) and/or `WorkloadTokens:RegistrationsFile`, a JSON array read at
startup. The shipped manifest mounts ConfigMap `token-service-workloads`
(`src/services/token-service/k8s/token-service-deployment.yaml`).

| Workflow | Service account = client id | Allowed tenants | Permissions | Why |
|---|---|---|---|---|
| `x12-834-enrollment-import` | `wf-enrollment-import` | tenants with an 834 feed | enrollment:process, enrollment:read, members:read, members:write, coverage:write, benefits:read | `POST /api/v1/enrollment/import`; the import calls member-service (read, create, update, terminate), coverage-service (create), sponsor-service and benefit-plan-service `plan-code-mappings/resolve` with the forwarded token |
| `x12-837-ingest` (`publish-claims.sh`) | `wf-837-ingest` | tenants with an 837 feed | claims:work | `POST /api/claims` |
| `tenant-onboarding` | `wf-tenant-onboarding` | `*` (it onboards new tenants; every issuance audited) | settings:manage | `POST /api/v1/benefitplans`; `GET /api/v1/tenants/{id}` needs only an authenticated caller of that tenant; activation uses its own route (below) |
| `x12-277-rfai` | `wf-277-rfai` | tenants with RFAI enabled | claims:read | `GET /api/claims/number/{claimNumber}` |
| `x12-278-ingest`, `x12-278-replay` | `wf-278-ingest` | tenants with 278 intake | authorizations:write | Only used when `claims-backend-api-endpoint` is set (see below) |

Not registered (no live CHO call): `x12-270-ingest`,
`x12-271-eligibility-response` (would be `wf-270-271`: eligibility:check,
trading-partners:read), `x12-276-ingest`, `x12-277-claim-status`
(`wf-276-277`: claims:read, trading-partners:read). Their calls are still
commented out or mocked. To enable one: add the service account to
`cho-workload-identity.yaml`, a registration, the two volumes and mounts, and
call through `cho_curl`.

## tenant-onboarding activation

Activating a tenant needs `platform:tenants`, which no service or workload token
has. tenant-service has one route for exactly the workflow's step:

`POST /internal/v1/tenants/{tenantId}/onboarding-complete`
(`Controllers/InternalOnboardingController.cs`)

- `[RequireServiceClient("wf-tenant-onboarding")]`: only that workload identity.
  User tokens (platform administrators included), service tokens (including one
  that names `wf-tenant-onboarding`) and other workloads get 403.
- The route tenant check applies: the path tenant must be the token's tenant, so
  a token issued for tenant A cannot activate tenant B.
- Only `pending` → `active`, in one conditional update
  (`TryActivatePendingAsync`). Already `active`: 200, unchanged. `suspended`,
  `terminated` or anything else: 409 `tenant_not_pending`; a suspension that
  lands between the read and the write wins. Unknown tenant: 404.
- Every call is written to `CloudHealthOffice.TenantService.Audit` with the
  actor (`wf-tenant-onboarding`), tenant and outcome.

The workflow's `complete-onboarding` step replaces the old
`PUT /api/v1/tenants/{id}` with `{"status": ...}`.

## Workflows

All in `infrastructure/argo-workflows/`.

- `cho-workload-identity.yaml` (new): the five service accounts, an executor
  Role (`workflowtaskresults`, pod get/watch/patch) bound to them, a ClusterRole
  letting `wf-tenant-onboarding` create namespaces (its resource step), and
  ConfigMap `cho-workload-lib` with `cho-workload.sh`, the shared helper.
- `cho-workload.sh`: `cho_curl TENANT METHOD URL [curl args]` obtains (and reuses
  until a minute before expiry) a workload token for the tenant and calls the URL
  with `Authorization: Bearer` and `X-Tenant-ID`. Token and headers stay in a
  `0700` temporary directory and reach curl through `-H @file`, never a command
  line or an Argo output parameter. It refuses to send a token to anything but an
  in-cluster `http://<svc>[.<ns>[.svc[.cluster.local]]]` URL and refuses
  malformed tenant ids.
- Each calling workflow declares `serviceAccountName`, a projected token volume
  (`audience: cho-token-service`, `expirationSeconds: 600`) and the helper
  volume, and mounts both into the calling steps only.

| Workflow | Change |
|---|---|
| `x12-834-enrollment-import` | SA `wf-enrollment-import`; `import-to-cosmos` uses `cho_curl`, checks the HTTP status, creates `/work/results` |
| `x12-837-ingest` | SA `wf-837-ingest` (was `argo-workflow-sa`); `create-claims-batch` mounts the helper; `containers/claims-publisher/publish-claims.sh` sources it and posts with `cho_curl` (fails if it is not mounted) |
| `tenant-onboarding` | SA `wf-tenant-onboarding`; `validate-tenant-exists` and `seed-benefit-plans` use `cho_curl`; `update-tenant-status` replaced by `complete-onboarding`; the duplicate `templates:` key that silently replaced every template with `cleanup-on-failure` fixed (`onExit` moved to the spec) |
| `x12-277-rfai` | SA `wf-277-rfai`; new `tenant-id` and `claims-service-url` parameters (tenant: the parameter, else `tenantId` in the RFAI request); `fetch-claim-template` calls claims-service `GET /api/claims/number/{n}`; secret `claims-backend-api-secret` and configmap `backend-config` no longer used; exit-handler templates moved into `templates` (the file did not parse) |
| `x12-278-ingest`, `x12-278-replay` | SA `wf-278-ingest`; new `tenant-id` parameter; static `CLAIMS_BACKEND_API_TOKEN` removed; `claims-backend-api-endpoint` now defaults to empty and the call is skipped (no CHO service accepts this payload; the old default was an external placeholder). When set, it must be an in-cluster CHO URL and is called with the workload token; exit-handler templates moved into `templates` (the files did not parse) |
| `test-workflow` | probes `/health/live` (no token needed) instead of `/` |
| `x12-837-claims-scrubbing` | no CHO calls; only its exit-handler templates moved into `templates` (the file did not parse) |

No workflow carries a static CHO token, the `claims-backend-api-secret` token, or
an Entra token any more.

### Retired

Deleted, as recommended (the services already do this work):

- `sla-deadline-watchdog.yaml`: authorization-service's `SlaWatchdogService`
  scans every tenant itself. It sent an Entra workload-identity token, which CHO
  services do not accept. Nothing else referenced it.
- `claims-adjudication-workflow.yaml`, `claims-adjudication-template.yaml`:
  claims-service adjudicates on submission (`ClaimAdjudicationOrchestrator`).
  Referenced by, and therefore also deleted:
  `infrastructure/argo-events/claims-event-trigger.yaml` (webhook EventSource,
  Sensor and Service submitting `claims-adjudication-template`) and
  `infrastructure/argo-events/claims-adjudication-eventsource.yaml` (Kafka
  `claims-adjudication` EventSource and Sensor). Remaining non-executable
  references: `src/engines/cho-enrollment-wiring/argo/validate-provider-step.yaml`
  (a patch snippet for the old workflow, now marked retired),
  `infrastructure/monitoring/grafana-claims-dashboard.json` (panels on
  `claims-adjudication.*` workflow metrics, now empty), comments in
  benefit-plan-service's `AdjudicationController`/`NcciController`, and
  `docs/guides/DEPLOYMENT.md` / `docs/deployment/DEPLOYMENT.md` (apply
  instructions). `x12-837-ingest` still publishes to the `claims-adjudication`
  Kafka topic, which now has no Argo consumer.

## Deployment configuration

1. **Cluster issuer.** Enable the AKS OIDC issuer
   (`az aks update --enable-oidc-issuer`) and take
   `az aks show --query oidcIssuerProfile.issuerUrl -o tsv`. Set
   `WorkloadTokens__KubernetesIssuer` to it exactly (trailing slash included).
   token-service needs HTTPS egress to that URL (discovery and JWKS). Without
   egress, set `WorkloadTokens__JwksFile` to a mounted copy of
   `kubectl get --raw /openid/v1/jwks` and update it when the cluster rotates
   its service-account key.
2. **token-service** (`k8s/token-service-deployment.yaml`):
   `WorkloadTokens__Enabled=true`, `KubernetesIssuer`, `Audience=cho-token-service`,
   `Issuer=cho-workload`, `TokenLifetime=00:05:00`,
   `RegistrationsFile=/etc/cho/workloads/registrations.json` from ConfigMap
   `token-service-workloads`. Replace the `<TENANT_…>` placeholders; token-service
   will not start with them. To keep tenant lists out of a ConfigMap, mount the
   same JSON from a Key Vault secret (Secrets Store CSI driver) at that path, or
   supply `WorkloadTokens__Registrations__N__…` from Key Vault-backed
   environment variables. Signing uses the existing `TokenSigning__KeyVaultKeyId`.
3. **Every CHO service** that a workflow calls (and, simplest, every service)
   adds a trusted issuer:
   ```json
   { "Issuer": "cho-workload", "PublicKeyPem": "<token-service public key, same as cho-token-service>", "Kind": "Workload" }
   ```
   e.g. `ChoAuth__Issuers__2__Issuer=cho-workload`,
   `ChoAuth__Issuers__2__PublicKeyPem=…`, `ChoAuth__Issuers__2__Kind=Workload`.
   Never set `AllowServiceRole` on it. When the token-service key rotates
   (`docs/security/portal-token-service.md`, "Rotation"), rotate `cho-workload`
   the same way (for example `cho-workload-2` alongside `cho-token-service-2`).
   Development: `ChoDevelopmentAuth` and tenant-service's
   `appsettings.Development.json` trust `cho-workload-dev` with the development
   key; other services' development settings need the same entry to accept
   development workload tokens.
4. **Argo.** Apply `infrastructure/argo-workflows/cho-workload-identity.yaml`
   before the workflows. The workflows run in `cho-workflows`; the registrations
   are keyed on that namespace. Apply `infrastructure/argo-events/sensor-rbac.yaml`
   before the sensors: they run in `cloudhealthoffice` and submit into
   `cho-workflows`.

## Limits and follow-ups

- `x12-834-enrollment-import` still has a single `tenant-id` parameter; a
  per-tenant fan-out is still needed for several 834 feeds.
- Fixed: the Argo Events sensors (`rfai-sensor`, `sftp-sensor`) stay in
  `cloudhealthoffice` with their EventSources but now submit their Workflows
  into `cho-workflows`, naming the template's service account
  (`wf-277-rfai`, `wf-278-ingest`, `argo-workflow-sa` for 275/276). They run as
  `cho-workflow-sensor` (`infrastructure/argo-events/sensor-rbac.yaml`), which
  may only create Workflows (and get WorkflowTemplates, for submit validation)
  in `cho-workflows`. The `sftp-sensor` triggers no longer write the transaction
  type into `sftp-host` / `tenant-id`; they pass the event's folder and pattern.
- Fixed: `tenant-onboarding` no longer references the undefined
  `send-welcome-notification` template, and the `create-api-keys` template (two
  `args` keys, both a welcome-email stub that printed the temporary admin
  password, SFTP password and API key to the pod log, and no key creation) is
  removed with its step. There is no notification capability yet; a TODO in the
  workflow says what a replacement needs.
- Several steps install tools at run time from unpinned images (`alpine` +
  `apk add`, `curlimages/curl:latest`), and the 278 templates use `jq` in
  `curlimages/curl`, which does not ship it. A compromised step can obtain
  tokens only for its own workflow's tenants and permissions, for the life of
  its pod token.
- Workload tokens are not tenant-existence checked: a `*` registration
  (`wf-tenant-onboarding`) gets a token for any well-formed tenant id. Its
  permissions (settings:manage, and the activation route that only moves a
  `pending` tenant to `active`) bound what that allows.

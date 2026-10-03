# Argo workflows: CHO service tokens

Status: proposal. No workflow has been changed.

Every CHO service now requires a CHO token (see
`docs/security/service-auth-rollout-playbook.md`). Inside a service, the
shared `ChoOutboundTokenHandler` forwards the caller's token or mints a
`cho-internal` service token for the tenant a request names in `X-Tenant-ID`.
Argo workflow steps are not CHO services: they call CHO services with `curl`
or Python and do not hold a token. Every such call now gets **401**. The
`X-Tenant-ID` header some steps send is not a credential. A service uses it
only to name the tenant when it mints its own token.

This document lists what each workflow calls, compares three ways to get
tokens, recommends one, and gives the client id and permissions for each
workflow.

## Inventory

All files are in `infrastructure/argo-workflows/`. "Tenant" is where the step
gets the tenant it acts for. Permissions are the service defaults from the
playbook table.

| Workflow (kind, schedule) | Step → CHO call | Permission | Tenant | Auth today |
|---|---|---|---|---|
| `x12-834-enrollment-import.yaml` (CronWorkflow, every 10 min) | `import-to-cosmos` (curl) → enrollment-import-service `POST /api/v1/enrollment/import` | enrollment:process | `workflow.parameters.tenant-id`, sent as `X-Tenant-ID` | none |
| `x12-837-ingest.yaml` (WorkflowTemplate + CronWorkflow, every 5 min) | `create-claims-batch` (`containers/claims-publisher/publish-claims.sh`, curl) → claims-service `POST /api/claims` | claims:work | `tenant-id` parameter → `TENANT_ID`, sent as `X-Tenant-ID` | none |
| `claims-adjudication-workflow.yaml` and `claims-adjudication-template.yaml` (Workflow) | `get-claim` (curl) → claims-service `GET /api/claims/{id}` | claims:read | `tenant-id` parameter, sent as `X-Tenant-ID` | none |
| | `verify-coverage` (Python) → coverage-service `GET /api/coverage/member/{memberId}/active` | coverage:read | same | none |
| | `validate-provider` (Python) → provider-service `GET /api/providers/{npi}` | providers:read | same | none |
| | `validate-codes` (Python) → reference-data-service `GET /api/referencedata/icd10/{code}/validate`, `.../cpt/{code}/validate` | reference-data:read | not sent | none |
| | `check-prior-auth` (Python) → authorization-service `GET /api/authorizations/{authNumber}/validate` | authorizations:read | same | none |
| | `adjudicate-claim-lines` (Python) → benefit-plan-service `POST /api/v1/adjudication/adjudicate` | claims:work | same | none |
| | `update-claim` (Python) → claims-service `PUT /api/claims/{id}/pend`, `/adjudication`, `/status` | claims:work | same | none |
| `sla-deadline-watchdog.yaml` (CronWorkflow, every 15 min) | `query-at-risk` (curl) → authorization-service `GET /api/authorizations/sla/at-risk` | authorizations:read | **none**. The endpoint now takes the tenant from the token, so one call covers one tenant | an Azure workload-identity token from `/var/run/secrets/azure/token`. That is an Entra token, which CHO services do not accept |
| `tenant-onboarding.yaml` (Workflow) | `validate-tenant-exists` (curl) → tenant-service `GET /api/v1/tenants/{id}` | any authenticated caller in that tenant | `tenant-id` parameter (not sent) | none |
| | `seed-benefit-plans` (curl) → benefit-plan-service `POST /api/v1/benefitplans` | settings:manage | `tenant-id`, sent as `X-Tenant-ID` | none |
| | `update-tenant-status` (curl) → tenant-service `PUT /api/v1/tenants/{id}` with `{"status": ...}` | **platform:tenants**. Service tokens never satisfy a `platform:*` permission | `tenant-id` | none |
| | `create-stripe-subscription` → `api.stripe.com` | external (Stripe key from `stripe-api-keys`) | n/a | Stripe key; out of scope |
| `x12-277-rfai.yaml` (WorkflowTemplate) | `fetch-claim-template` (curl) → `$CLAIMS_BACKEND_API_URL/claims/{claimNumber}` (configmap `backend-config`) | claims:read if it points at claims-service | **none** | static bearer from secret `claims-backend-api-secret`. A CHO service rejects it |
| `x12-278-ingest.yaml`, `x12-278-replay.yaml` (WorkflowTemplate) | `claims-backend-api-template` (curl) → `claims-backend-api-endpoint` (default `https://backend.api.local/claims/process-278`) | depends on target; authorizations:write if pointed at authorization-service | **none** | static bearer `CLAIMS_BACKEND_API_TOKEN` |
| `x12-270-ingest.yaml`, `x12-271-eligibility-response.yaml` | eligibility-service `POST /api/eligibility/inquiry`, trading-partner-service | eligibility:check, trading-partners:read | `tenant-id` parameter | **no live call**: the curl lines are commented out |
| `x12-276-ingest.yaml`, `x12-277-claim-status.yaml` | claims-service claim lookup, trading-partner-service | claims:read, trading-partners:read | `tenant-id` parameter | **no live call**: commented out / "in production, call ..." |
| `test-workflow.yaml` (Workflow) | curl `GET /` on coverage, provider, claims, authorization and benefit-plan services | none needed | n/a | none. It should probe `/health/live`, which needs no token |
| `x12-275-ingest.yaml`, `x12-837-claims-scrubbing.yaml`, `sftp-test-workflow.yaml`, `workflow-metrics-config.yaml` | no HTTP calls to CHO services (SFTP, Kafka, blob storage only) | | | |

Some of these workflows duplicate work the services already do:

- claims-adjudication-workflow/template re-implement adjudication that
  claims-service now runs itself (`ClaimAdjudicationOrchestrator`, triggered
  on submission). They call benefit-plan-service's adjudicate endpoint without
  the stages claims-service runs (scrubbing, NCCI, COB, persistence).
- sla-deadline-watchdog duplicates authorization-service's own
  `SlaWatchdogService`, a hosted service that already scans open
  authorizations for every tenant inside the service.

Retiring those two is simpler than giving them tokens. They are in the table
below in case they are kept.

## Options

### A. Mount the `cho-internal` signing key and mint in the step

A small .NET or Python helper (an init container or a wrapper around `curl`)
reads the `cho-internal` private key from a Key Vault-backed secret and signs a
5-minute service token: `sub` and `azp` set to a per-workflow client id,
`tenant_id` set to the workflow's tenant, role `cho.service`.

- Works with the services as they are today, with no new endpoint.
- The `cho-internal` key signs tokens for **any tenant and any client id**, and
  those tokens satisfy **every tenant permission**. Every workflow pod that
  mounts it, including steps running unpinned third-party images
  (`curlimages/curl:latest`, `python:3.11-alpine`, `alpine` with `apk add` at
  run time), can act as any service in any tenant.
- The per-workflow client id is chosen by whoever holds the key, so it labels
  the call but does not restrict it. `[RequireServiceClient]` checks would be
  forgeable from any workflow pod.
- Rotation means updating every workflow secret as well as every service.

### B. Exchange the pod's Kubernetes identity at token-service (recommended)

Add one endpoint to token-service, which already holds the `cho-internal` key
(`ChoAuth__ServiceToken__PrivateKeyPem` from `token-service-secrets`) and
already signs with Key Vault:

```
POST /v1/token/workload
Authorization: Bearer <projected service-account token, audience "cho-token-service">
{ "tenantId": "tenant-a" }
→ 200 { "access_token": "<cho-internal token, 5 min>", "expires_in": 300 }
```

1. Each workflow runs under its own Kubernetes service account
   (`wf-enrollment-import`, ...), with a projected token volume
   (`serviceAccountToken`, `audience: cho-token-service`, `expirationSeconds: 600`).
2. token-service validates that token against the cluster's OIDC issuer (the
   AKS OIDC issuer used for workload identity), with audience
   `cho-token-service`.
3. It looks up the service account (`system:serviceaccount:<ns>:<name>`) in a
   configured allow-list: `{ clientId, allowedTenants: "*" | [..], permissions: [..] }`.
   An unknown account gets 403. A tenant outside `allowedTenants` gets 403.
4. It mints a `cho-internal` token with `sub = azp = clientId` and
   `tenant_id = tenantId`, and logs `clientId, tenantId, workflow pod` for audit.

A step gets its token with plain `curl`, so no helper image is needed:

```sh
TOKEN=$(curl -sf -X POST http://token-service/v1/token/workload \
  -H "Authorization: Bearer $(cat /var/run/secrets/cho/token)" \
  -H "Content-Type: application/json" \
  -d "{\"tenantId\":\"$TENANT_ID\"}" | jq -r .access_token)
curl -H "Authorization: Bearer $TOKEN" -H "X-Tenant-ID: $TENANT_ID" ...
```

- No signing key leaves Key Vault or token-service. A compromised workflow pod
  can get tokens only for its own client id and its allowed tenants, for as
  long as its pod token lives.
- The client id is bound to a Kubernetes identity, so `[RequireServiceClient]`
  can safely admit a specific workflow (needed for tenant activation, below).
- Removing a workflow's access is one allow-list edit. Key rotation stays a
  token-service concern.
- Cost: one endpoint in token-service, an OIDC-issuer setting, an allow-list in
  configuration, and service accounts and token volumes in the workflow manifests.

### C. Call through an existing service

Move the step's logic into the CHO service that owns it, which then authenticates
as itself (for example an SFTP poller hosted in enrollment-import-service, the
existing `SlaWatchdogService`, or claims-service's own adjudication).

- No new credentials at all.
- Applies only where a service already does, or should do, the work. It
  cannot cover onboarding steps that orchestrate several services and external
  systems.

## Recommendation

**Option B**: a token-service workload-token exchange, with a dedicated service
account and client id per workflow. It is the only option where a workflow's
identity is enforced rather than self-asserted, and it keeps the `cho-internal`
key out of workflow pods. Option A would put a key that can act in every tenant
with every tenant permission into pods running unpinned public images.

Retire `sla-deadline-watchdog` and `claims-adjudication-*` (option C: the
services already do this work) rather than issuing them tokens.

Two limits apply to tokens from option B:

- **Permissions.** Today every service token carries `cho.service`, which
  satisfies every tenant permission. Until the shared layer can honour a
  narrower permission list on service tokens, the "permissions" column below is
  the allow-list token-service should record and audit against. When the shared
  layer supports it, token-service should mint exactly those permissions
  instead of `cho.service`. Changing that needs changes to the shared
  authorization code and `ChoRolePermissions`, which are outside this proposal.
- **Platform actions.** Service tokens never satisfy `platform:*`.
  tenant-onboarding's `update-tenant-status` (`PUT /tenants/{id}` with a status)
  needs `platform:tenants`, so no workflow token can do it. Add a narrow
  tenant-service endpoint, for example
  `POST /internal/v1/tenants/{id}/onboarding-complete`, guarded by
  `[RequireServiceClient("wf-tenant-onboarding")]` and audited. Option B makes
  that guard meaningful. Under option A it could be forged.

## Per-workflow client ids and permissions

| Workflow | Service account / client id | Allowed tenants | Permissions (allow-list) | Notes |
|---|---|---|---|---|
| `x12-834-enrollment-import` | `wf-enrollment-import` | tenants with an 834 feed (list) | enrollment:process | Also needs a per-tenant fan-out: today the CronWorkflow has one `tenant-id` parameter. |
| `x12-837-ingest` (claims-publisher) | `wf-837-ingest` | tenants with an 837 feed (list) | claims:work | `publish-claims.sh` needs the token call added before its curl. |
| `tenant-onboarding` | `wf-tenant-onboarding` | `*` (it creates new tenants), every issuance audited | settings:manage (seed benefit plans); tenant-service own-tenant read; `[RequireServiceClient("wf-tenant-onboarding")]` activation endpoint for the status change | The status change cannot use a tenant permission (see above). |
| `x12-277-rfai` | `wf-277-rfai` | tenants with RFAI enabled (list) | claims:read | The template has no tenant parameter. Add one, and point `backend-config/api-url` at claims-service instead of using the static `claims-backend-api-secret`. |
| `x12-278-ingest`, `x12-278-replay` | `wf-278-ingest` | tenants with 278 intake (list) | authorizations:write (if the target is authorization-service) | Same as 277-rfai: add a tenant parameter. Today the target is an external placeholder with a static token. |
| `x12-270-ingest`, `x12-271-eligibility-response` | `wf-270-271` | tenants with 270 intake (list) | eligibility:check, trading-partners:read | Only when the commented-out calls are enabled. |
| `x12-276-ingest`, `x12-277-claim-status` | `wf-276-277` | tenants with 276 intake (list) | claims:read, trading-partners:read | Only when the commented-out calls are enabled. |
| `claims-adjudication-workflow` / `-template` (if kept) | `wf-claims-adjudication` | list | claims:read, claims:work, coverage:read, providers:read, reference-data:read, authorizations:read | Recommended: retire (claims-service adjudicates). |
| `sla-deadline-watchdog` (if kept) | `wf-sla-watchdog` | `*`, one call per tenant | authorizations:read | Recommended: retire (`SlaWatchdogService`). The Entra token it sends today is not a CHO token. |
| `test-workflow` | none | | | Probe `/health/live` instead of `/`. |

No service has to trust a new issuer: the tokens are ordinary `cho-internal`
service tokens, and the client id appears as `sub`/`azp` in every service's logs.

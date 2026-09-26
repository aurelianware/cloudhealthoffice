# Provider Eligibility API

Outbound eligibility (270/271) for provider applications such as CloudDentalOffice.
Cloud Health Office runs the check through the configured healthcare gateway (Stedi
in production) and returns a normalized, vendor-neutral result.

This is deliberately a small host. It serves eligibility checks and payer search and
nothing else: no payer-side responders, batch jobs, claims, demo routes or database.
The full `eligibility-service` is a different product surface (CHO as payer).

## API

All `/api/v1` requests need both headers:

| Header | Value |
| --- | --- |
| `X-Api-Key` | The client credential issued to the calling application |
| `X-Tenant-ID` | A tenant on that client's allow-list |

A credential only works for the tenants listed for it in `ProviderApi:Clients`. The
tenant is never read from the query string or body.

### `POST /api/v1/eligibility/check`

```json
{
  "payerId": "87726",
  "provider": { "npi": "1999999984", "organizationName": "3rd Set Smiles" },
  "subscriber": { "memberId": "…", "firstName": "…", "lastName": "…", "dateOfBirth": "1961-04-17" },
  "patient": { "firstName": "…", "lastName": "…", "dateOfBirth": "2011-09-03", "relationshipToSubscriber": "child" },
  "groupNumber": "optional",
  "serviceTypeCode": "35",
  "serviceDate": "optional, defaults to today",
  "correlationId": "optional, no PHI"
}
```

Omit `patient` when the patient is the subscriber. `serviceTypeCode` defaults to `30`
(health benefit plan coverage); use `35` for dental care.

| Status | Meaning |
| --- | --- |
| 200 `Completed` | Payer answered; see `eligible`, `coverageStatus`, plan and `benefits` |
| 200 `Rejected` | Payer rejected the inquiry (for example unknown member); `message` says why |
| 400 | Request invalid; `fields` names each problem. No payer call was made |
| 422 | Caller-fixable: unknown payer, enrollment required, or the clearinghouse rejected the data |
| 502 | CHO or clearinghouse configuration/credential problem — not fixable by the caller |
| 503 | Temporary: rate limit, timeout, clearinghouse outage, or the payer directory is still loading (`ReferenceDataUnavailable`, with `Retry-After`) — retry later |

Responses never echo member ids, names or dates of birth.

### `GET /api/v1/payers?q=<text>&maxResults=10`

Search the payer directory (synchronized from Stedi at startup and every 24 hours) so a
practice can link each insurance plan to a routable payer. Returns `id`, `name`,
`payerIds` and whether the payer supports eligibility.

## Configuration

| Setting | Notes |
| --- | --- |
| `ProviderApi:Clients:N:Name` / `ApiKey` / `Tenants:M` | One entry per calling application. Missing or incomplete clients fail closed (503) |
| `HealthcareTransactions:DefaultGateway` | `Stedi` by default. `Mock` only in Development; outside Development the service refuses to start on Mock (an unset value counts as Mock) |
| `HealthcareTransactions:Gateways:Stedi:ApiKey` | From Key Vault. Missing key → 502 `Configuration`, never a mock answer |
| `HealthcareTransactions:Gateways:Stedi:Environment` | `test` or `production`, matching the key's mode |

Claim lifecycle stores are in memory because this host never reads or writes them;
`HealthcareTransactions:ClaimLifecycle:AllowInMemoryInNonDevelopment` is set for that
reason only.

## Deploy

`infrastructure/azure/provider-eligibility-container-app.bicep` deploys the app into the
Container Apps environment shared with CloudDentalOffice, with **internal-only ingress**.

```sh
az keyvault secret set --vault-name cho-kv --name provider-eligibility-stedi-api-key --value "<stedi key>"
CHO_PROVIDER_ELIGIBILITY_TENANT_IDS=<cdo tenant id> ./scripts/deploy-provider-eligibility-container-app.sh preview
CHO_PROVIDER_ELIGIBILITY_TENANT_IDS=<cdo tenant id> ./scripts/deploy-provider-eligibility-container-app.sh deploy
```

Set `CHO_PROVIDER_ELIGIBILITY_STEDI_ENV=production` only with a production Stedi key.
Test-mode keys return Stedi's mock responses and cannot check real patients.

Or run the **Deploy CDO-facing services (Container Apps)** workflow
(`.github/workflows/deploy-cdo-services.yml`) from main: pick `provider-eligibility`,
`mode=preview` to see the what-if, then `mode=deploy`. It waits for the new revision to be
ready. The Stedi key must already be in Key Vault; the workflow never handles it.

## Local development

```sh
dotnet run --project src/services/provider-eligibility-api
curl -s localhost:5000/api/v1/eligibility/check \
  -H 'X-Api-Key: local-development-only' -H 'X-Tenant-ID: local-tenant' \
  -H 'Content-Type: application/json' -d @request.json
```

Development uses the Mock gateway and synthetic payers.

## Readiness

The payer directory is held in memory and loaded from Stedi when the replica
starts. `/health/ready` stays unhealthy until that first load succeeds, and
until then both endpoints answer 503 `ReferenceDataUnavailable` with
`Retry-After` rather than a 422 "payer not found". If the startup load fails,
the service retries with backoff (from 60 seconds, capped at 5 minutes) instead
of waiting for the next daily sync. `/health/live` does not wait for the
directory.

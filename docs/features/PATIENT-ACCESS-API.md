# Patient Access API

The **PatientAccessApi** Logic App implements CMS-0057-F patient access requirements by exposing FHIR R4 search/read endpoints backed by Cosmos DB. SMART-on-FHIR authentication is enforced with Azure AD B2C and all responses comply with US Core where applicable.

## Endpoints

| Endpoint | Method | Description | Required Scopes |
| --- | --- | --- | --- |
| `/Patient` | GET | Search for patients (_id, identifier, _lastUpdated, _count) | `patient/*.read` or `user/*.read`, `openid`, `profile` |
| `/Patient/{id}` | GET | Read a specific Patient | `patient/*.read` or `user/*.read`, `openid`, `profile` |
| `/Coverage` | GET | Patient coverage search (_lastUpdated, _count) | `patient/*.read` or `user/*.read`, `openid`, `profile` |
| `/ExplanationOfBenefit` | GET | EOB search for the member (_lastUpdated, _count) | `patient/*.read` or `user/*.read`, `openid`, `profile` |
| `/Claim` | GET | Claim search (_lastUpdated, _count) | `patient/*.read` or `user/*.read`, `openid`, `profile` |

- Responses are returned as FHIR Bundles with `Bundle.link.self` set to the original request URL.
- The `Prefer` header is honoured (`return=minimal`, `respond-async`).
- All errors are surfaced as FHIR `OperationOutcome` resources.

## Authentication

SMART-on-FHIR tokens issued by Azure AD B2C are validated by the HTTP trigger:

- **Audience**: Configured via workflow parameter `b2cAudience`.
- **Issuer**: Configured via workflow parameter `b2cIssuer`.
- **Required scopes**: `patient/*.read` *or* `user/*.read`, plus `openid` and `profile`.

Tokens without the required scopes are rejected with HTTP 403 and an `OperationOutcome` (issue code `forbidden`).

## Rate Limiting

A per-member throttle protects downstream systems:

- **Limit**: 1000 requests per member per hour.
- **Storage**: Cosmos DB container `rate_limits` with partition key `/memberId` and TTL `3600` seconds.
- **Response headers**: `X-RateLimit-Remaining`, `X-RateLimit-Reset`.
- **Breach behaviour**: Requests beyond the threshold return HTTP 429 and an `OperationOutcome` with code `too-many-requests`.

## Data Sources

All FHIR resources are materialised via Cosmos DB queries against the existing `cloudhealthoffice` database:

- `Members` container → Patient & Coverage resources (US Core profiles).
- `Claims` container → Claim resources.
- `Payments` container → ExplanationOfBenefit resources.

Transformation logic reuses shared mappers in `src/fhir/patient-access-mapper.ts` (built atop `ProviderAccessApi`). HIPAA redaction is applied before returning any resource payloads.

## Generated Specifications

Running the generator script creates formal specifications:

```bash
npm run build
node dist/scripts/generate-patient-access-specs.js
```

Artifacts are written to `generated/infra/patient-access-api/`:

- `capabilitystatement.json`
- `openapi.yaml`

## Example Requests

```http
GET https://{logic-app-host}/Patient?_id=MEM123&_count=25 HTTP/1.1
Authorization: Bearer {access_token}
Accept: application/fhir+json
Prefer: respond-async
```

```http
GET https://{logic-app-host}/Claim?patient=MEM123&_lastUpdated=2024-01-01T00:00:00Z HTTP/1.1
Authorization: Bearer {access_token}
Accept: application/fhir+json
```

Each request returns a Bundle with the member’s resources limited to the past 24 months when `_lastUpdated` is omitted. Errors return `OperationOutcome` resources with diagnostics for client troubleshooting.

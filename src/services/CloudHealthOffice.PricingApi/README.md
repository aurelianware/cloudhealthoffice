# CloudHealthOffice Claims Pricing API

**Vendor-neutral claims repricing by Aurelianware, Inc.**

Price professional, outpatient, and inpatient claims against Medicare fee schedules (RBRVS, OPPS, MS-DRG) — or upload your own contracted rates. Built for health plans, TPAs, clearinghouses, and anyone who needs accurate claims pricing without vendor lock-in.

**Free tier: 1,000 claims/month. No credit card required.**

## Quick Start

```bash
# Start locally with Docker
docker compose up -d

# Browse available fee schedules (no auth required)
curl http://localhost:8080/api/v1/fee-schedules | jq

# Look up a single code
curl "http://localhost:8080/api/v1/lookup/99213?feeScheduleId=MEDICARE_RBRVS_2025" \
  -H "X-API-Key: your-api-key" | jq

# Reprice a professional claim
curl -X POST http://localhost:8080/api/v1/reprice \
  -H "Content-Type: application/json" \
  -H "X-API-Key: your-api-key" \
  -d @- <<'EOF' | jq
{
  "feeScheduleId": "MEDICARE_RBRVS_2025",
  "claimType": "Professional",
  "placeOfService": "11",
  "lines": [
    {
      "lineNumber": 1,
      "procedureCode": "99214",
      "units": 1,
      "billedAmount": 175.00
    },
    {
      "lineNumber": 2,
      "procedureCode": "71046",
      "units": 1,
      "billedAmount": 85.00
    }
  ]
}
EOF
```

## API Endpoints

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| `GET` | `/api/v1/fee-schedules` | No (CMS Medicare schedules only) | List available fee schedules |
| `GET` | `/api/v1/fee-schedules/{id}` | No (CMS Medicare schedules only) | Get fee schedule details |
| `GET` | `/api/v1/lookup/{code}` | No (CMS Medicare schedules only) | Look up a single procedure code |
| `POST` | `/api/v1/reprice` | API key, or CHO token | Reprice a claim |
| `POST` | `/api/v1/reprice/batch` | API key, or CHO token | Reprice up to 100 claims |
| `POST` | `/api/v1/signup` | CHO token, `platform:admin` | Issue a free-tier key |
| `*` | `/api/v1/admin/*` | CHO token, `platform:admin` | API keys, usage, fee schedule loads |
| `GET` | `/health` | No | Health check |

## Authentication

Two schemes; a request carries exactly one (both together is 401).

**External customers: `PricingApiKey`.** Pass your API key in the `X-API-Key` header:

```
X-API-Key: cho_pk_a1b2c3d4e5f6...
```

Request access at [cloudhealthoffice.com/pricing-api](https://cloudhealthoffice.com/pricing-api).
The key is bound to its own credential tenant (`pricing-api-key:<key id>`),
never a CHO tenant; an `X-Tenant-ID` header that names another tenant is
refused (403). A key holds no CHO permission: it can reprice (metered against
its monthly quota) and read every fee schedule, nothing else.

**Key storage.** The service stores a key's SHA-256 and its first 12
characters (`keyPrefix`), never the key. The key is in the create (or signup)
response only; it cannot be shown again. Admin actions address a key by its
`keyId` (`pk_…`): `DELETE /api/v1/admin/api-keys/{keyId}`. Usage records carry
the key id. Keys stored in plaintext by earlier versions are hashed at startup
(idempotent; logged as `AUDIT pricing api-key migration`).

**Rate limiting.** `RateLimiting:PermitLimit` requests per
`RateLimiting:WindowSeconds` per caller, after authentication: an API-key
customer by key id, a CHO caller by tenant and subject, anyone else (anonymous,
or an unknown key) by client address. Over the limit: 429.

**CHO callers: CHO bearer token** (`AddChoAuthentication`). The tenant and the
actor come from the token.

| Action | Permission |
|---|---|
| Reprice, batch reprice, reading non-CMS schedules | `contracts:read`, `claims:work` or `benefits:read` (service tokens qualify); not metered |
| Admin (API keys, usage reset, RBRVS/OPPS/DRG loads, demo seed) and signup | `platform:admin` (global data; tenant roles via `*:*` and service tokens never qualify) |

The issuing or deactivating admin (token subject) is recorded on the key
(`createdBy`, `deactivatedBy`) and every admin action is logged with `AUDIT`.
The former `X-Admin-Secret` header is no longer read.

**Anonymous.** The catalog and single-code lookup serve CMS-published Medicare
schedules (RBRVS, OPPS, MS-DRG) without credentials: they are public federal
data with nothing of any tenant or customer. Any other schedule type (Medicaid,
commercial) reads as not found unless the caller is an API-key customer or a
CHO caller with a pricing permission.

## Fee Schedules

### Included (public Medicare data)

| ID | Type | Description |
|----|------|-------------|
| `MEDICARE_RBRVS_2025` | Professional | Physician Fee Schedule (RVU-based) |
| `MEDICARE_OPPS_2025` | Outpatient | Outpatient Prospective Payment (APC-based) |
| `MEDICARE_DRG_2025` | Inpatient | MS-DRG relative weights |

### Custom Fee Schedules (Starter+ plans)

Upload your own contracted rates via CSV or the management API. The pricing engine applies your custom rates with the same modifier, MPPR, and geographic adjustment logic.

## Claim Type and Type of Bill

`claimType` and `billType` are optional. They select the claim's setting the same
way claims adjudication does (ADR 016), so the Pricing API and adjudication return
the same allowed amount for the same claim:

| `claimType` | Setting | Notes |
|-------------|---------|-------|
| `professional` | Facility rate only for a facility `placeOfService` (21, 22, 23, ...) | 837P |
| `dental` | Same as professional | 837D |
| `institutional` | Always the facility rate | 837I; priced by DRG against an MS-DRG schedule only with an inpatient type of bill (11x/12x), otherwise line by line |
| `outpatient` | Always the facility rate | 837I |
| `inpatient` | Always the facility rate; priced by `drgCode` | 837I |
| *(absent)* | `institutional` when `billType` is valid, otherwise `professional` | Unchanged for requests without `billType` |

`billType` is the NUBC type of bill (837I CLM05-1 facility type + CLM05-3
frequency): three digits (`"131"`) or four with a leading zero (`"0131"`). It is
echoed in the response normalized to three digits. A malformed `billType`
(`"0"`, `"N/A"`, `"13"`), or a `billType` on a `professional` or `dental` claim,
is rejected with `400 INVALID_BILL_TYPE`. A blank or whitespace `billType` is
treated as absent (it was ignored before the field was validated). A claim type
that contradicts its type of bill (`outpatient` with 11x/12x, `inpatient` with any
other) is priced by its claim type and the response carries a warning. On an institutional claim
`placeOfService` is not read for the facility decision (it may hold the CLM05-1
facility type code).

> Behaviour change: an `outpatient` claim priced against a professional
> (RBRVS/commercial) schedule now takes the facility rate whatever its
> `placeOfService`, matching adjudication (before, a non-facility POS such as the
> default `11` gave the non-facility rate). OPPS and MS-DRG pricing are unaffected.
>
> 837I → facility rate is the parity rule with adjudication (ADR 016 §5). Known CMS
> exceptions are not yet modelled: hospital outpatient therapy (TOB 13x, revenue
> codes 042x–044x, paid at the MPFS non-facility rate) and CAH Method II
> professional services (revenue codes 096x–098x).

```bash
# Reprice a hospital outpatient claim (type of bill 131)
curl -X POST http://localhost:8080/api/v1/reprice \
  -H "Content-Type: application/json" \
  -H "X-API-Key: your-api-key" \
  -d '{
    "feeScheduleId": "MEDICARE_RBRVS_2025",
    "claimType": "institutional",
    "billType": "0131",
    "lines": [
      { "procedureCode": "99213", "revenueCode": "0510", "units": 1, "billedAmount": 210.00 }
    ]
  }' | jq
```

## Example: Reprice an Inpatient Claim (DRG)

```bash
curl -X POST http://localhost:8080/api/v1/reprice \
  -H "Content-Type: application/json" \
  -H "X-API-Key: your-api-key" \
  -d '{
    "feeScheduleId": "MEDICARE_DRG_2025",
    "claimType": "Inpatient",
    "drgCode": "470",
    "primaryDiagnosis": "M16.11",
    "lines": [
      { "lineNumber": 1, "procedureCode": "27130", "units": 1, "billedAmount": 42000.00 }
    ]
  }' | jq
```

Response:

```json
{
  "success": true,
  "data": {
    "requestId": "a1b2c3d4e5f6",
    "feeScheduleId": "MEDICARE_DRG_2025",
    "claimType": "Inpatient",
    "drgCode": "470",
    "totalAllowed": 11090.86,
    "totalBilled": 42000.00,
    "lines": [
      {
        "lineNumber": 1,
        "procedureCode": "27130",
        "units": 1,
        "allowedAmount": 11090.86,
        "billedAmount": 42000.00,
        "breakdown": {
          "baseRate": 6377.73,
          "drgRelativeWeight": 1.7390,
          "hospitalBaseRate": 6377.73
        },
        "status": "Priced"
      }
    ],
    "pricedAt": "2025-03-20T15:30:00Z"
  }
}
```

## Pricing Tiers

| Tier | Monthly Claims | Price |
|------|---------------|-------|
| **Free** | 1,000 | $0 |
| **Starter** | 10,000 | [Contact us](mailto:sales@cloudhealthoffice.com) |
| **Professional** | 100,000 | [Contact us](mailto:sales@cloudhealthoffice.com) |
| **Enterprise** | Unlimited | [Contact us](mailto:sales@cloudhealthoffice.com) |

## Rate Limiting

- **Per-minute**: 100 requests/minute per API key
- **Monthly**: Based on your pricing tier (counted by claim lines)
- Rate limit headers included in every response: `X-RateLimit-Limit`, `X-RateLimit-Remaining`

## Deployment

### Docker

```bash
docker compose up -d
```

### AKS (CloudHealthOffice namespace)

```bash
# Build and push
docker build -t acr.azurecr.io/cho-pricing-api:latest .
docker push acr.azurecr.io/cho-pricing-api:latest

# Deploy to existing CHO namespace
kubectl apply -f k8s/pricing-api.yaml -n cloudhealthoffice
```

## Roadmap

- [ ] FHIR Claim / ClaimResponse resource support ($reprice operation)
- [ ] Medicaid state fee schedules (starting with TX, CA, FL, NY)  
- [ ] Contract upload API for custom/commercial rates
- [ ] DRG grouper integration (ICD-10 → MS-DRG)
- [ ] Geographic Practice Cost Index (GPCI) adjustments by locality
- [ ] Webhook notifications for fee schedule updates
- [ ] SDKs: Python, TypeScript, C#

## License

Business Source License 1.1 — see [LICENSE](LICENSE).

© 2025 Aurelianware, Inc.

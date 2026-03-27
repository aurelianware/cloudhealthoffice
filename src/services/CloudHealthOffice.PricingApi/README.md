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
| `GET` | `/api/v1/fee-schedules` | No | List available fee schedules |
| `GET` | `/api/v1/fee-schedules/{id}` | No | Get fee schedule details |
| `GET` | `/api/v1/lookup/{code}` | Yes | Look up a single procedure code |
| `POST` | `/api/v1/reprice` | Yes | Reprice a claim |
| `POST` | `/api/v1/reprice/batch` | Yes | Reprice up to 100 claims |
| `GET` | `/health` | No | Health check |

## Authentication

Pass your API key in the `X-API-Key` header:

```
X-API-Key: cho_pk_a1b2c3d4e5f6...
```

Register for a free key at [cloudhealthoffice.com/pricing-api](https://cloudhealthoffice.com/pricing-api).

## Fee Schedules

### Included (public Medicare data)

| ID | Type | Description |
|----|------|-------------|
| `MEDICARE_RBRVS_2025` | Professional | Physician Fee Schedule (RVU-based) |
| `MEDICARE_OPPS_2025` | Outpatient | Outpatient Prospective Payment (APC-based) |
| `MEDICARE_DRG_2025` | Inpatient | MS-DRG relative weights |

### Custom Fee Schedules (Starter+ plans)

Upload your own contracted rates via CSV or the management API. The pricing engine applies your custom rates with the same modifier, MPPR, and geographic adjustment logic.

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

# Synthetic CMS-0057-F Demo Tenant

This folder runs a **labeled synthetic** Cloud Health Office demo. It is not a
production tenant, not a compliance attestation, and not live payer data.

## Labels

Every FHIR response from `fhir-service` carries:

| Header | Meaning |
| --- | --- |
| `X-CHO-Adapter-Mode` | `Demo`, `Hybrid`, or `Live` |
| `X-CHO-Data-Class` | `synthetic`, `de-identified`, `limited-phi`, or `production-phi` |
| `X-CHO-Adapter-Label` | Buyer-safe sentence for the effective mode |

Inventory endpoint (no auth, no tenant header):

```bash
curl -s http://localhost:5023/fhir/r4/adapter-status | jq
```

Tenant config: [`config/demo-tenant/demo-tenant.json`](../../config/demo-tenant/demo-tenant.json).

## Start the stack

```bash
docker compose --profile fhir up -d --build
curl -s http://localhost:5023/health/live
curl -s http://localhost:5023/fhir/r4/adapter-status | jq '.effectiveMode,.dataClassification,.resources[] | select(.resource=="Patient" or .resource=="PayerToPayer")'
```

`fhir-service` publishes on port **5023**. The older demo script defaulted to
5007 (eligibility). Override `FHIR_URL` if you still use that.

## Run the script

```bash
export FHIR_URL=http://localhost:5023
export TENANT_ID=demo-tenant
./scripts/demo/cms-0057-f-demo.sh
```

The script labels every step Demo / Hybrid / Out of scope. FHIR calls that
require SMART scopes will skip with a label rather than claiming success when
`FHIR_TOKEN` is unset.

## What this is not

- Not CMS certification.
- Not a payer attestation.
- Not a live QNXT / Facets / HealthEdge integration.
- Not an invitation to load PHI. Sign a BAA first; see
  [`docs/diligence/`](../../docs/diligence/README.md).

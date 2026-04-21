# idcard-service

Digital-first member ID card issuance. See
[`docs/architecture/idcard-service.md`](../../../docs/architecture/idcard-service.md)
for the full design write-up.

## Endpoints

| Method | Route | Description |
|---|---|---|
| POST | `/api/v1/id-cards/orders` | Order (issue) a card |
| GET  | `/api/v1/id-cards/{orderId}` | Order status |
| POST | `/api/v1/id-cards/{cardId}/revoke` | Revoke an issued card |
| GET  | `/api/v1/members/{memberId}/id-cards` | Card history |
| POST | `/api/v1/id-cards/scan` | Scan QR (provider JWT + rate-limited) |

All endpoints (except `/health/*` and `/swagger/*`) require an `X-Tenant-ID`
header.

## Configuration

Key settings live under `IdCard:*` in `appsettings.json` and can be
overridden via environment variables (`IdCard__CurrentKeyVersion` etc.).

- `IdCard:SigningKeySecretPrefix` — Key Vault secret prefix; keys are
  resolved as `{prefix}-{version}`.
- `IdCard:CurrentKeyVersion` — the version used to sign new cards.
- `IdCard:AcceptedKeyVersions` — the rolling window that the scan endpoint
  still accepts (default: `[CurrentKeyVersion]`).
- `IdCard:DevSigningKeys:{version}` — development-only fallback when no
  secret provider is configured.
- `IdCard:ScanRateLimit:*` — per-tenant, per-provider, per-card per-minute
  limits for the scan endpoint.
- `IdCard:QnxtMirror:*` — Service Bus mirror queue connection (leave empty
  for the in-memory fallback).
- `IdCard:Reconciliation:IntervalHours` — cadence for the QNXT mirror
  reconciliation job.
- `IdCard:HealthCheckTenants` — tenants whose global template presence is
  part of the readiness probe.
- `ProviderJwt:Authority` / `ProviderJwt:Audience` — provider JWT validation
  for the scan endpoint. Leaves dev mode active when empty.

## Storage

Auto-detects MongoDB → Cosmos DB → in-memory (dev only) based on connection
strings. Collections / containers: `idcard_orders`, `idcard_records`,
`idcard_templates`.

## Key rotation

1. Publish `idcard-signing-key-v{n+1}` in Key Vault.
2. Set `IdCard:CurrentKeyVersion = v{n+1}`.
3. Keep the previous version in `AcceptedKeyVersions` for the rolling window.
4. After the window expires, drop the old version — cards signed under it
   will return `CARD_SIGNATURE_STALE` so the portal prompts re-issue.

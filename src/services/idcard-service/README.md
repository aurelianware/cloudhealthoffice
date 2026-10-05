# idcard-service

Digital-first member ID card issuance. See
[`docs/architecture/idcard-service.md`](../../../docs/architecture/idcard-service.md)
for the full design write-up.

## Endpoints

| Method | Route | Description | Caller needs |
|---|---|---|---|
| POST | `/api/v1/id-cards/orders` | Order (issue) a card | CHO token, `members:write` |
| GET  | `/api/v1/id-cards/{orderId}` | Order status | CHO token, `members:read` |
| POST | `/api/v1/id-cards/{cardId}/revoke` | Revoke an issued card | CHO token, `members:write` |
| GET  | `/api/v1/members/{memberId}/id-cards` | Card history | CHO token, `members:read` |
| POST | `/api/v1/id-cards/scan` | Scan QR (rate-limited) | provider JWT (`ProviderJwt` scheme) |

CHO endpoints use the shared CHO authentication
(`docs/security/service-auth-rollout-playbook.md`): the tenant and the acting
user come from the token only. `X-Tenant-ID` is tolerated only as an echo of the
token tenant. The scan endpoint takes provider JWTs; its tenant is the one in the
card's signed QR payload. Upstream calls carry the caller's CHO token, or this
service's own service token (client id `idcard-service`) when there is no CHO
caller (background work, provider scans).

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

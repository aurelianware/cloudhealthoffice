# idcard-service

Digital-first member ID card issuance, QR scan verification, and card history.
Phase 1: PDF + PNG delivered via member-document-service. Phase 2: Apple/Google
Wallet and physical mail fulfillment.

## Service context

```
                          +-----------------------+
                          |    idcard-service     |
                          +----------+------------+
                                     |
     +-------------------------------+--------------------------------+
     |        |             |             |            |              |
     v        v             v             v            v              v
 member-   coverage-   sponsor-    benefit-plan-   member-doc-    eligibility-
 service   service    service      service         service        service
                                                   (PDF blob +    (271 snapshot
                                                    FHIR DR)      on scan)
```

Other collaborators:

- **tenant-service** — adapter platform configuration (`cho` | `qnxt` |
  `fulfillment-vendor`).
- **ISecretProvider / Azure Key Vault** — versioned HMAC signing keys for
  the card QR payload.
- **Service Bus** — QNXT augment-mode mirror queue (`qnxt-idcard-requests`).

## Adapter pattern

Matches `eligibility-service`. `IIdCardAdapter` has three implementations
registered at `ChoIdCardAdapter`, `QnxtIdCardAdapter`, `FulfillmentVendorAdapter`.
`IdCardAdapterFactory` resolves the correct adapter per tenant from
`tenant-service`'s `configuration.idCardPlatform.platform`, defaulting to
`cho`.

### ChoIdCardAdapter (default)

1. Fan out: `member-service` + `coverage-service` in parallel.
2. Fan out: `sponsor-service` (by groupNumber) + `benefit-plan-service` (by
   planId) in parallel.
3. Resolve template via `TemplateResolver` (sponsor+plan → sponsor-default →
   global-default).
4. Generate QR (`QrCodeService.GenerateAsync`) — HMAC-signed payload with
   the current key version.
5. Render card: QuestPDF for the PDF, SkiaSharp (Svg.Skia) for the PNG preview.
6. Upload PDF + PNG to `member-document-service` with `Category=IdCard`.
7. Persist `IdCardRecord`, update order → `Issued`.

### QnxtIdCardAdapter (augment)

Delegates to `ChoIdCardAdapter` for the actual issuance, then enqueues a
mirror message to the `qnxt-idcard-requests` Service Bus queue. Enqueue
failures log a warning but never fail the order — the nightly
`QnxtMirrorReconciliationJob` backfills any missed messages.

### FulfillmentVendorAdapter

Phase-2 placeholder: throws `NotSupportedException` with a clear message so
misconfigured tenants fail loudly instead of silently dropping through.

## Order state machine

```
         POST /orders
              |
              v
         +----------+
         | Pending  |
         +----------+
              |
              v
        +-----------+
        | Rendering |  — parallel upstream lookups, template resolve
        +-----+-----+
              |
              v
        +-----------+
        | Uploading |  — PDF + PNG to member-document-service
        +-----+-----+
              |
      +-------+-------+
      v               v
 +--------+       +--------+
 | Issued |       | Failed |
 +--------+       +--------+
```

`Cancelled` is reserved for Phase 2 (user cancels a physical-card order
before the vendor has pulled it).

## QR payload and signing

### Payload

On-wire form is `{canonical_base64url}.{signature_base64url}`. The canonical
part is `JsonSerializer.SerializeToUtf8Bytes(QrCardPayload)` with fixed
property names:

| Field | JSON | Notes |
|---|---|---|
| `Version`     | `v` | Protocol version (currently 1) |
| `TenantId`    | `t` | Cross-checked by scan endpoint |
| `MemberId`    | `m` | |
| `CardId`      | `c` | Opaque, generated at issuance |
| `IssuedAtUnix`| `i` | Unix seconds — deterministic under repeat-sign |
| `KeyVersion`  | `k` | Which signing key signed this card |

Signature: `HMAC-SHA256(canonicalBytes, key[keyVersion])`. Keys come from
`ISecretProvider` under `{prefix}-{version}` (default prefix
`idcard-signing-key`).

### Key rotation

Rotation is a **configuration change** — no code change, no mass re-issuance.

1. Publish a new secret, e.g. `idcard-signing-key-v2`, in Key Vault.
2. Update `IdCard:CurrentKeyVersion` to `v2`.
3. Keep `v1` in `IdCard:AcceptedKeyVersions` for the rolling window.
4. After the window expires, drop `v1` from `AcceptedKeyVersions` — any
   card still signed under `v1` starts returning `CARD_SIGNATURE_STALE` and
   the portal prompts the member to request a new one.

Rolling-window default: current + two previous versions. Tests cover:
round-trip verification, tamper detection, previous-version acceptance
within the window, stale rejection outside the window
(`CARD_SIGNATURE_STALE`), audit round-trip (persisted canonical payload
matches the scanned one).

## Scan validation (`POST /api/v1/id-cards/scan`)

No issued-at time window. A member's card should work as long as their
coverage does. Freshness is handled by the rolling-key-version window, not
by an arbitrary `issuedAt + 24h` check.

1. Parse `{canonical}.{sig}`; reject malformed → `CARD_PAYLOAD_MALFORMED`.
2. Verify `keyVersion` is in `AcceptedKeyVersions`; else
   `CARD_SIGNATURE_STALE`.
3. Verify HMAC with the key for that version; else `CARD_SIGNATURE_INVALID`.
4. Cross-check `payload.TenantId` against the request's tenant header.
5. Look up `IdCardRecord` by `cardId`; missing → `CARD_NOT_FOUND`.
6. If `RevokedAt` is set → `410 Gone` with `CARD_REVOKED`.
7. Look up coverage at scan time; not active → `409 Conflict` with
   `COVERAGE_INACTIVE`.
8. Increment scan counters, request a 271 snapshot from `eligibility-service`,
   return `QrScanResponse`.

### Rate limiting

ASP.NET `RateLimiter` policy `card-scan`, partitioned on a composite key:

```
t:{tenantId}|p:{providerId}|c:{cardId}
```

Thresholds are the minimum of the three configured per-minute values so any
single dimension tripping a threshold blocks the request.

### Provider JWT

`[Authorize(Policy = "ProviderJwt")]`. Production wires a real JwtBearer
handler via `ProviderJwt:Authority` + `Audience`. When the authority is not
configured (dev/test), a `DevProviderAuthHandler` accepts all requests and
stamps an `X-Provider-Id` (or `"dev-provider"`) into the principal so rate
limiting by provider still has a non-empty partition key.

## Template resolution

```
(sponsorId, planId, language) ?     → IdCardTemplate
  └─ specific match (if language supported)
  └─ sponsor-default (if language supported)
  └─ global-default (tenant)
  └─ null (deployment error — surfaced by GlobalTemplateHealthCheck)
```

Phase 1 policy: every tenant must have a global default template. The
`GlobalTemplateHealthCheck` is registered on the `ready` tag so a missing
global default fails the readiness probe and blocks the rollout. At
runtime, a missing global default surfaces as an order with
`FailureCode = NO_TEMPLATE_AVAILABLE` so the operator sees a clear error
rather than a generic 500.

Templates are seeded during tenant onboarding (Phase 2 will add a
template-admin UI).

## Revocation

`POST /api/v1/id-cards/{cardId}/revoke` with `RevocationReason` enum:
`Replaced`, `Lost`, `Compromised`, `CoverageEnded`, `Administrative`.

Revoked cards return `410 Gone` with `CARD_REVOKED` on the next scan
regardless of coverage state.

## QNXT mirror reconciliation

`QnxtMirrorReconciliationJob` is a `BackgroundService` that wakes up every
`IdCard:Reconciliation:IntervalHours` (default 24) and re-enqueues any
non-revoked card issued in the last interval plus a 6-hour safety margin.
The initial run is delayed by 5 minutes to avoid saturating startup.

This is the backstop for the best-effort fire-and-forget mirror enqueue in
`QnxtIdCardAdapter`. If the Service Bus was down during a burst of card
issuances, the reconciliation pass will ensure QNXT eventually sees them.

## Portal

`MemberDetailsDialog` gets a new `ID Cards` tab rendering a `MudTable` of
`IdCardHistoryView` rows. Row actions: download PDF, download PNG preview,
revoke (with confirmation message box). The "Order New Card" button opens
`IdCardOrderDialog`, a 3-step wizard (member + channel + language → review
→ result). Wallet / Physical channels are visible but disabled in Phase 1.

## Storage

Mirrors `eligibility-service`: auto-detects MongoDB (`MongoDb:ConnectionString`),
falls back to Cosmos (`CosmosDb:ConnectionString`), falls back to in-memory
(dev only). Collections / containers:

- `idcard_orders` (tenant-partitioned, `id` key)
- `idcard_records` (tenant+card unique index, tenant+member history index)
- `idcard_templates` (tenant-partitioned)

## Phase-2 backlog

- `.pkpass` (Apple Wallet) and Google Wallet object generation.
- `FulfillmentVendorAdapter` physical-card mail vendor integration.
- Bulk re-issuance (e.g. on plan-wide change).
- Template admin UI in the portal.
- Per-card HMAC key pinning for compromise response (revoke-by-key).

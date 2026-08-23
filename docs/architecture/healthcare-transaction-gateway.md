# Healthcare Transaction Gateway

The healthcare transaction gateway is Cloud Health Office's vendor-neutral
abstraction for external payer / clearinghouse connectivity. One capability
model, many interchangeable vendor implementations, living in
[`CloudHealthOffice.Infrastructure.Gateways`](../../src/services/shared/CloudHealthOffice.Infrastructure/Gateways/).

This is the **foundation** layer. The mock gateway, the Stedi eligibility
(270/271) adapter, and the canonical payer reference service are implemented
today. Additional transactions (837, 276/277, 275, 835) stay contract-only.

## Where the boundary sits

```
                        CloudHealthOffice
                              |
                 Healthcare Transaction Gateway
                              |
              +---------------+---------------+
              |               |               |
            Stedi          Availity         Direct
                                        (payer / X12 / FHIR)
```

**Cloud Health Office owns the business.** Eligibility and benefit
interpretation, member coverage, provider / network logic, claims
adjudication, pricing, and accumulators all stay in CHO domain services. None
of that moves into a gateway.

**A gateway owns transport and translation only.** It carries a HIPAA/X12
transaction to an external system and translates the vendor's response back
into a CHO canonical model. It makes no coverage or adjudication decisions.

## Canonical models never leak vendor shapes

Request and response flow through CHO canonical models. A vendor DTO or raw
X12 payload never crosses the gateway boundary into a domain service.

```
CHO GatewayEligibilityRequest
        |
   IEligibilityGateway
        |
   Vendor adapter (translate)
        |
   Stedi / Availity / X12 / FHIR
```

```
   Vendor response
        |
   Vendor adapter (normalize)
        |
CHO GatewayEligibilityResponse
```

The canonical models live in
[`Gateways/Models`](../../src/services/shared/CloudHealthOffice.Infrastructure/Gateways/Models/):
`GatewayEligibilityRequest` and `GatewayEligibilityResponse`. They reference
only BCL and CHO types — a guard test
(`GatewayVendorNeutralityTests`) fails the build if any vendor name appears in
the abstraction.

## Capabilities are explicit, not faked

Not every gateway implements every transaction. Each gateway advertises the
subset it actually supports via `IHealthcareTransactionGateway.Capabilities`
and implements the matching capability-specific interface. Unsupported
transactions are **rejected explicitly** rather than returning a no-op result.

| Capability | Interface | Transaction | Status |
|------------|-----------|-------------|--------|
| `Eligibility` | `IEligibilityGateway` | 270/271 | **Implemented (Mock + Stedi)** |
| `ClaimSubmission` | `IClaimSubmissionGateway` | 837P/837I/837D | Contract only |
| `ClaimStatus` | `IClaimStatusGateway` | 276/277 | Contract only |
| `ClaimAcknowledgment` | `IClaimAcknowledgmentGateway` | 277CA | Contract only |
| `ClaimAttachment` | `IClaimAttachmentGateway` | 275 | Contract only |
| `Remittance` | `IRemittanceGateway` | 835 | Contract only |

### Per-gateway implementation status

This matrix is Cloud Health Office's **implementation** status — it is not
everything a vendor supports. Stedi's own API offers claim status, remittance,
and more; Cloud Health Office simply does not implement those capabilities yet,
so those gateways do not advertise them.

| Gateway | Eligibility (270/271) | 837 | 276/277 | 277CA | 275 | 835 |
|---------|:---:|:---:|:---:|:---:|:---:|:---:|
| Mock  | Yes | No | No | No | No | No |
| Stedi | Yes | No | No | No | No | No |

The "contract only" interfaces are intentionally member-less: they let a
gateway advertise a future capability and let callers discover it, without a
stub method that pretends to work. Adding a real transaction later means adding
its method and canonical models — no change to the capability enum wiring
beyond the single `GatewayCapabilityMap` entry.

### Discovering and rejecting capabilities

```csharp
// Resolve the configured default gateway (or one by name).
var gateway = resolver.Resolve();               // e.g. the Mock gateway

// Discover a capability.
if (gateway.Supports(GatewayCapability.Eligibility)) { /* ... */ }

// Resolve typed to a capability — throws GatewayCapabilityNotSupportedException
// when the gateway does not support it.
var eligibility = resolver.ResolveCapability<IEligibilityGateway>();
var result = await eligibility.CheckEligibilityAsync(request, ct);
```

## Transaction metadata (non-PHI)

Every transaction returns a `GatewayResponse<T>` that pairs the canonical
result with `GatewayTransactionMetadata`: gateway name, transaction type,
submitted / completed timestamps, status, external transaction id, correlation
id, tenant id, latency, retry count, and error category.

This metadata is deliberately PHI-free so it can go straight into structured
logs, metrics, and audit records. **Raw request/response payloads are never
logged.** The mock gateway logs only metadata, and
`GatewayPhiLoggingTests` enforces that subscriber identifiers, names, and dates
of birth never reach the log sink.

## Configuration

Bound from the `HealthcareTransactions` section into
`HealthcareTransactionOptions`:

```yaml
HealthcareTransactions:
  DefaultGateway: Mock
  Gateways:
    Stedi:
      BaseUrl: https://healthcare.us.stedi.com/...
      ApiKey: ""          # supplied by the secret provider / Key Vault, never source control
      Environment: sandbox
```

Only `DefaultGateway` is required today. The per-gateway map is prepared for
future vendors; secrets (`ApiKey`) flow through the existing secret provider /
Azure Key Vault configuration layering — no credentials are committed.

## Dependency injection

Registration follows the existing `AddChoMessaging` convention. From a
service's `Program.cs`:

```csharp
builder.Services.AddChoHealthcareGateways(builder.Configuration);
```

This binds the options, registers the resolver, and registers the mock
gateway. Additional gateways register through the same extension without
touching the resolver:

```csharp
builder.Services.AddHealthcareGateway<StediHealthcareGateway>();
```

Callers depend on `IHealthcareGatewayResolver` (or a capability interface) via
constructor injection — there is no service-locator access to concrete
gateways.

## Relationship to the existing eligibility adapters

eligibility-service already has an internal `IEligibilityAdapter` factory
(CHO / Availity / Change Healthcare) for its own request path. The gateway
abstraction is the **shared, cross-service** transport layer that future
vendor connectivity (starting with Stedi) plugs into. This PR adds the
abstraction and wires the mock gateway into eligibility-service DI; the
existing adapter flow is unchanged.

## Stedi gateway

`StediHealthcareGateway` (`Gateways/Stedi/`) is the first real external gateway.
It implements `IEligibilityGateway` on top of Stedi's **real-time eligibility
(270/271) JSON API** — `POST /2024-04-01/change/medicalnetwork/eligibility/v3`
on `https://healthcare.us.stedi.com`. Stedi translates the JSON to X12 270,
sends it to the payer, and returns the 271 as JSON; Cloud Health Office never
generates raw X12.

### Transaction flow

```
CloudHealthOffice
        |
GatewayEligibilityRequest          (canonical, vendor-neutral)
        |
StediHealthcareGateway             (validate config, resolve payer, map)
        |
StediEligibilityRequestDto         (Stedi JSON)
        |
Stedi Healthcare API  ── 270 X12 ──►  payer network
        |                                    |
StediEligibilityResponseDto  ◄── 271 X12 ──  payer
        |
StediEligibilityMapper             (normalize)
        |
GatewayEligibilityResponse         (canonical, vendor-neutral)
        |
CloudHealthOffice
```

The Stedi request/response DTOs and the mapper are **internal** to the
infrastructure assembly — an architecture test fails the build if any of them
becomes public. Only `StediHealthcareGateway`, `StediGatewayOptions`, and the DI
extension are public.

### Subscriber vs patient

Canonical eligibility distinguishes the **subscriber** (policyholder / insured)
from the **patient** (person receiving services). They are the same person for
self coverage and different people for a dependent inquiry.

```
Subscriber = insured / policyholder
Patient    = person receiving services
```

Existing flat fields (`SubscriberId`, `SubscriberFirstName`, …) remain valid.
New callers should set `Subscriber` and, when needed, `Patient` as
`GatewayEligibilityPerson`. `MemberId` on the request is **not** a dependent
inquiry by itself — populate `Patient` for that.

#### Subscriber inquiry

```
Provider
   ↓
CHO
   ↓
Subscriber eligibility request
   ↓
Stedi subscriber object (no dependents[])
   ↓
271
```

#### Dependent inquiry

```
Provider
   ↓
CHO
   ↓
Subscriber + Patient
   ↓
Stedi subscriber + dependents[]
   ↓
271
```

CHO `Patient` maps to Stedi `dependents[]` (at most one). Stedi's request
schema does not require a relationship on `dependents[]`; CHO only sends
first name, last name, DOB, and an optional dependent member id. Payer-returned
`relationToSubscriber` is preserved on the canonical response `Patient`.
Date of birth is `YYYYMMDD`. The subscriber member id stays on Stedi
`subscriber.memberId`.

Verified sandbox evidence (Stedi `applicationMode = test`):

```
Trading partner 87726 (UnitedHealthcare)
Subscriber John Doe / UHC202649
Dependent  Jane Doe / DOB 1952-11-21
Service type 30
→ HTTP 200, Active Coverage, benefits present
```

A subscriber-only request for that same UHC fixture returns AAA 73
(Invalid/Missing Subscriber/Insured Name) — a payer business rejection, not a
transport failure (`GatewayTransactionStatus.Rejected` /
`GatewayErrorCategory.PayerRejected`).

### Architectural boundary

Stedi = network / transport / transaction translation.
Cloud Health Office = healthcare business logic.

A 271 response is an **external payer eligibility statement**, not a Cloud Health
Office calculation. The gateway surfaces it as a normalized eligibility context;
prospective benefits, cost estimates, and adjudication remain separate Cloud
Health Office steps downstream. The gateway never applies benefits, computes
accumulators, or adjudicates.

### Payer identifier mapping

Payer identity is a first-class Cloud Health Office platform concept, not a
Stedi configuration map. Canonical payer records live in
[`ReferenceData/Payers`](../../src/services/shared/CloudHealthOffice.Infrastructure/ReferenceData/Payers/)
and are reused across eligibility, and later claims, attachments, claim status,
and remittance.

```
                  CloudHealthOffice
                         |
                  Canonical Payer
                         |
                 Payer Reference Data
                    /           \
                   /             \
             Stedi ID         Other IDs
                |                |
                v                v
              Stedi          Availity/etc.
```

Three distinct concepts:

| Concept | What it answers |
|---------|-----------------|
| **Gateway capability** | Does this CHO gateway implementation send 270/271 (or 837, …)? |
| **Payer capability** | Does *this payer/network* support that transaction? |
| **Tenant enrollment/configuration** | Has *this tenant* completed any required enrollment, and do they override routing? |

A transaction is attempted only when the gateway implements it **and** the
payer supports it **and** tenant enrollment (when required) is complete.

```
GatewayEligibilityRequest.PayerId
        |
        v
IPayerReferenceService
        |
        v
Canonical payer
        |
        v
External identifier
  System = "stedi"
  Type   = "tradingPartnerServiceId"
        |
        v
StediHealthcareGateway
```

Resolution is exact-match only (canonical id, alias, or external identifier
value). Multiple matches return `AmbiguousPayer`; zero matches return
`PayerNotFound`. Arbitrary payer ids are **never** passed through to Stedi.

Hand-maintained `PayerMap` / `TenantPayerMap` remain as a **deprecated
fallback** for environments that have not yet synchronized a directory. They
are not the primary architecture. `TenantPayerMap` is still consulted only for
the requesting tenant.

### Payer directory synchronization

Stedi's List Payers JSON API is the source of directory data:

```
GET https://payers.us.stedi.com/2024-04-01/payers?pageSize=100
Authorization: <api-key>
```

API version: **2024-04-01**. Pagination request query parameters are `pageSize`
(minimum 10) and `pageToken`. The response field `nextPageToken` is supplied as
the next request's `pageToken`. The mapper copies only fields Stedi documents (stediId,
displayName, primaryPayerId, aliases, names, transactionSupport, enrollment
process metadata, coverage types, etc.). Stedi transport DTOs stay internal.

Synchronization is opt-in (`PayerReference:Sync:Enabled`, default `false`) so
hosts start without live Stedi credentials. When enabled, a hosted service
refreshes on a configurable interval (default 24h) and optionally on startup.
An on-demand `POST /api/payer-references/sync` is available in Development
(or when `AllowOnDemandSync` is true).

Records are stored in the configured `PayerReference:Store` (`InMemory` by
default; `Mongo` when an `IMongoClient` is registered). CI uses deterministic
synthetic seed payers — no live Stedi credentials required.

Payers present in a previous Stedi sync but missing from the latest run are
**disabled**, not deleted. Seed records (`Source = seed`) are left untouched.

### Tenant overlays

Global payer records are shared. A `PayerTenantOverride` keyed by
`(tenantId, payerId)` may:

- enable/disable the payer for that tenant
- supply a preferred alias
- replace the clearinghouse identifier
- record which transactions the tenant has enrolled

One tenant cannot read or modify another tenant's overlays.

### Configuration (no secrets in source control)

```yaml
HealthcareTransactions:
  DefaultGateway: Stedi          # or Mock
  Gateways:
    Stedi:
      BaseUrl: https://healthcare.us.stedi.com
      ApiKey: ""                 # supply via env var or secret provider / Key Vault
      Environment: sandbox       # sandbox | test | production
      PayerDirectoryBaseUrl: https://payers.us.stedi.com
      PayerDirectoryPath: /2024-04-01/payers
      PayerMap:                  # deprecated fallback only
        AETNA: "60054"
      TenantPayerMap:            # deprecated fallback; tenant-scoped
        tenant-alpha:
          AETNA: "60055"

PayerReference:
  Store: InMemory                # InMemory | Mongo
  SeedSyntheticPayers: true      # CI/dev seed; no live Stedi required
  Sync:
    Enabled: false               # opt-in; application starts without Stedi
    OnStartup: false
    IntervalHours: 24
```

Supply the API key out of band, e.g.:

```
export HealthcareTransactions__Gateways__Stedi__ApiKey="<your-stedi-key>"
```

or through the existing Azure Key Vault / secret-provider layering. The key is
sent in the `Authorization` header per request and never appears in logs,
exceptions, telemetry, or checked-in configuration. If Stedi is selected but its
configuration is invalid, the Stedi gateway returns a `Configuration` error —
it never silently falls back to Mock.

### Resilience & error handling

The Stedi API client runs an explicit, configurable retry loop (default 2
retries) so the retry count can be recorded on `GatewayTransactionMetadata` and
so behaviour is deterministically testable. Transient failures (HTTP 429, 5xx,
network errors, timeouts) are retried with exponential backoff and honour
`Retry-After`; validation (400/422), authentication (401), authorization (403),
and payer business rejections are never retried. All failures map to the
vendor-neutral `GatewayErrorCategory`; no Stedi exception type escapes the
gateway.

### Choosing a gateway (Mock vs Stedi)

Selection is configuration-only — no code change and no caller awareness of
`StediHealthcareGateway`:

```yaml
HealthcareTransactions:
  DefaultGateway: Mock    # deterministic, offline
# DefaultGateway: Stedi   # real payer transaction (requires ApiKey)
```

In Development, eligibility-service exposes a dev-only demo endpoint
(`POST /api/gateway-demo/eligibility`) that runs a request through the
configured gateway, so the same request can be pointed at Mock or Stedi.

## Relationship to the existing eligibility adapters (consolidation path)

eligibility-service still has its internal `EligibilityAdapterFactory`
(`IEligibilityAdapter`: CHO / Availity / Change Healthcare) for its own request
path. That system and `IHealthcareGatewayResolver` currently **overlap** for
eligibility: the adapter factory routes per-tenant platform choices inside the
service, while the gateway resolver is the shared, cross-service transport layer.
This PR does not consolidate them (a broad refactor is out of scope and higher
risk). The intended future path is to have the CHO/Availity/Change Healthcare
eligibility adapters delegate to (or be replaced by) capability gateways behind
`IHealthcareGatewayResolver`, leaving one transport abstraction. That
consolidation should be its own PR.

## Next Stedi integration

Payer identity is now a shared platform service. Recommended next step:
**inbound payer-side 270/271 support** —

```
Stedi → CloudHealthOffice → eligibility/benefit logic → 271 → Stedi
```

so Cloud Health Office can act as the payer endpoint, not only as an outbound
eligibility client. Claim submission (837P) and the 277CA acknowledgment
lifecycle remain follow-on work after inbound eligibility.

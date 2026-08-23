# Healthcare Transaction Gateway

The healthcare transaction gateway is Cloud Health Office's vendor-neutral
abstraction for external payer / clearinghouse connectivity. One capability
model, many interchangeable vendor implementations, living in
[`CloudHealthOffice.Infrastructure.Gateways`](../../src/services/shared/CloudHealthOffice.Infrastructure/Gateways/).

This is the **foundation** layer. Only the mock gateway and the eligibility
(270/271) capability contract are implemented today; live vendor adapters
(Stedi first) are added in later PRs without architectural rework.

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
| `Eligibility` | `IEligibilityGateway` | 270/271 | **Implemented (mock)** |
| `ClaimSubmission` | `IClaimSubmissionGateway` | 837P/837I/837D | Contract only |
| `ClaimStatus` | `IClaimStatusGateway` | 276/277 | Contract only |
| `ClaimAcknowledgment` | `IClaimAcknowledgmentGateway` | 277CA | Contract only |
| `ClaimAttachment` | `IClaimAttachmentGateway` | 275 | Contract only |
| `Remittance` | `IRemittanceGateway` | 835 | Contract only |

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

## Follow-up: Stedi eligibility adapter

The next PR implements `StediHealthcareGateway : IEligibilityGateway`:
translate `GatewayEligibilityRequest` into Stedi's eligibility API request,
authenticate with the API key from Key Vault, POST to the configured
`BaseUrl`, and normalize the response into `GatewayEligibilityResponse`,
populating `GatewayTransactionMetadata` (external transaction id, latency,
retries). No change to the abstraction is required.

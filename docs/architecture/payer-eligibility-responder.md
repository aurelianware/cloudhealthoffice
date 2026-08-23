# Payer-side eligibility responder

Cloud Health Office can act as the **payer / information source** for an
inbound eligibility inquiry (270-equivalent) and produce a 271-equivalent
response from existing member, coverage, benefit, network, and accumulator
data.

This is the opposite of the outbound healthcare transaction gateway:

```
EligibilityInquiryClient / IEligibilityGateway
    CHO → external payer

IEligibilityResponder
    external provider → CHO
```

`IEligibilityGateway` is not overloaded with inbound responsibility.

## Stedi capability finding

**Stedi does not currently expose a supported public API for a core-administration
platform to receive inbound 270 transactions and return 271 responses.**

Reviewed 2026-08-23 against:

| Source | What it covers |
|--------|----------------|
| [Stedi Healthcare developer docs](https://www.stedi.com/docs/healthcare) | Provider-side eligibility, claims, claim status, 277CA, ERA |
| [Submit eligibility checks](https://www.stedi.com/docs/healthcare/send-eligibility-checks) | `POST /2024-04-01/change/medicalnetwork/eligibility/v3` — **send** 270 JSON/X12 **to** a payer |
| [Eligibility overview](https://www.stedi.com/docs/healthcare/intro-eligibility-checks) | Stedi routes a provider's 270 to an existing payer and returns the payer's 271 |
| [Retrieve Payer](https://www.stedi.com/docs/healthcare/api-reference/get-payer) | `transactionSupport.eligibilityCheck` = whether **you can send** 270s to that payer |
| [Claim responses / webhooks](https://www.stedi.com/docs/healthcare/claim-responses-overview) | Inbound 277CA and 835 ERA after **you** submitted an 837; enrollment events |
| [About Stedi (provider docs)](https://www.stedi.com/docs/providers/providers-about-stedi) | Stedi is an intermediary between providers and insurance payers |

There is no documented mechanism to:

- register Cloud Health Office as a payer / trading partner that **receives** 270s
- receive normalized JSON eligibility **requests** as the information source
- expose a webhook/callback that Stedi invokes with an inbound 270
- configure routing for a custom `tradingPartnerServiceId` that points at a CHO endpoint

Stedi inbound 270 routing is therefore:

```
Adapter-ready / pending Stedi payer-side connectivity
```

not

```
Implemented
```

A real Stedi inbound transaction was **not** executed for this capability.

## Architecture

Path B — vendor-neutral responder with a planned Stedi adapter seam:

```
             Inbound Transaction Adapter
                /          |          \
          Stedi*        Direct X12    Canonical API
            |              |              |
            +--------------+--------------+
                           |
                           v
                 PayerEligibilityInquiry
                           |
                           v
                  IEligibilityResponder
                           |
                           v
         CHO Member / Coverage / Benefits / Network / Accumulators
                           |
                           v
                 PayerEligibilityResponse
                           |
                           v
                    Outbound Adapter
```

`Stedi*` is **planned**. `StediInboundEligibilityAdapter.IsImplemented` is
`false` and throws if invoked.

```
Inbound Adapter
      ↓
EligibilityInquiry (PayerEligibilityInquiry)
      ↓
IEligibilityResponder
      ↓
CHO Member / Coverage / Benefits / Network / Accumulators
      ↓
EligibilityResponse (PayerEligibilityResponse)
      ↓
Outbound Adapter
```

Canonical models live in
[`Responders/Models`](../../src/services/shared/CloudHealthOffice.Infrastructure/Responders/Models/).
They reuse `GatewayEligibilityPerson` for subscriber vs patient and contain no
Stedi-specific fields.

## Outbound vs inbound

### Outbound / client mode (implemented, including Stedi)

```
CHO
 ↓
Stedi
 ↓
External Payer
```

### Inbound / payer mode (implemented against CHO; Stedi routing pending)

```
Provider
 ↓
Network / Clearinghouse
 ↓
CHO
```

### Combined vision (CloudDentalOffice later; not in this PR)

```
CloudDentalOffice
      |
      | 270
      v
    Stedi
      |
      v
CloudHealthOffice
      |
      | 271
      v
    Stedi
      |
      v
CloudDentalOffice
```

CDO does not need a CHO-only business protocol once a clearinghouse can
route 270/271. Until Stedi (or another network) offers payer-side inbound
routing, CHO is exercised through the canonical development API and the
optional X12 adapter.

## Processing pipeline

The responder is read-only.

```
Inbound 270-equivalent request
        ↓
validate transaction
        ↓
resolve payer tenant          (trusted identifiers only)
        ↓
resolve subscriber/member     (exact match)
        ↓
resolve patient/dependent     (must belong to the subscriber)
        ↓
resolve coverage              (effective / termination vs date of service)
        ↓
resolve plan
        ↓
resolve provider/network      (informative if unknown)
        ↓
read accumulators             (no writes)
        ↓
evaluate requested service types
        ↓
build canonical eligibility response
```

A 270 inquiry must not mutate claims, accumulators, authorization state,
payment state, enrollment, coverage, or visit counters. Tests assert the
directory mutation probe stays at zero.

## CHO business logic reused

The responder does not implement a second eligibility engine. It reads:

| Concern | Seam | Development backing | Production backing |
|---------|------|---------------------|--------------------|
| Member / subscriber / dependent | `IPayerEligibilityDirectory.FindSubscriberAsync` / `FindDependentAsync` | In-memory CHO Demo seed | member-service |
| Coverage dates / plan enrollment | `GetCoverageAsync` | In-memory coverages | coverage-service |
| Benefit plan / copay / coinsurance / STC | `GetPlanAsync` | In-memory Demo PPO | benefit-plan-service / benefit engine |
| Accumulators (deductible, OOP remaining) | `GetAccumulatorsAsync` | In-memory snapshot | accumulator-service (GET only) |
| Provider / network | `FindProviderAsync` | In-memory NPIs | provider-service |
| Payer / tenant routing | `IPayerEligibilityRouter` + inbound routes | Seeded routes | configured endpoint identity + payer reference |

`InMemoryPayerEligibilityDirectory` is the Development / test projection of
those same concepts (analogous to `MockHealthcareGateway` on the outbound
path). A host that already has live member/coverage/benefit services
registers an `IPayerEligibilityDirectory` that performs HTTP GETs against
them — the responder code does not change.

## Routing

Incoming transactions never trust `ClaimedTenantId`.

```
external payer / network identifier
        ↓
configured inbound route (payer id, trading partner id, or authenticated endpoint)
        ↓
CHO tenant + canonical payer
```

Unknown and ambiguous matches fail explicitly (`InvalidPayer` /
`AmbiguousPayer`). The router never guesses.

## Subscriber vs patient

Same canonical distinction as outbound eligibility (#1109):

```
Subscriber = policyholder / insured
Patient    = person receiving services
```

Self: `Subscriber == Patient`. Dependent: the dependent must exist on that
subscriber's coverage. Lookup is exact (member id, or first + last + DOB).
Ambiguous matches return no member payload.

## Transport vs business outcome

```
HTTP 200
+
invalid subscriber
=
successful transport
+
payer business rejection
```

`PayerEligibilityResponse` carries `TransportStatus`, `BusinessStatus`, and
`CoverageStatus` separately. `GatewayResponse.IsSuccess` is transport
success. X12 AAA codes are mapped only in
`X12PayerEligibilityMapper.ToAaaCode`.

## Development ingress

```
POST /api/dev/payer/eligibility
```

Canonical JSON in, canonical JSON out. **404 outside Development.**

```
POST /api/dev/payer/eligibility/x12
```

Raw X12 270 in, raw X12 271 out, using the existing `Edi270Parser` /
`Edi271Generator`. Does **not** call `ProcessInquiryAsync` (which persists
inquiries). 404 outside Development.

Production-capable ingress is not exposed. A future network adapter must use
API key, OAuth2, mTLS, or an equivalent clearinghouse-authenticated
connection.

## FHIR / Da Vinci

Canonical models are conceptually compatible with future
`CoverageEligibilityRequest` / `CoverageEligibilityResponse` mapping. FHIR
SDK types are not introduced into the responder. Mapping belongs in a FHIR
adapter.

## Future Stedi seam

When Stedi (or a Stedi partnership) documents inbound 270 delivery:

1. Implement translation in `StediInboundEligibilityAdapter` (currently
   planned-only).
2. Keep `IEligibilityResponder` and the canonical models unchanged.
3. Record a **separate** evidence artifact for a real Stedi inbound
   transaction. Do not update this document's "Implemented" label without
   that evidence.

Recommended next PR after this capability is stable:

```
837P / 837I / 837D claim submission through Stedi
```

Inbound payer-side claim **receipt** (837 to CHO as payer) remains a
separate capability.

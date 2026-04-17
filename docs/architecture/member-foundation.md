# Member Foundation (5.1)

Elevates `Member` from an 834 enrollment record to a full FHIR R4 **Patient**
operational projection, with typed identifiers, an append-only event stream,
and downstream-service fan-out via typed clients.

## Scope of this PR
- Extended `Member` model (FHIR-aligned demographics + identifiers + communication).
- Append-only `member-events` stream (Cosmos container / Mongo collection).
- `IMemberEventPublisher` (decorator-friendly so a future bus publisher can be
  layered in without touching call sites).
- `IFhirPatientProjector` projecting `Member` → FHIR R4 Patient (hand-built
  JSON, no `Hl7.Fhir.R4` dependency).
- `IdentifiersController` for typed identifier management.
- Typed downstream HTTP clients (coverage-service, enrollment-import-service,
  accumulator-service) with 503 Problem Detail when unconfigured and dev-only
  fakes gated by `IHostEnvironment.IsDevelopment()`.

## Component diagram

```mermaid
flowchart LR
  subgraph Client
    Portal[Portal / 834 import]
  end

  Portal -->|HTTP| MC[MembersController]
  Portal -->|HTTP| IC[IdentifiersController]

  subgraph member-service
    MC -->|IMemberRepository| MR[(Cosmos/Mongo Members)]
    MC -->|IMemberEventPublisher| MEP
    MC -->|IFhirPatientProjector| FP
    MC -->|ICoverageServiceClient| Cov[[coverage-service]]
    MC -->|IEnrollmentImportServiceClient| Enr[[enrollment-import-service]]
    MC -->|IAccumulatorServiceClient| Acc[[accumulator-service]]

    IC -->|IMemberRepository| MR
    IC -->|IIdentifierEncryptor| KV[(Azure Key Vault)]
    IC -->|IMemberEventPublisher| MEP

    MEP[CosmosMemberEventPublisher] -->|IMemberEventRepository| ME[(Cosmos member-events)]
    FP[FhirPatientProjector] -->|JsonObject| MC
  end

  ME -.->|Change Feed (future PR)| Bus[(Service Bus fan-out)]
```

## Data model additions

| Field | Type | Notes |
|---|---|---|
| `Identifiers` | `List<MemberIdentifier>` | Typed identifiers. PII values (SSN/MBI/Medicaid) stored ciphertext; `IsEncrypted=true`. |
| `PreferredLanguage` | `string?` (BCP-47) | Projects to `Patient.communication[preferred=true]`. |
| `Languages` | `List<string>` | Additional BCP-47 languages. |
| `Race` / `RaceDetail` | `CodedConcept?` / `List<CodedConcept>` | us-core-race ombCategory + detailed. |
| `Ethnicity` / `EthnicityDetail` | ditto | us-core-ethnicity. |
| `GenderIdentity` | `CodedConcept?` | us-core-genderIdentity. |
| `Pronouns` | `string?` | `individual-pronouns` extension. |
| `MaritalStatus` | `CodedConcept?` | v3 MaritalStatus. |
| `Deceased` / `DeceasedDate` | `bool` / `DateTime?` | `deceasedBoolean` vs `deceasedDateTime`. |
| `BirthSex` | `string?` (M/F/UNK) | us-core-birthsex. |
| `CommunicationPreferences` | `List<CommunicationPreference>` | Channel + window + opt-in. |

## Identifier system URIs (`FhirIdentifierSystems`)

| Type | System |
|---|---|
| `MemberId` | `urn:cho:member-id` |
| `SSN` | `http://hl7.org/fhir/sid/us-ssn` |
| `MedicareMbi` | `http://hl7.org/fhir/sid/us-mbi` |
| `Medicaid` | `http://hl7.org/fhir/sid/us-medicaid` |
| `Exchange` | `urn:cho:exchange-id` |
| `Portal` | `urn:cho:portal-id` |
| `Legacy` | `urn:cho:legacy:{slug}` (slug is tenant-configured) |

## Event stream

### Envelope

```jsonc
{
  "id": "evt-uuid",                 // Cosmos doc id, defaults to eventId
  "partitionKey": "{tenantId}:{memberId}",
  "tenantId": "tenant-1",
  "memberId": "M-001",
  "eventId": "evt-uuid",            // client-supplied, unique key for idempotency
  "eventType": "MemberCreated",
  "version": 1,                     // monotonically increasing per aggregate
  "schemaVersion": 1,
  "occurredAt": "2026-04-17T15:00:00Z",
  "actorId": "user@tenant",
  "correlationId": "trace-abc",
  "payload": { "...": "see below" }
}
```

- **Cosmos container**: `member-events`, partition key path `/partitionKey`.
- **Mongo collection**: `member-events` with unique compound indexes on
  `(tenantId, memberId, eventId)` and `(tenantId, memberId, version)`.
- **Idempotency**: duplicate writes with the same `eventId` are no-ops.
  Callers may safely retry.
- **Ordering**: `version` is assigned server-side by the publisher
  (`max(version)+1` per aggregate). Concurrent writers conflict on the
  unique index and retry.
- **Genesis rule**: `MemberCreated` payloads MUST contain the full member
  snapshot so projections can be rebuilt from the stream without
  special-casing. Subsequent events (`MemberUpdated`, `AddressChanged`,
  `PcpChanged`, `MemberTerminated`) SHOULD contain diffs of changed fields.

### Event types

| Type | Trigger | Payload |
|---|---|---|
| `MemberCreated` | `POST /members` | Full member snapshot (genesis). |
| `MemberUpdated` | `PUT /members/{id}` (any field change) | Diff of changed fields. |
| `AddressChanged` | `PUT /members/{id}` with address fields | Diff of address fields. |
| `MemberTerminated` | `DELETE /members/{id}` or `POST /terminate` | `{ terminationDate, reasonCode }` |
| `PcpChanged` | `PUT /members/{id}/pcp` | `{ providerId, effectiveDate, reason }` |

## API contract

| Method | Path | Response |
|---|---|---|
| GET | `/api/v1/members` | List (paged). |
| GET | `/api/v1/members/{memberId}` | `Member` or 404. |
| POST | `/api/v1/members` | 201 + `Location` header; emits `MemberCreated`. |
| PUT | `/api/v1/members/{memberId}` | 200 / 404; emits `MemberUpdated` (+ `AddressChanged` if applicable). |
| DELETE | `/api/v1/members/{memberId}` | 204 / 404; emits `MemberTerminated`. |
| POST | `/api/v1/members/{memberId}/terminate` | 200 / 404 / 503. |
| GET | `/api/v1/members/{memberId}/eligibility` | `EligibilityCheckResponse`. |
| **GET** | **`/api/v1/members/{memberId}/fhir`** | **`application/fhir+json` R4 Patient.** |
| **GET** | **`/api/v1/members/{memberId}/events`** | **List of `MemberEvent`, ordered by version.** |
| GET | `/api/v1/members/{memberId}/pcp` | `MemberPcpResponse` / 503 (coverage-service). |
| PUT | `/api/v1/members/{memberId}/pcp` | 200 / 503; emits `PcpChanged`. |
| GET | `/api/v1/members/{memberId}/coverage-history` | 200 / 503 (coverage-service). |
| GET | `/api/v1/members/{memberId}/834-transactions` | 200 / 503 (enrollment-import-service). |
| GET | `/api/v1/members/{memberId}/accumulators` | 200 / 503 (accumulator-service). |
| GET | `/api/v1/members/{memberId}/dependents` | `List<Member>`. |
| GET | `/api/v1/members/{memberId}/identifiers` | `List<IdentifierResponse>` (PII redacted). |
| POST | `/api/v1/members/{memberId}/identifiers` | 201 / 400 / 404 / 409; emits `MemberUpdated`. |
| DELETE | `/api/v1/members/{memberId}/identifiers?system=&value=` | 204 / 404. |

### 503 contract (downstream unavailable)

When `Downstream:{Service}:BaseUrl` is not configured (or the downstream is
unreachable), endpoints that depend on it return RFC 7807 ProblemDetails:

```json
{
  "type": "https://cloudhealthoffice.com/problems/downstream-unavailable",
  "title": "Downstream service unavailable",
  "status": 503,
  "detail": "Configure Downstream:CoverageService:BaseUrl to enable coverage integrations.",
  "service": "coverage-service"
}
```

In **development** only (`IHostEnvironment.IsDevelopment()`), unconfigured
downstreams fall back to `Fake*Client` stand-ins (not used in production).

## PII encryption

- Field-level AES-256-GCM on PII identifier values (`SSN`, `Medicaid`,
  `MedicareMbi`).
- Data encryption key is sourced from Azure Key Vault via the shared
  `ISecretProvider` abstraction (`Member:IdentifierEncryption:KeySecretName`).
- Ciphertext envelope: `[version:1][nonce:12][tag:16][ciphertext]`,
  base64url-encoded.
- `Member.Identifiers[i].IsEncrypted=true` marks ciphertext values. The FHIR
  projector redacts encrypted values to `[REDACTED]`; `IdentifiersController`
  decrypts by the `IIdentifierEncryptor` when matching for removal.
- Dev fallback is the `NoOpIdentifierEncryptor`; Program.cs **throws at
  startup** in non-development environments when the key secret name is
  unset.

## Cosmos provisioning

- `Members` container — PK `/tenantId` (unchanged).
- `member-events` container — PK `/partitionKey`
  (`{tenantId}:{memberId}`) so per-member streams remain co-located for
  future Change Feed consumers. **Unique-key policy on `/version`** so
  concurrent writers to the same `(tenantId, memberId)` collide at the
  index level; the Cosmos repository converts the resulting HTTP 409 with
  `SubStatusCode=1009` into a version retry
  (`CosmosMemberEventPublisher`, backoff 2/5/25/100/250 ms, max 5
  attempts, then `ConcurrencyException`).

### Provisioning script

See `scripts/cosmos/provision-member-events.sh`. Usage:

```bash
scripts/cosmos/provision-member-events.sh \
  --account my-cosmos \
  --resource-group rg-cho \
  --database CloudHealthOffice
```

### Sample `az` command (runbook-inline copy)

```bash
az cosmosdb sql container create \
  --account-name my-cosmos \
  --resource-group rg-cho \
  --database-name CloudHealthOffice \
  --name member-events \
  --partition-key-path "/partitionKey" \
  --unique-key-policy '{"uniqueKeys":[{"paths":["/version"]}]}' \
  --throughput 400
```

### Migration

Unique-key policies are immutable after container creation. For **dev /
staging**: drop and recreate the container (data loss acceptable). For
**prod**: create a new container with the policy, copy documents via Data
Migration Tool or a Change-Feed pump, swap reads/writes, then delete the
old container.

### PII dedupe (HMAC fingerprint)

PII identifier values (SSN, MBI, Medicaid) are encrypted with AES-GCM, so
two writes of the same plaintext produce different ciphertexts and can't
be compared by `Value`. To catch duplicates, the controller computes an
HMAC-SHA256 fingerprint of the NORMALIZED plaintext (dashes, spaces,
parentheses stripped; uppercased) before encryption, and stores it in
`MemberIdentifier.ValueFingerprint`.

- Fingerprint HMAC key is a **distinct** Key Vault secret
  (`Encryption:IdentifierFingerprintKeySecret`) — not reused from the AES
  data key — so the two secrets can rotate independently.
- `IdentifierNormalization.Normalize` is the only normalizer; both
  `Add` and `Remove` flows call it.
- Dedupe scope is `(system, fingerprint)` within a member.
- Non-PII identifiers skip fingerprinting and dedupe by `Value`.

## Future work (explicitly out of scope for this PR)

- Cosmos Change Feed → Service Bus fan-out (next PR).
- `ServiceBusMemberEventPublisher` decorator layered onto
  `CosmosMemberEventPublisher`.
- Change Feed subscriber that materializes a `CurrentPcpSnapshot` read-through
  cache on `Member` from `PcpChanged` events.
- Full `UpdateMember` surface for the new demographic fields (currently the
  controller accepts the core subset; race/ethnicity/communication are
  populated through `POST /members` or the future terminology-service sync).

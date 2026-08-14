# Reference Data Foundation

CloudHealthOffice uses one canonical, transport-neutral model for healthcare
reference codes. Native APIs and future FHIR/Da Vinci adapters consume that
model; neither repository nor pricing logic depends on FHIR SDK classes.

```text
Official / Licensed Sources
          │
          ▼
Canonical Reference Data
          │
     ┌────┴────┐
     │         │
     ▼         ▼
Native API   FHIR Coding
```

## Domain boundaries

- A reference code identifies a code system, code, version and optional text.
- A fee schedule associates a procedure code with a decimal rate, effective
  dates, payer/contract context and provenance.
- Benefit rules own coverage and cost-sharing policy.
- Adjudication owns the resulting financial outcome.

These types must not be collapsed. `CloudHealthOffice.ReferenceData` has no
dependency on the fee schedule, benefit, adjudication, service, or FHIR layers.
The FeeScheduleEngine references only shared classifications needed for
provenance and continues to own all rate resolution.

## Coding and code systems

`ChoCoding` retains `CodeSystem`, optional verified `CodeSystemUri`, `Code`,
optional `Version`, and optional `Display`. A missing display or description is
valid—for example, a licensed CDT record may contain only `CDT` and `D2740`.

The registry records canonical URIs only where verified. An absent URI is
intentional and must not be replaced with an invented identifier. The
SDK-independent FHIR-compatible mapper demonstrates preservation of FHIR
`system`, `code`, `version`, and `display`; an API adapter may translate that
wire shape to its selected FHIR SDK.

## Versions, provenance, and tenancy

Reference records are immutable versions selected by effective/service date.
Historical and future records coexist. Imports carry source ID, source version,
import timestamp, and checksum; the repository treats a repeated source/version
checksum as idempotent. Global records are visible to every tenant, while
tenant-owned records require the matching tenant context.

Fee schedules retain their existing calculation types and now separately carry
source type, source/version, payer and network context, jurisdiction, effective
dates, code system, checksum, global/tenant ownership, and license class.

## Licensing and exposure

Licensing (`Public`, `Licensed`, `CustomerProvided`, `DevelopmentOnly`,
`Restricted`, `Unknown`) is distinct from exposure (`PublicReference`,
`AuthenticatedReference`, `TenantRestricted`, `InternalOnly`). Public exposure
is allowed only for public-licensed records. Unauthorized consumers may retain
the code identifier but display and description are redacted. Complete CDT or
CPT terminology is not stored in this foundation.

## Source lifecycle

Source acquisition and ingestion are explicit stages:

```text
Retrieve → Parse → Normalize → Validate → Preview → Import → Activate
```

Retrieval never activates data. `IReferenceDataSource` returns a versioned,
checksummed package; `IReferenceDataImporter` keeps each later stage independently
testable. Concrete public acquisition and service APIs belong to the subsequent
Reference Data Service change.

## Search contract

The repository supports exact code, code prefix, code/display/description text,
category, explicit version, effective date, active state, tenant isolation, and
bounded pagination (maximum 500 records per page).

## Reference Data Service integration

The existing `reference-data-service` hosts the durable canonical repository in
PostgreSQL alongside its legacy CPT, ICD-10, HCPCS, modifier, DRG, place-of-service,
and revenue-code tables. Legacy endpoints remain compatible; canonical clients use:

- `GET /api/reference-data/codes/{codeSystem}/{code}` for effective-date lookup.
- `GET /api/reference-data/codes` for versioned, tenant-aware search.
- `POST /api/reference-data/codes/import` for administrator-controlled imports.

Canonical imports are recorded in `canonical_reference_data_imports` and are
idempotent by source ID, source version, and checksum. Records are stored in
`canonical_reference_codes` using a normalized logical key. The service derives
tenant access from authenticated claims (or an authenticated service's
`X-Tenant-ID` header) and applies `ReferenceDataExposurePolicy` before returning
display or description text. Anonymous tenant headers do not grant access.

External retrieval and source-specific parsers remain a separate acquisition
concern; this service integration does not make retrieval activate data.

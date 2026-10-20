# ADR 020: Payer-to-Payer Member Data and Member Match From CHO's Own Services

## Status

Accepted. Extends [ADR 018](018-fhir-patient-coverage-from-cho-services.md),
which left the Payer-to-Payer member source on the mock.

## Context

Payer-to-Payer has two inbound operations:

- `PayerToPayer/$member-data-export` (P2P-01) resolves a member and sends the
  receiving payer the member's data;
- `Patient/$member-match` (P2P-04) resolves the member another payer describes
  and selects the coverage the match concerns.

Both read `MockPatientAccessDataProvider`: five hard-coded members and three
payments, registered in every environment and blind to tenant (gap-matrix T-4,
G-30). So the exchange that sends a member's records to another payer ran over
records that were never the plan's.

Two facts shape the replacement:

1. **Matching needs candidates, not the whole membership.** The mock handed
   `$member-match` every member and let `MemberMatchPolicy` decide. That cannot
   scale, and the replacement must still never miss a second strong candidate:
   a missed one turns an ambiguous request into a disclosure to the wrong
   member. The policy makes a candidate strong in three ways: by member or
   subscriber id, by SSN, or by family name plus birth date.
2. **SSN cannot be searched.** member-service stores SSNs encrypted, with no
   searchable index, so no CHO service can find a member by SSN.

## Decision

### 1. One more binding

`FhirAdapters:Sources:PayerToPayer` is `Mock` (the default) or `ChoNative`,
alongside the Patient and Coverage bindings of ADR 018. When it is
`ChoNative`, both inbound operations and outbound initiation resolve members
from member-service and coverage-service, and the export reads the claims from
claims-service. The Kubernetes manifest binds it; nothing else changes for a
`Mock` deployment. A `ChoNative` binding requires `Services:MemberServiceUrl`
and `Services:CoverageServiceUrl` (startup refuses otherwise).

### 2. Candidates are a complete superset of every strong match

For `$member-match`, the candidates depend on what the request supplies:

- **A member id.** The members whose member id is that id, and the members
  whose subscriber is that id (dependents carry their subscriber's id on their
  card), each in any case or separator spelling: member-service ignores case,
  spaces and hyphens, exactly the differences `MemberIdentityNormalizer`
  collapses. The subscriber comes from the family relationship graph, with the
  legacy recorded field as fallback, as the member read derives it. Of those,
  only members whose own id or derived subscriber normalizes to the supplied id
  are kept. The policy makes every member not bearing a supplied id a conflict,
  so no other member (and no birth-date search) can add a strong candidate.
- **No member id, a birth date.** Every member born on that date.

The existing policy then decides among them, unchanged. Every strong path
except SSN goes through a member id or a birth date, so no strong candidate can
be left out. Each path is read to a bound (500 candidates). A path with more
members than that, or whose paging changes during the read (a total that moves,
a page that ends early), cannot prove uniqueness, so the request answers as
ambiguous rather than guess.

For the export (P2P-01), resolution stays deterministic by member id and the
supplied demographics must agree, as before.

### 3. SSN-only requests are declined

A `$member-match` request with neither a member id nor a birth date can only
match by SSN, and CHO cannot search by SSN. With the `ChoNative` binding it
answers **insufficient criteria**.

A request that supplies an SSN *with* other identifiers is answered as
**ambiguous** (the generic 422). The policy rules a candidate out when a
supplied SSN disagrees with the stored one, but CHO's projections redact the
SSN, so the comparison cannot be made. Ignoring the SSN would let a different
person who shares a name and birth date match, so a supplied SSN that cannot be
verified is never ignored. The candidates say so (`SsnVerifiable` false); the
mock directory carries SSNs and is unaffected.

A searchable SSN index (a keyed one-way hash, its key
management and rotation) is deliberately left for its own decision, when a
plan's prior-payer traffic shows it is needed.

### 4. The export sends the plan's records as CHO projects them

The bundle carries, as their owning services project them (except as below):

- the Patient from member-service;
- every Coverage from coverage-service;
- the member's ExplanationOfBenefit resources from claims-service whose
  service period starts within the lookback window (five years by default).
  An EOB without a readable service date is left out, as the mock path already
  did.

CMS-0057-F excludes provider remittances and enrollee cost-sharing from the
Payer-to-Payer claims data, so every EOB is redacted before it leaves
(`PayerToPayerEobRedaction`): `total`, `payment`, `payee`, `adjudication`,
`benefitBalance`, `processNote` and `addItem` are removed, and on every item,
detail and sub-detail the `adjudication` (amounts and CARC/RARC reasons),
`unitPrice` and `net`. The mock path sends its EOBs without the paid amount.

The export is never silently partial: coverage or claims paging that changes
during the read, or more EOBs than one read carries (10,000), answer FHIR 502.

The plan's own payer id (`Cms0057:PayerToPayerOutbound:LocalPayerId`) is the payer of
every CHO coverage, and a coverage's subscriber id is the member's subscriber
(the member's own id for a subscriber).

### 5. Identity, tenancy, consent and failure unchanged

- The tenant comes from the authenticated request, and only the configured
  served tenant is answered (multi-tenant Payer-to-Payer remains G-35).
- Upstream calls carry that tenant and fhir-service's own service identity, as
  ADR 018's reads do.
- The consent gate and the receiving-payer binding run exactly as before.
- An upstream failure answers FHIR 502, never "no match" or an empty export.
- A directory read outside a tenant-resolved request is refused.
- Logs carry route templates and masked member ids only.

## Consequences

Positive:

- The data a plan sends another payer is the plan's own, tenant-scoped, and
  current.
- Member match scales with candidates rather than membership, without weakening
  the ambiguity guarantee.
- The mock no longer sits behind any production Payer-to-Payer path once the
  binding is set.

Tradeoffs:

- Requests that only an SSN could match are declined until an SSN index exists,
  and requests that supply an SSN alongside other identifiers answer ambiguous;
  a prior payer can resend without the SSN.
- The receiving payer gets clinical and claim context, not financial detail;
  the plan's own Patient Access API still serves the full EOB to the member.
- A birth date shared by more than 500 members cannot be matched on name and
  birth date alone; the caller has to supply a member id. Likewise an id borne
  by more than 500 members (a subscriber with that many dependents) answers
  ambiguous.
- The export makes one call per Coverage page and per EOB page; it is bounded by
  the same page limits as the services' own searches.

## Scope boundaries

- Prior-authorization data in the export (G-35 PA data, M) remains missing.
- D-13 (consent not bound to the receiving payer, `$member-match` not
  consent-gated) is unchanged.
- Bulk Payer-to-Payer transfer, multi-tenant serving and the one-business-day
  clock are unchanged.
- Clinical resources imported from prior payers are not re-exported.

## References

- `src/services/fhir-service/Services/ChoNative/ChoNativeMemberDirectory.cs`
- `src/services/fhir-service/Services/PayerToPayer/ChoNative/`
- `src/services/fhir-service/Services/PayerToPayer/PayerToPayerEobRedaction.cs`
- `src/services/member-service/Repositories/MemberFhirSearchRepository.cs` (subscriber filter, any-form identifiers)
- [ADR 018](018-fhir-patient-coverage-from-cho-services.md)
- `docs/epa-gap-matrix.md` §4.5, §5 T-4, §11 P2P-01, P2P-04

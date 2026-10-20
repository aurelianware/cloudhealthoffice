# ADR 018: FHIR Patient and Coverage Served From CHO's Own Member and Coverage Services

## Status

Accepted. Applies [ADR 008](008-fhir-r4-projections.md) to Patient and Coverage.

## Context

fhir-service answers `Patient` and `Coverage` reads and searches through
`IFhirDataAdapter`. The only implementation was `MockFhirDataAdapter`,
registered unconditionally, so every environment served five synthetic
patients even when member-service and coverage-service held real records
(capability ledger CAP-22). Patient Access, Provider Access and the
Payer-to-Payer export all sit on top of these reads, which is why the
CMS-0057-F scenarios PAT-01, PROV-01 and P2P-01 are scored as gaps.

Other FHIR resources already follow one pattern: the domain service that owns
the record owns its FHIR projection, and fhir-service is the single external
FHIR façade that applies SMART scopes, patient binding, consent and
attribution before calling it (claims-service → ExplanationOfBenefit,
provider-service → Practitioner, benefit-plan-service → InsurancePlan).
member-service already had a Patient projector; coverage-service had none.

Two defects stood in the way:

- member-service's projector set `Patient.id` to the internal document id,
  while every other CHO resource references a patient as
  `Patient/{memberId}` (ExplanationOfBenefit, the SMART launch context, the
  read route itself). A client following `EOB.patient` could not land on the
  Patient it named.
- member-service's general search could not support FHIR search: no stable
  sort, drafts removed after the page limit (short pages), no total, no
  given-name, gender or last-updated filters.

## Decision

1. **The owning service projects; fhir-service fronts.**
   - member-service gains `GET /api/v1/members/fhir/Patient` (search) beside the
     existing `GET /api/v1/members/{memberId}/fhir` (read).
   - coverage-service gains a Coverage projector,
     `GET /api/v1/coverage/{id}/fhir` (read) and
     `GET /api/v1/coverage/fhir/Coverage` (search).
   - Both search endpoints take **structured** parameters (exact id, name
     prefixes, a date range, offset and limit) and return
     `{ "total": n, "resources": [ … ] }`. FHIR search syntax — modifiers,
     date prefixes, token `system|code`, `Patient/` references, paging — is
     parsed only in fhir-service, so the domain services never parse FHIR.
2. **`Patient.id` is the member id.** It matches every reference CHO already
   emits. The internal document id is not exposed.
3. **Binding is configuration, per resource.**
   `FhirAdapters:Sources:Patient` and `FhirAdapters:Sources:Coverage` take
   `Mock` or `ChoNative`. The code and `appsettings.json` default stays `Mock`
   so a clone or a test host behaves as before; the Kubernetes manifest for
   fhir-service binds both to `ChoNative`. `Encounter` and `Claim` accept only
   `Mock`; any other value, or `ChoNative` without the upstream URL, stops
   fhir-service at start-up rather than serving something other than what the
   configuration says.
4. **Status reporting follows the binding, not the label.** The adapter-status
   report reads the same resolved bindings the router uses. A resource bound
   to `Mock` reports Demo whatever mode is configured. A resource bound to
   `ChoNative` reports Hybrid by default, like the other CHO-service proxies,
   and Live only when an operator labels it Live under
   `FhirAdapters:Resources`. The four data resources are therefore no longer
   labelled in `appsettings.json`. Data classification stays independent: a
   synthetic demo tenant can be wired natively.
5. **Confinement is checked twice.** A patient- or provider-confined search
   is narrowed by the upstream's member filter, and fhir-service then refuses
   (502) a result that names any other member, so a data source that ignored
   the filter cannot leak records. A read returns 404 unless the resource is
   the bound member's.
6. **Nothing is silently ignored.** A search parameter, modifier or control
   (`_include`, `_sort`) the native path does not apply is a 400, because the
   bundle's self link echoes the query as given.
7. **Failures are explicit.** Upstream 404 is FHIR 404. Upstream 5xx,
   transport failure, timeout or an unparseable body is FHIR 502 with a
   transient OperationOutcome and no upstream detail. Unsupported search
   input (an unknown date prefix, an identifier system CHO does not index) is
   FHIR 400, never silently ignored.
8. **Identity and tenancy are unchanged.** Calls use the existing outbound
   chain: a SMART caller's request carries fhir-service's own service token
   for the caller's tenant; a CHO caller's token is forwarded. The upstream
   resolves the tenant from the token and filters every query by it.
   SMART patient binding and Provider Access attribution are still applied in
   fhir-service before and after the call.

## Scope boundaries

Not in this change, each recorded as a follow-up:

- `IPatientAccessDataProvider` and `IChoMemberDirectory` (Payer-to-Payer
  respond and `$member-match`) still read the mock store. Member-match needs a
  demographic candidate query on member-service rather than "all members".
- `Encounter` and `Claim` have no CHO-native source yet.
- Coverage carries no `subscriber`, `relationship` or `dependent` because
  coverage-service does not store them; they belong to the member record and
  are a follow-up join.
- Patient search results omit `generalPractitioner` (a PCP lookup per row);
  the read includes it.
- Record-access audit for SMART callers records fhir-service's service
  identity upstream, not the end user or app; accounting of disclosures for
  Patient Access needs fhir-service to log the SMART subject and client.
- The data-class label is tenant-wide: with Patient and Coverage served from
  plan records and Encounter and Claim still mock, the per-resource modes in
  adapter-status are the precise statement.

## Consequences

- A deployment that sets both sources to `ChoNative` serves Patient and
  Coverage from the same records the portal and claims adjudication use.
- Demo environments must hold synthetic members and coverage in the CHO
  services; an empty store now returns empty bundles instead of the mock's
  five patients.
- member-service's FHIR read now hides draft members, as its other reads do.
- Clients that depended on `Patient.id` being the internal document id from
  member-service's projection endpoint must use the member id (no such
  client exists in this repository).

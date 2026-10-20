# DTR durable resource operations

DTR uses the existing `dtr_questionnaires` and `dtr_responses` collections in
`MongoDb:DatabaseName`. All resource reads, search inputs, questionnaire updates
and response-intake existence checks come from MongoDB. There is no per-pod
resource dictionary or success fallback when Mongo is unavailable.

## Compatibility and visibility

No migration or additional fields are required. Existing envelopes containing
`tenantId`, `resourceId` and `fhirJson` remain readable and searchable. Updates
patch only `fhirJson`, preserving `createdAt` and unknown envelope fields.
Historical edits that existed only in a stopped pod's memory cannot be recovered
from Mongo; reapply those changes from the payer's source if needed.

Questionnaires are selected by exact stored tenant keys: the caller's tenant
plus the read-only `default` library. A tenant's resource with the same id hides
the default **before** search filtering and pagination. Updating a default as a
normal tenant creates that tenant's own durable override. The reserved default
tenant remains unavailable for API questionnaire writes. Responses are selected
for exactly one tenant, then filtered by the existing patient, questionnaire,
status and id search rules; the controller retains patient-context enforcement.

Search uses the existing JSON payloads, so there is no stale metadata index to
backfill. Mongo scopes candidates by tenant, and the service filters and sorts
these per-request results by ordinal resource id before pagination. This closes
durability and cross-pod consistency gaps; it is not an indexed server-side
filter/pagination implementation for large tenant libraries or response sets.
The existing tenant/resource and tenant/questionnaire indexes remain unchanged.

## Concurrent writes and failures

Questionnaire create is insert-only. If a supplied id already belongs to that
tenant, create retains the existing behavior of generating another id; it never
replaces the existing form. Generated ids use full GUIDs. Shared seeds are also
insert-only, so parallel startup cannot overwrite an existing library entry.

Updates read the persisted version, increment it and conditionally patch the
original stored JSON. A competing update or override creation returns a
concurrency conflict (HTTP 409 through the shared exception middleware); reload
and retry. This fences changes during a request. It does not implement FHIR
`If-Match` or reject a client editing an old copy after the latest version has
already committed before that client's request begins.

A successful create/update/response submission follows the acknowledged Mongo
write. Store failures propagate; do not treat an unavailable store as an empty
or successful search. Default seeding failures remain logged at construction;
reads do not manufacture an in-process library as a fallback.

## Remaining DTR work

G-53 remains partial for complete answer validation and general canonical URL /
version resolution. Local ids and `Questionnaire/{id}` references retain their
existing semantics. G-54/G-55 package, CQL/ValueSet and prepopulation work remains
separate, as does CRD classification durability (D-22). This change does not
establish full DTR conformance or production deployment validation.

## Verification

`MongoDtrResourceStoreTests` uses a real MongoDB process. It exercises already
running readers and fresh service instances, tenant/default override visibility,
stable pagination and response filters, old payload-only documents, response
intake against another pod's form, create races, conditional update races and
parallel library seeding. Existing DTR HTTP access and patient-scope tests remain
part of the FHIR regression suite.

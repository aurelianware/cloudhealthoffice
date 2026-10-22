# ADR 023: Publish FHIR Projections Into the Plan's Managed FHIR Store

## Status

Accepted. Applies [ADR 008](008-fhir-r4-projections.md) outward: the
projections CHO already serves are also copied into a cloud provider's managed
FHIR server when the plan asks for it. First provider: Google Cloud Healthcare
API. First resource: ExplanationOfBenefit.

## Context

Every major cloud now sells a managed FHIR R4 server (Google Cloud Healthcare
API, Azure Health Data Services, AWS HealthLake). Health plans increasingly
standardize on one as their clinical and claims data hub, because the server
connects natively to the rest of that cloud. On Google Cloud, a FHIR store
streams every write into BigQuery as SQL-on-FHIR tables, emits change
notifications to Pub/Sub, exports NDJSON to Cloud Storage, de-identifies into a
second store, and its BigQuery tables are reachable from Vertex AI and BigQuery
ML. A plan running that stack wants its core administration data in it.

CHO already projects its domain records to FHIR R4 (ADR 008, ADR 018): each
owning service projects, and fhir-service is the single façade. It does not
place those projections anywhere a plan's own data platform can reach them
without calling CHO's API and building an extract.

The plan owning its data in its own cloud, in a form its analytics and AI
tooling read natively, is the point of running CHO in the plan's tenancy.

## Decision

1. **CHO stays the system of record; the managed store is a copy.** Adjudication
   state, pricing, accumulators and appeals timers keep their domain models
   and MongoDB persistence (ADR 017). Nothing in CHO reads back from the managed
   store to make a decision. Making a managed FHIR server CHO's primary schema is
   rejected for the same reasons as ADR 008, plus per-request cost and latency
   in the adjudication path, and a hard dependency on one cloud.

2. **Publishing is event-driven and idempotent.** fhir-service consumes
   `claims.finalized.v1` in its own consumer group, fetches the claim's
   ExplanationOfBenefit from claims-service (the same projection the façade
   serves), and PUTs it at its own id (the claim's version chain id). Replaying
   an event re-reads the chain head and overwrites, so redelivery converges.
   The event gains an additive, nullable `ClaimVersionId`; consumers fall back
   to `ClaimId` for older producers.

3. **Outcomes are terminal or retried, never skipped.** Published, another
   tenant's claim, no projection, and a resource the store rejects (profile
   validation) commit the offset. A rejection is logged with its
   OperationOutcome issue codes, never the resource. A claims-service outage or
   a store failure leaves the offset uncommitted and retries the message.

4. **A managed store holds one plan.** `ManagedFhirStore:TenantId` is required
   when publishing is on; events for other tenants are neither fetched nor
   written. Multi-tenant CHO hosting publishes to one store per plan.

5. **Provider-neutral contract, one provider implemented.**
   `IManagedFhirStore` (CloudHealthOffice.ManagedFhirStore) carries upsert,
   read, delete and store-level export. `ManagedFhirStore:Provider` is `None`
   by default, `GcpHealthcare`, or `InMemory` (Development only). Azure Health
   Data Services and AWS HealthLake are planned; naming one is a start-up
   error, not a silent no-op. A named provider with missing settings stops the
   service at start-up.

6. **Analytics and AI are store configuration, not CHO code.** BigQuery
   streaming, Pub/Sub notifications, the export bucket, and the BigQuery
   connection to Vertex AI are provisioned by Terraform
   (`infrastructure/gcp/fhir-store.tf`, off by default). CHO writes FHIR; the
   plan's cloud does the rest.

7. **The store accepts unresolved references.** Resources are published as
   their source events arrive, so an EOB can land before the Patient or
   Coverage it references. The Terraform disables the store's referential
   integrity check; integrity is enforced in CHO.

## Consequences

Positive:

- A plan on Google Cloud gets adjudicated claims in BigQuery within seconds of
  finalization, as standard SQL-on-FHIR tables, with no extract to build.
- Vertex AI and BigQuery ML work over CHO data in the plan's own project, under
  the plan's own IAM, which is the data-ownership argument made concrete.
- The integration seam with other vendors on the same platform is FHIR, which
  both sides already speak.

Tradeoffs:

- Only ExplanationOfBenefit is published. Patient, Coverage, Practitioner and
  Organization follow from their owning services' events; until then, joins in
  BigQuery go through reference ids.
- The managed store's profile validation can reject a resource CHO serves.
  Rejections are logged, not retried, and need an operational alert before
  production use.
- Deleting a claim in CHO does not delete it from the store. CHO adjusts by
  appending versions, so the EOB at the chain id is overwritten with the
  current head; hard deletion is out of scope.
- The managed store is a PHI store. The demo Terraform is synthetic-data only
  (no BAA, no CMEK, public Healthcare API endpoint); production needs the
  plan's BAA, CMEK and VPC Service Controls, as for the rest of the stack.
- Sending FHIR rows to a generative model from BigQuery is a disclosure to that
  model's service; it must be covered by the plan's agreements before real
  member data is used.

## References

- [Managed FHIR store analytics](../architecture/managed-fhir-store-analytics.md)
- [ADR 008: FHIR R4 projections](008-fhir-r4-projections.md)
- [ADR 018: Patient and Coverage from CHO services](018-fhir-patient-coverage-from-cho-services.md)
- [Claim FHIR projection](../architecture/claim-fhir-projection.md)
- [GCP demo](../deploy/GCP-DEMO.md)

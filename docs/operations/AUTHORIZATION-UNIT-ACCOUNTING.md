# Authorization unit accounting

## Implemented: preserve the quantity identity

The prerequisite fix based on `2cf1f13e` preserves unit metadata that was previously
lost before claims could account for it:

* 837 SV103/SV204 service quantity codes now survive parser → native
  `AdapterClaimLine.UnitType` → persisted `ClaimLine.UnitType` → adapter/scrub
  mapping. `DrugUnitOfMeasure` remains the separate CTP drug quantity measure.
* PAS quantity `code` and `system` are stored independently as requested-service
  `UnitCode` and `UnitSystem`. The legacy `UnitType` label/fallback is retained;
  a display label is not promoted to a coding system or a comparable code.
* The internal automated-decision endpoint explicitly copies those fields into
  the authoritative record. Internal recording/inquiry DTOs retain them.
* Authorization validation returns `approvedUnitType`, `approvedUnitCode` and
  `approvedUnitSystem` for the uniquely matched service line alongside
  `approvedUnits`. Ambiguous or absent line selection supplies no guessed unit.
  The claims validation result retains those fields without conversion.

Codes and systems are opaque and case-sensitive. No UCUM/X12 conversions, unit
aliases, default `UN`, or default coding systems are introduced. Legacy fields
that were never stored remain unknown. No index migration or production backfill
was performed, and external-core adapter round trips are not certified here.

PAS codes/systems longer than 256 characters fail mapping instead of being
silently truncated. Non-null codes/systems participate in the existing request
hash, so equal display labels cannot hide different coded quantities. New null
fields are omitted from that wire shape to preserve the previous hash shape for
requests with no quantity coding.

The recorder also requires the stored response (or compensating read) to confirm
requested quantity coding against the stable authorization line. Missing,
changed, or differently cased coding is not acknowledged as successfully stored.
Complete the authorization-service rollout before the new FHIR writer. An older producer that
drops coding causes submission failure without a successful `preAuthRef`; records
already written without metadata require reconciliation, not a fabricated unit.
Complete the claims writer rollout before relying on its unit metadata. Older
writers can read additive fields but replacement writes may discard fields they
do not model; rollback and mixed-version writes need reconciliation before any
ledger rollout.

A request now carrying previously omitted coding can have a different hash from
an older attempt, and must not be treated as the same coded request blindly.

The standard PAS response builder's decimal adjudication representation is not
changed. This fix does not invent FHIR extensions or establish complete PAS
quantity conformance (G-17).

## Not implemented: reservation and consumption ledger

This is the unit identity prerequisite, not authorized-unit accounting completion.
`ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes` remains false by default. No
quantity is reserved, consumed, released, or newly denied by this change.

The remaining accounting boundary must provide:

* Explicit agreement between claim service units and the authorization's unit
  code/system, with unknown or incompatible units held for resolution.
* Aggregation of all claim lines drawing on the same authorization service,
  including repeated procedures and service dates; a per-line comparison cannot
  prevent double spending.
* Tenant/member/authorization-line scoped atomic reservations, stable retry
  identities, and concurrent-claim tests proving the approved budget is never
  exceeded.
* Durable coordination with claim approval and the existing accumulator outbox,
  including failed persistence, lost HTTP answers, examiner approval retries,
  denials, voids, reversals, replacement claims, and adjustments.
* Explicit commit/release semantics and reconciliation after crashes; a timeout
  alone is not evidence that a paid claim's units may be reused.

G-64 also retains target-deployment identity/plan-scope validation and EDI line
correlation where the submission does not identify the authorization service.
The configurable floor and institutional bill-type scope remain G-61/G-62.

## Evidence

Regression tests cover professional and institutional EDI codes across native
round trips, unknown legacy units, separation from drug units, coded PAS mapping,
request-hash differences, oversized coding rejection, and the real
FHIR → authorization-service → Mongo seam. HTTP recorder tests cover acknowledgement
and lost-answer read-back matching. Producer and consumer validation tests retain
the selected line's quantity coding without inferring it for another line.
Projects and SDK policy remain .NET 8. Tests are not production payer acceptance.

# Claims authorization line matching

## Implemented boundary

CHO claim lines may carry `priorAuthorizationLineNumber`: the positive service
line number on the referenced authorization. It is independent of the claim's
`lineNumber`. The existing line `priorAuthorizationNumber` still overrides the
claim-level number; the explicit authorization line may also reference that
claim-level number. The field survives the native claim/adapter round trip.
Existing stored claims need no backfill; the new field is nullable.

For example, claim line 2 may reference authorization AUTH-1, service line 12.
Claims sends `GET /api/authorizations/AUTH-1/validate?...&lineNumber=12` with the
procedure, service date and provider, under the existing tenant-guarded service
identity. Different authorization line references use different cached lookups,
even when their authorization number, procedure and date are identical.
A uniquely matched denied line remains a definitive denial.

Without an explicit line reference the existing unique-procedure lookup remains.
Repeated procedures return `validationCode: AmbiguousServiceLine`; claims pends
`PAUNAVAIL` before benefit calculation instead of treating ambiguity as a clinical
denial. Correct the claim's authorization reference through the supported claim
submission/correction workflow before rerunning it; retrying an unchanged
ambiguous claim cannot select a line. No new examiner edit UI is supplied here.

The consumer also recognizes the older producer's exact ambiguity message during
a rolling deployment. Deploy the claims consumer before expanding enforcement;
an old consumer still interprets the producer's invalid result as a denial.
Other non-definitive HTTP responses retain existing pend behavior.

## Deliberate mapping limits

837 `REF*G1` carries the authorization number. The parser does not synthesize an
authorization service identity from the claim's `LX` sequence. An EDI claim with
repeated authorization procedures therefore needs explicit correlation/enrichment
through a supported native workflow. External adapters must preserve this new
field before the same guarantee can be claimed for their round trips. No
outbound X12 mapping for this field is invented.

## G-64 rollout assessment

`ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes` stays false by default. The
line-selection defect is fixed; this is not approval to expand PA enforcement.
The original review at `b6f97595` identified the checks below; the member-binding
follow-up is based on `634e8046`:

* **Member binding implemented:** claims requires a confirmed comparison to the
  individual member, with dependent intake guards. See
  [member-binding runbook](CLAIMS-AUTHORIZATION-MEMBER-BINDING.md) for legacy and
  rollout limits. Historical identity data quality is not inferred.
* **Quantity accounting:** the API returns approved units, but claims does not
  compare or consume them. Define matching units of measure, aggregation across
  claim lines, and concurrency-safe reservation/release across claims and
  adjustments. A per-line comparison alone cannot prevent double spending.
* **Scope:** verify actual tenant plan PA flags and deployment service identity.
  G-61 (configurable floor) and G-62 (837I bill-type inpatient alignment) remain
  open and require explicit changed-claim scope tests. This change preserves the
  current institutional Medicaid POS-21 floor.

No target deployment, payer credentials, production claims, or unit ledger were
used. These are confirmed source-level limits, not observed production incidents.

## Evidence

`BenefitCalculationStagePriorAuthLookupTests` covers explicit approved and denied
lines, independence from claim ordinals, lookup-cache separation, ambiguity
pending, and native adapter preservation. `HttpAuthorizationValidationClientTests`
covers the stable ambiguity code (even an inconsistent valid body) and invalid
explicit references. `AuthorizationsControllerExtendedTests` exercises the real
HTTP producer for mixed decisions on repeated procedures and verifies its code,
selected quantities, and date limits. Tests target .NET 8.

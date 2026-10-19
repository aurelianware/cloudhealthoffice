# Authorization service-line decisions

CHO Replace mode accepts `lineDecisions` on both reviewer endpoints:
`POST /api/authorizations/{id}/response` and `PUT /api/authorizations/{id}/status`.
Existing `authorizations:decide` permission requirements still apply.

Example response body for one approved and one denied service:

```json
{
  "controlNumber": "review-123",
  "reviewDecision": "A2",
  "lineDecisions": [
    { "lineNumber": 7, "reviewDecision": "A1" },
    {
      "lineNumber": 12,
      "reviewDecision": "A3",
      "denialReasonCode": "NOTMEDNEC",
      "denialReason": "The second service does not meet the documented clinical criteria."
    }
  ]
}
```

Use the authorization's `requestedServices[].lineNumber`; for a legacy record
without numbers, use its one-based service ordinal. PAS requests preserve
`Claim.item.sequence`, which must be positive and unique. Explicit decisions
must cover every requested line exactly once, regardless of request order.

- A1 approves all requested units. A different quantity requires A2.
- A2 on a line requires positive approved units below its requested quantity,
  plus a reason code and explanation for the reduction.
- A3 requires a reason code and explanation; approved units are zero.
- A4 leaves approved units unset and can carry a pend reason.

The header must match the aggregate: all approved means A1, all denied A3,
any approved/reduced line mixed with another outcome A2, and no approval with
an outstanding pend A4. Uniform A1/A3/A4 header-only decisions populate every
line. Header-only A2 is supported only for a single reduced line. Multi-line
quantities must be supplied per line; the service never sums unlike units.
The status endpoint also requires the corresponding status name.

Validation precedes mutation and persistence. Decisions and independent line
snapshots in status history survive Mongo reloads. Automated decisions and
benefit exclusions use the same policy; public submissions cannot self-approve
by populating line outcomes.

PAS submit/inquiry project committed line identities, outcomes, quantities and
reasons. This is not a claim of full PAS STU2 or X12 reason-code conformance;
G-13/G-17 remain open. A replay after a reviewer modification answers with the
current stored partial decision.

Claims validation accepts optional `lineNumber` together with `procedureCode`.
If a procedure occurs on multiple authorization lines, omission of that identity
returns invalid rather than selecting an arbitrary line. Claims-service does
not yet correlate that identity automatically. Unit consumption, request
modification outside quantities, and the full lifecycle state machine are
separate work. Existing records are not backfilled; use an explicit reviewer
decision to establish missing line outcomes. Keep the plan-flagged claims
feature disabled pending G-64, including D-36/D-40 service-identity fixes.

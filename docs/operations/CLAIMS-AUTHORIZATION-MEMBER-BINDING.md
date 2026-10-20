# Claims authorization member binding

## Implemented boundary

The claims validation client requires the individual claim `MemberId` and sends
it as the escaped `memberId` query on the existing authorization validation
endpoint. The benefit stage uses the claim member, never `SubscriberId`,
`ResolvedMember.SubscriberMemberId`, or a PAS `Patient/...` reference. The existing
tenant-guarded claims-service identity, authorization number, procedure, service
date, provider and explicit authorization-line matching remain in place.

Authorization-service compares the requested member with the stored
`Authorization.MemberId`, trimming surrounding whitespace and otherwise using
case-sensitive ordinal equality. This is the same individual identifier domain
used by native claims and the PAS mapper's member identifier; it is not a Mongo
document ID. No alias, demographic, family-wide or case-folded match is inferred.
PAS mapping already restricts subscriber-ID fallback to a self-coverage patient;
that mapping is not changed by this fix.

A bound response echoes only the requested member as `validatedMemberId` and
includes nullable `memberMatches`:

| Response | Claims outcome |
| --- | --- |
| Requested member echoed exactly, `memberMatches: true` | Continue existing status/date/procedure/provider/line validation; this alone does not approve the authorization. |
| Requested member echoed exactly, `memberMatches: false` | Definitive invalid authorization for this member; existing CARC 197 denial before benefit calculation. |
| Missing/different echo or absent comparison (including older producer) | `PAUNAVAIL` before benefit calculation, never payment or clinical denial on an unconfirmed comparison. |
| Stored authorization member missing | Producer returns `MemberIdentityUnavailable`, no comparison; claims pends. |
| Blank or overlong explicit query | Producer returns 400; claims treats it as non-definitive. |

The response never adds the stored owner's member identifier. Member-mismatch
messages and client reasons contain no actual owner identifier. Existing access
logging still uses the stored record/member under its existing controls.

For compatibility, direct legacy calls to `/validate` without `memberId` retain
an **unbound** result. That response is not evidence of member ownership; the
claims client requires a member argument and refuses unconfirmed responses.
No new user permissions or token-routing rules are introduced.

## Dependent intake and previously accepted records

837 mapping preserves the subscriber separately. When a separate patient is
present, only that patient's own identifier becomes `MemberId`. An absent or
blank patient ID no longer substitutes the subscriber's ID. Canonical intake then
returns its existing required-MemberId validation failure before adapter writes
or a submitted event. This is an intake error to correct, not an adjudicated
clinical denial. Demographic dependent matching remains unimplemented.

Already accepted PA-required claims that explicitly identify a non-self patient
(`PatientRelationship` other than `18`) but reuse the subscriber's ID pend
`PAUNAVAIL` without an authorization lookup. They need identity reconciliation;
repeated examiner approval does not override this check. Claims outside the
existing PA-required scope are not newly subjected to PA validation here.

No production record scan or backfill was performed. Historical records that
lack enough relationship data to distinguish an individual from a family ID need
separate data-quality review; this patch cannot infer the actual patient from a
misidentified historical record.

## Rollout and remaining G-64 scope

Deploy authorization-service's comparison fields before the new claims consumer.
If deployed in the opposite order, PA-required claims pend until the producer
confirms member comparisons. An older claims consumer does not enforce member
binding even with the new producer. Verify both versions and service identity in
the intended deployment before expanding PA scope.

There is no schema/index migration: the authorization member field already exists.
Records without an individual identifier require correction through an audited
workflow rather than a fabricated identifier or automatic family match.

`ClaimsAdjudication:PriorAuth:UsePlanFlaggedCodes` remains false by default.
Unit identity preservation is implemented in [the unit accounting prerequisite](AUTHORIZATION-UNIT-ACCOUNTING.md); no unit ledger is implemented yet.
G-64 still requires unit-of-measure agreement and concurrency-safe authorized-unit
accounting across claims/adjustments, EDI authorization-line enrichment where
needed, tenant plan scope checks and deployment validation. G-61/G-62 floor
configuration and inpatient bill-type alignment remain separate.

## Evidence

The full claims suite includes dependent matching/mismatch through the real HTTP
client and benefit stage, pending before engine calls on missing/unconfirmed
identity, old-import guarding, escaped IDs and real outbound service-token tests.
The EDI mapper and canonical submission tests prove missing dependent IDs cannot
reach the adapter/event publisher. Real authorization HTTP tests cover individual
and case-sensitive comparisons, invalid queries, missing stored identity, and
claims-service token acceptance with no stored-owner ID disclosure. All projects
remain on .NET 8. These tests do not constitute production-payer acceptance.

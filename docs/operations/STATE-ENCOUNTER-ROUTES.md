# State encounter routes (Florida, Texas, California)

`encounter-submission-service` routes each adjudicated Medicaid claim to the
state that receives its encounter. This runbook covers what each route does
today, how a plan is turned on, and what still has to happen before a state
accepts the files. It answers TX-02 and CA-01 in part; it does not close them.

## Routes

| State | Receiver | Output | Deadline default | Responses the state returns |
| --- | --- | --- | --- | --- |
| FL | AHCA FMMIS | Claim JSON staging file (unchanged) | Tenant compliance config, 60 days after adjudication | 999 |
| TX | HHSC via TMHP | ASC X12 005010 837P / 837I | 30 days after the end of the adjudication month | 999, 277CA, HHSC encounter reports |
| CA | DHCS (PACES) | ASC X12 005010 837P / 837I | 30 days after the end of the adjudication month | 999, 277, Encounter Validation Response (EVR) |

The Texas and California deadline defaults are placeholders to be confirmed
against the plan's signed contract (Texas TX-00) and the current DHCS
encounter APL. DHCS APL 14-019 requires at least monthly submission and
corrections within 15 calendar days of the response file. Override with
`DeadlineRule` and `DeadlineDays`. Florida's 60-day rule is never applied to
Texas or California.

## Turning a route on

Texas and California are off by default. A claim is routed to one of them
only when the state is `Enabled`, the receiver identities are set, **and**
the plan's tenant has its state-issued identities. Anything less creates no
record, as before, and `GET /api/encounters/state/routes` says what is
missing.

```json
"EncounterRoutes": {
  "TX": {
    "Enabled": true,
    "ReceiverId": "<TMHP ISA08 from the trading partner agreement>",
    "ReceiverName": "TMHP",
    "DestinationPayerId": "<2010BB payer ID from the TMHP encounters companion guide>",
    "DestinationPayerName": "<2010BB payer name>",
    "UsageIndicator": "T",
    "Tenants": {
      "<cho-tenant-id>": {
        "SubmitterId": "<TPID issued by TMHP>",
        "SubmitterName": "<plan legal name>",
        "ContactName": "EDI Operations",
        "ContactPhone": "5125550100",
        "PlanName": "<plan name as HHSC knows it>",
        "PlanPayerId": "<plan payer ID for 2330B / SVD01>",
        "PlanCode": "<HHSC plan code, used in file names>"
      }
    }
  }
}
```

No receiver identifier is defaulted in code. Every value comes from the
state's companion guide and the plan's trading partner agreement.

`UsageIndicator` stays `T` (ISA15 test) until the state has accepted test
files and issued production credentials. File names carry `TEST` or `PROD`.

Optional state-level settings, with their defaults: `ReceiverIdQualifier`
(ZZ), `DestinationPayerResponsibility` (S), `PlanPayerResponsibility` (P),
`DestinationClaimFilingIndicator` (MC), `PlanClaimFilingIndicator` (HM),
`StagingContainer`. Change them only where the companion guide says so.

## What a batch does

Every worker cycle (default 4 hours), for each enabled route and tenant:

1. Reads each pending encounter's claim (claims-service), member
   (member-service) and billing provider (provider-service by NPI). Any read
   failure refuses the whole batch and leaves every record retryable, the
   same rule as the Florida batch-integrity fix.
2. Validates each encounter against the 5010 rules a state front end
   rejects on: NPI check digit, EIN, billing ZIP+4, member DOB and address,
   diagnosis pointers, facility type and inpatient admission data for 837I,
   revenue codes, and line balancing (charge = plan paid + CAS adjustments).
   A failing encounter is set to `ValidationFailed` with its reasons in
   `validationErrors` and is not sent.
3. Writes one interchange per form type (837P, 837I). Each encounter is its
   own ST/SE, so a 999 or 277CA points at one record. The plan's adjudication
   is reported as the other payer (2320/2330B) with line-level SVD, CAS and
   DTP*573; the state is the destination payer in 2010BB; BHT06 is `RP`.
4. Stores the file in the `encounter_state_files` collection and, when blob
   staging is configured, copies it there. Records become `Batched` with the
   file name, ISA13, GS06, ST02 and CLM01 they were sent under, which is what
   response reconciliation will match on.

Files can be listed and downloaded with
`GET /api/encounters/state/files?state=TX` and
`GET /api/encounters/state/files/{id}/content` (token tenant only,
`encounters:read`).

ISA13 control numbers come from an atomic per-tenant, per-state counter in
`encounter_control_numbers`.

## Hand-off and responses

When a staged file has been handed to the state (SFTP drop, partner upload),
record it with `POST /api/encounters/state/files/{id}/transmitted`. Its
`Batched` encounters become `Submitted`.

Apply each response file the state returns with
`POST /api/encounters/state/{TX|CA}/responses` and a body of
`{ "fileName": "...", "content": "<raw file>" }` (`encounters:write`). The
file is stored in `encounter_state_responses` with what it matched, and each
encounter keeps a `responseHistory`.

| Response | Matched on | Result |
| --- | --- | --- |
| 999 | GS06 (AK1) and ST02 (AK2) | IK5 A → `SyntaxAccepted`, E → `SyntaxAcceptedWithErrors`, R/M/W/X → `Rejected` with the IK3/IK4 errors. A transaction the 999 does not list takes the AK9 group code. Applies only to `Batched` / `Submitted` records, so a late 999 never undoes a 277CA acceptance. |
| 277CA (TX) / 277 (CA) | CLM01 (TRN02 at the patient level) | STC A1/A2 → `StateFrontEndAccepted` and the state's claim ID from REF*1K is saved as `stateClaimReference` (needed for replacements and voids). A3/A4/A6/A7/A8 → `Rejected` with the STC codes. Service-line STCs are not used. |
| DHCS EVR (XML) | — | Refused with 422 until the EVR schema from the DHCS guide is implemented. |

References in a file that match nothing are returned as `unmatched` and kept
on the stored response. `StateFrontEndAccepted` means the state's front end
took the encounter; it is not final encounter acceptance.

The Florida `POST /api/encounters/{tenantId}/acknowledge` path now only
touches Florida records.

## Where encounters come from

Texas and California encounter records are created from `claims.finalized.v1`,
the event claims-service publishes when a claim is paid, denied or reversed.
The service's older consumer listens on `adjudication-completed`, which no
service in the repository publishes; it is left in place for Florida only.

| Setting | Default |
| --- | --- |
| `Kafka:ClaimFinalized:Topic` | `claims.finalized.v1` |
| `Kafka:ClaimFinalized:GroupId` | `encounter-submission-service.claims-finalized` |

The consumer acts only for tenants enrolled on exactly one X12 route, and
only for Medicaid claims (it reads `lineOfBusiness` from claims-service). The
event carries no state, so a tenant enrolled on both Texas and California is
refused with a warning until a member-state rule is added. Denied claims are
reported only when the route sets `ReportDeniedClaims`. The offset is
committed after the event is handled; a claims-service outage leaves the
event to be redelivered, and redelivery finds the same records.

## Replacements and voids

A claim adjustment (`originalClaimId` on the event) or a reversal becomes
encounter work against the original encounter:

| Original encounter is | Adjustment | Reversal |
| --- | --- | --- |
| not yet sent (`Pending`, `ValidationFailed`) | original closed, adjusted claim goes as a new original | original closed, nothing sent |
| rejected by the state, or closed | adjusted claim goes as a new original | original closed, nothing sent |
| in flight (`Batched`, `Submitted`, `SyntaxAccepted`) | replacement (CLM05-3 `7`) created, waits | void (`8`) created, waits |
| accepted, with the state's claim reference | replacement sent with `REF*F8` | void sent with `REF*F8` |

A waiting replacement or void stays `Pending` and is listed under
`waitingOnOriginal` in the batch result. When the original's 277 arrives it
goes in the next batch with the state's reference in `REF*F8`; if the state
rejects the original instead, the replacement goes as a new original and the
void is closed. Records link both ways (`originalSubmissionId`,
`supersededBySubmissionId`), and every automatic close is kept in
`reworkHistory`.

## Rework queue

`GET /api/encounters/state/rework?state=TX&reason=VALIDATION` lists the
tenant's Texas and California encounters that need a person, nearest
deadline first, each with its reasons to fix:

| Reason | Status | Comes from |
| --- | --- | --- |
| `VALIDATION` | `ValidationFailed` | CHO's own pre-send validation; the encounter was never sent |
| `SYNTAX_REJECTED` | `Rejected` | the state's 999 (IK3/IK4 detail) |
| `STATE_REJECTED` | `Rejected` | the state's 277CA / 277 (STC codes) |

Fix the source record (claim, member, provider), then
`POST /api/encounters/state/rework/{submissionId}/requeue` with an optional
`{ "note": "what was fixed" }`. The encounter returns to `Pending`, its old
batch, file and control numbers are cleared, and the next batch re-reads and
re-validates it. A state-rejected encounter was never accepted, so it goes
back as a new original (DHCS APL 14-019 handles rejected records the same
way). DHCS expects corrections within 15 calendar days of the response file.

To take an encounter out of the queue without reporting it (a duplicate, a
service the state does not take encounters for),
`POST .../rework/{submissionId}/close` with a required note. It becomes
`ClosedNotReportable`. Every action is kept in the record's `reworkHistory`
with who did it and why.

Florida rejections keep their existing `retry` endpoint and are not in this
queue.

## Not done yet

- **Transport.** Nothing sends files to TMHP or DHCS or collects responses
  from them. Files are staged for the EDI lead or the partner hand-off
  (TX-02: "agree actual HHSC destination/operator"), and responses are
  posted to the API.
- **DHCS EVR file** and any HHSC encounter quality reports after the 277CA.
- **Work distribution and portal.** The rework queue is an API; it is not
  yet a work type in work-management-service or a portal page.
- **Florida on `claims.finalized.v1`.** Florida still depends on the
  unpublished `adjudication-completed` topic; moving it is a separate change.
- **Tenants on both Texas and California** need a member-state rule before
  their claims can be routed.
- **Companion-guide replacement rules.** Whether TMHP or DHCS want REF*F8 to
  carry their ICN or another identifier, and whether a void must repeat the
  original's lines, still has to be checked in the companion guides.
- **837D, capitated and delegated encounters, other-payer (TPL) loops** and
  any state-specific segments in the TMHP or DHCS companion guides.
- **State acceptance.** Companion-guide review, partner test cycles and
  certification. Passing these tests is not evidence that either state
  accepts the files.

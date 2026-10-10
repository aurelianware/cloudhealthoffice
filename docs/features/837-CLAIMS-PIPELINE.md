# 837 Claims Ingestion Pipeline

How a raw X12 837 file becomes an adjudicated claim.

> **Note:** This doc previously described an SFTP → Argo Workflow → Kafka → Argo Events
> pipeline with a stub `x12-parser` container. That design was never wired up end-to-end
> and has been superseded by the in-process pipeline below, built directly into
> `claims-service`. If you find references elsewhere (`containers/x12-parser/`,
> `argo-workflows/x12-837-ingest.yaml`, `kafka/topics.yaml`'s `claims-adjudication` topic)
> treat them as historical/unused rather than the current path.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                       837 Claims Import Pipeline                        │
└─────────────────────────────────────────────────────────────────────────┘

Evaluator / integration                POST /api/v1/claims/import/raw837
   drops a raw 837 file    ──────────▶  (ClaimsV1Controller, claims-service)
                                                    │
                                                    ▼
                                   X12837Parser.Parse(ediContent)
                                   (hand-rolled segment walker — 837P/SV1
                                    and 837I/SV2, multi-claim batches,
                                    2000C dependent loops)
                                                    │
                                                    ▼
                                   X12837ClaimMapper.Map(parsed, tenantId)
                                   (→ AdapterClaim; deliberately leaves
                                    BenefitPlanId blank — see below)
                                                    │
                                                    ▼
                                   IClaimSubmissionService.SubmitAsync
                                   (validates, writes via tenant-routed
                                    IClaimAdapter, persists a
                                    ClaimImportTransaction row — accepted
                                    or rejected — either way)
                                                    │
                                                    ▼
                                   Service Bus: ClaimVersionSubmitted
                                                    │
                                                    ▼
                              ClaimAdjudicationOrchestrator (background
                              message consumer) runs the 8-stage pipeline
                              in Order:
                                100  Scrubbing
                                150  ProviderIntegrity
                                200  NetworkCredentialing
                                (resolve BenefitPlanId from the member's
                                 active coverage here if it arrived blank
                                 — ICoverageResolver, coverage-service)
                                300  BenefitCalculation
                                400  NcciEdits
                                500  CoordinationOfBenefits
                                600  AiExamination
                                999  Persistence
                                                    │
                                                    ▼
                                   Claim.Status = Approved / Denied / Pended
```

## SNIP validation and the 999

Before a raw 837 is parsed or mapped, `X12837SnipValidator`
(`claims-service/EDI/Validation`) checks it against WEDI SNIP levels 1–5 for 837P
(005010X222A1) and 837I (005010X223A2/A3). It reports every finding as a `SnipIssue`
with the level, rule id, loop, segment id and position (counting from ST = 1), element
and component position, and a message. It does not throw on bad input.

| Level | What is checked |
| --- | --- |
| 1 Syntax | ISA fixed widths; ISA/IEA, GS/GE and ST/SE control numbers and counts; duplicate ST02; unknown segment ids; X12 mandatory elements; numeric and date/time formats |
| 2 Implementation guide | BHT values; 1000A/1000B; HL hierarchy (sequence, parents, child codes); required 2010AA/2000B/2010BA/2010BB/2000C segments (incl. billing tax id); CLM05 and CLM06–09; principal diagnosis; at most 12 diagnoses (837P); LX numbering; SV1/SV2 and their qualifiers; DTP*434 on 837I |
| 3 Balancing | CLM02 = ΣSV102 (837P) / ΣSV203 (837I) |
| 4 Situational | DTP*472 on every 837P line, and on 837I outpatient lines when the statement covers more than one day; 837I inpatient (TOB x1x) needs DTP*435 and CL1; line dates inside the statement period and not after BHT04; NPI check digit (Luhn, prefix 80840); billing address not a PO Box; subscriber-is-patient (SBR02=18) needs DMG and no 2000C; frequency 7/8 needs REF*F8; SV107 pointers point at existing diagnoses |
| 5 Code sets | ICD-10-CM format (no decimal point) and no ICD-9 qualifiers on or after 2015-10-01; ICD-10-PCS format; CPT/HCPCS and modifier formats; CMS place-of-service list; revenue code and type-of-bill formats. Membership checks run only when an `ISnipCodeSetReference` is registered. |

Each level's action is configurable under `ClaimsImport:Snip`:

```json
"ClaimsImport": { "Snip": { "Enabled": true, "Level1": "Reject", "Level2": "Warn",
                            "Level3": "Reject", "Level4": "Reject", "Level5": "Warn" } }
```

These values are also the defaults:

- **Level 2 warns at go-live.** Implementation-guide findings (for example a claim-level
  `DTP*472` on an 837P, rule `L2-2300-DTP472`) are accepted and reported, and every one is
  logged and recorded with the submitter (see "Level 2 warnings: log and record" below), so
  each partner can be moved to `Reject` once its files are clean.
- Levels 1, 3 and 4 reject.
- Level 5 warns because, without a code-set reference, its checks are format checks only.

- `Reject` rejects the transaction set: 999 IK5 `R`.
- `Warn` reports the finding but accepts the set: IK3/IK4 detail with IK5 `E`.
- `Off` skips the level.

### Per-partner overrides

`ClaimsImport:Snip:PartnerOverrides` overrides individual levels for one submitter. The key
is the interchange sender id **ISA06** (the trading partner's `x12Config.senderId` in
trading-partner-service). When no key matches ISA06, the **GS02** application sender code is
tried. Matching ignores case and surrounding spaces. A level the override leaves unset uses
the global value, and a submitter with no override gets the global levels.

```json
"ClaimsImport": { "Snip": {
  "Level2": "Warn",
  "PartnerOverrides": {
    "SUBMITTER01": { "Level2": "Reject" },
    "LEGACYCH":    { "Level3": "Warn" }
  } } }
```

As environment variables: `ClaimsImport__Snip__PartnerOverrides__SUBMITTER01__Level2=Reject`.

The override applies to that interchange's envelope checks (Level 1 on ISA/IEA, GS/GE) and
to every transaction set in it. Each `ClaimImportTransaction` stores the matched key in
`snipPartnerOverride` (null = global levels). The log entry carries it as `SnipPolicy`
(`default` or `partner:<key>`).

Overrides live in claims-service configuration, not on the trading-partner record. The raw
837 upload is tenant-scoped and does not resolve a trading partner, and claims-service has no
trading-partner-service client. Keying on ISA06 matches the same `x12Config.senderId` value
without adding a cross-service call to the intake path. Changing an override needs a config
change and restart (or reload) of claims-service.

### Level 2 warnings: log and record

The import (`POST .../import/raw837`) writes one structured `Warning` log entry per Level 2
finding at `Warn`, with event id `8372` / `SnipLevel2Warning` and these properties:

| Property | Source |
| --- | --- |
| `RuleId` | e.g. `L2-2300-DTP472` |
| `Location` | loop, segment with its qualifier code, element, e.g. `2300 DTP*472`, `2300 CLM05-2` |
| `SubmitterQualifier` / `SubmitterId` | ISA05 / ISA06 |
| `ApplicationSenderCode` | GS02 |
| `InterchangeControlNumber` / `GroupControlNumber` / `TransactionSetControlNumber` | ISA13 / GS06 / ST02 |
| `ClaimNumber` | CLM01 (as already logged for import failures) |
| `SetAccepted` | false when another finding rejected the same set |
| `TenantId`, `SnipPolicy` | tenant; `default` or `partner:<key>` |

No message text, member name, member id or date is logged. The only value read from the file
for `Location` is a 1–3 character alphanumeric qualifier code (`472`, `F8`, `85`). The
dry-run `/validate` endpoint does not log or record.

The durable record is the existing `ClaimImportTransaction` (Mongo
`claim-import-transactions`). Each claim's record now also holds `submitterQualifier`,
`submitterId`, `applicationSenderCode`, `interchangeControlNumber`, `groupControlNumber`,
`snipPartnerOverride` and `snipWarnings` (API JSON names; Mongo stores the PascalCase property names). `snipWarnings` lists every Warn-level finding (any
level) for the claim and its transaction set: level, rule id, location, loop, segment
id/position, element/component, data element reference, `claimLevel` and message. An index on
`(TenantId, SnipWarnings.RuleId, ReceivedAt)` backs the query.

To find who sends a claim-level DTP*472:

```bash
curl "http://claims-service.cloudhealthoffice/api/v1/claims/import-transactions/snip-warnings?ruleId=L2-2300-DTP472" \
  -H "X-Tenant-ID: <tenant>"
# narrow with &submitterId=<ISA06 or GS02>&level=2&limit=500
```

Or aggregate in Mongo:

```js
db["claim-import-transactions"].aggregate([
  { $match: { TenantId: "<tenant>", "SnipWarnings.RuleId": "L2-2300-DTP472" } },
  { $group: { _id: "$SubmitterId", claims: { $sum: 1 }, lastSeen: { $max: "$ReceivedAt" } } }])
```

Then flip a clean partner with `PartnerOverrides:<ISA06>:Level2 = Reject`.

### Warn findings in the 999

A Warn finding gets the same 999 detail as a reject, and only the acknowledgment code
changes: IK3 (segment, position, loop, IK304), `CTX*CLM01`, IK4 when the finding is
element-level, then IK5 `E` with `I5`, and AK9 `E`. A Level 2 warning cannot mask a reject:
if another finding in the set rejects, IK5 is `R`. This follows the 005010X231A1 meaning of
"accepted but errors were noted". The 999 does not show whether a finding came from a
warning or a reject. The IK5 code and the response's `snipIssues[].severity` tell them apart.

`POST /api/v1/claims/import/raw837` validates first:

- It submits only claims from accepted transaction sets.
- Claims from rejected sets come back with `Success = false` and their SNIP messages, and are
  written to the import-transaction log as `Rejected`.
- The response adds `acknowledgmentCode`, `acknowledgment999` and `snipIssues`.
  `acknowledgmentCode` takes the AK9 values: `A`, `E`, `P` (partially accepted) or `R`.
  `acknowledgment999` is the X12 999 (005010X231A1), with IK3 (segment, position, loop,
  IK304), `CTX*CLM01`, IK4 (element, data element reference, IK403, and a copy of the bad
  value for code values only) and IK5/AK9.
- An unreadable file returns 400 with no 999. A TA1 would cover that case and is out of scope.

`POST /api/v1/claims/import/raw837/validate` runs the same validation and returns the
findings and the 999 without submitting anything.

Rules and limits:

- **Envelope rejections reach the 999.**
  - An ISA/IEA error at a rejecting Level 1 rejects every set in that interchange (IK5 R, AK9 R).
  - A GS/GE error (GS01/04/06/08, GE count or control, a missing GE) rejects its group, with AK905 codes.
  - With Level 1 set to `Warn`, these group codes are listed in AK9 under `E` and nothing is rejected. With `Off`, they are not emitted.
  - The 999 always acknowledges exactly the transaction sets that the import submits.
- **One 999 interchange per inbound interchange.** Each is addressed to that interchange's sender and echoes its ISA15 (`T`/`P`). AK101 echoes GS01.
- **Inpatient vs outpatient** (837I) comes from the full CLM05-1 facility type and classification code:
  - Inpatient: 11, 12, 18, 21, 22, 28, 41, 65, 66, 86.
  - Outpatient: 13, 14, 23, 43, 71–77, 79, 83, 85.
  - Codes in neither table, such as home health and hospice, only produce warnings for the DTP*435, CL1 and line DTP*472 rules.
- **Finding caps.** Findings are capped per transaction set and per file (`MaxFindingsPerTransactionSet` and `MaxFindingsPerFile`, default 1,000 each), with a single "too many findings" entry when a cap is hit. Findings dropped by a cap still count toward acceptance. The 999 lists at most 1,000 IK3 loops per set.
- **Never throws.** A file cut off mid-transaction-set is rejected with L1 findings. An unexpected failure becomes an `L1-VALIDATION-FAILED` finding.
- **Echoed values.** Segment ids are echoed only as valid 2–3 character ids; anything else is replaced with `???`.
- **Import record.** Each `ClaimImportTransaction` records its transaction set's ST02 and IK5 code, the 999's ISA13, the submitter (ISA05/ISA06, GS02), ISA13/GS06 of the inbound file, the partner override in force and its Warn-level findings (`snipWarnings`). The 999 text itself is returned in the response and is not stored.

## Why `BenefitPlanId` starts blank

`X12837ClaimMapper` deliberately does not try to resolve `BenefitPlanId`/`CoverageId` from
the raw 837 — an unrecognized member should surface as a real pend during adjudication, not
get silently papered over during mapping. `ClaimAdjudicationOrchestrator` resolves it from
the member's active coverage (via `ICoverageResolver` → coverage-service's
`GET /api/v1/coverage/member/{memberId}/active`) immediately before plan resolution runs, but
only when the claim arrived without one — this is what lets a member seeded through the 834
enrollment pipeline actually reach a priced outcome from an 837, instead of pending on
"missing BenefitPlanId" regardless of how correctly they were enrolled.

## Testing end-to-end

```bash
# Seeds a benefit plan + plan-code mapping, imports a sample 834 (creating
# Sponsor/Member/Coverage), submits a matching 837, and polls until the
# claim reaches a terminal adjudication status.
scripts/smoke/834-to-837-e2e-smoke.sh
```

Or manually:

```bash
# 1. Submit a raw 837 file
curl -X POST http://claims-service.cloudhealthoffice/api/v1/claims/import/raw837 \
  -H "X-Tenant-ID: <tenant>" \
  -F "file=@my-claim.837"

# 2. Check the transaction log (admin view — accepted AND rejected imports)
curl http://claims-service.cloudhealthoffice/api/v1/claims/import-transactions \
  -H "X-Tenant-ID: <tenant>"

# 3. Poll the claim itself for adjudication status
curl http://claims-service.cloudhealthoffice/api/claims/{claim-id} \
  -H "X-Tenant-ID: <tenant>"
```

The portal's **EDI Transactions** console (`/edi-transactions`) shows both 834 and 837
import history — accepted/rejected status and error text — without needing raw API calls.

## Troubleshooting

### 837 file rejected at upload (400) or claims rejected by SNIP
- `acknowledgmentCode` is `R`/`P` → read `snipIssues` (or the 999's IK3/IK4): each names
  the level, rule, loop and segment position. Fix the file or, if a level should only warn
  for this deployment, set `ClaimsImport:Snip:LevelN` to `Warn` (or only for one submitter:
  `ClaimsImport:Snip:PartnerOverrides:<ISA06>:LevelN`).
- `acknowledgmentCode` is `E` → accepted with Warn findings; see
  `GET /api/v1/claims/import-transactions/snip-warnings` and the `SnipLevel2Warning` log entries.
- No `CLM` segments found → not a valid 837, or wrong transaction type.
- Parse failure → check the error message; `X12837Parser` throws `X12FormatException` with
  the specific segment/reason.

### Claim accepted but never prices (stuck pending/rejected on "missing BenefitPlanId")
- The member has no active coverage in coverage-service for the claim's service date /
  insurance line — most likely the 834 onboarding step (plan-code mapping,
  `scripts/onboard-plan-code-mappings.sh`) wasn't completed for this employer group before
  their 837s started arriving.
- Check `GET /api/v1/claims/import-transactions` for the transaction's `Status`/`Errors`, and
  `GET /api/claims/{id}` for `BenefitPlanId` and `PendDetails`.

### Adjudication never completes
- Check the Service Bus subscription is live (`ClaimAdjudicationOrchestrator`'s background
  consumer) — this replaced the old Argo Events/Kafka trigger entirely.

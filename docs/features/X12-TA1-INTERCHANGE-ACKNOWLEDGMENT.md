# X12 TA1 Interchange Acknowledgment

Every inbound X12 path checks the interchange envelope (ISA/IEA) before its
transaction parser runs, and answers with a TA1 when the sender asked for one
or when the envelope has errors. For interchanges CHO sends, the trading
partner's TA1 is read back and recorded against the interchange it
acknowledges.

Code: `src/services/shared/CloudHealthOffice.Infrastructure/Edi/Interchange/`.

## Where it runs

| Service | Inbound (TA1 generated) | Outbound (partner TA1 tracked) | Endpoints prefix |
|---|---|---|---|
| claims-service | `POST /api/v1/claims/import/raw837` (and `/validate`, which previews and stores nothing) | 277CA from `GET /api/claims/{id}/277ca` | `api/v1/claims/interchange` |
| enrollment-import-service | `POST /api/v1/enrollment/import/raw834` | — | `api/v1/enrollment/interchange` |
| eligibility-service | `POST /api/eligibility/270` | 271 returned by the same endpoint | `api/eligibility/interchange` |
| payment-service | — | every 835 envelope persisted by payment and reversal runs | `api/v1/payments/interchange` |

Not wired yet: the 820 upload in premium-billing-service, 275 attachments,
the capitation 835, and the 837 encounter submission path. Each one needs the
same few lines: call `IX12InterchangeIntake.ReceiveAsync` before parsing, and
`IOutboundInterchangeTracker.RecordSentAsync` when sending.

## Inbound: envelope checks and the TA1

`X12EnvelopeReader` reads the delimiters the way X12 defines them: the element
separator is the 4th character, ISA16 is the component separator, and the next
character is the segment terminator. It splits the ISA on the element
separator instead of reading fixed offsets, so a field of the wrong width is
reported under that field's own note code. A file can hold several
interchanges; each one is judged on its own.

| TA105 | Check |
|---|---|
| 001 | IEA02 ≠ ISA13 (the TA1 reports the header's value) |
| 002 | ISA12 ≤ 00401 and ISA11 ≠ `U` |
| 003 | ISA12 is not a supported version (`X12Interchange:SupportedVersions`, default 00401, 00501) |
| 004 | Segment terminator is alphanumeric, or the same as the element or component separator |
| 005 / 007 | ISA05 / ISA07 is not a valid ID qualifier, or not 2 characters |
| 006 / 008 | ISA06 / ISA08 is blank or not 15 characters |
| 009 | ISA08 is not in `X12Interchange:KnownReceiverIds` (checked only when that list is set) |
| 010 / 011 | ISA01 is invalid / ISA02 is not 10 characters, or is not blank when ISA01 = 00 |
| 012 / 013 | ISA03 is invalid / ISA04 is not 10 characters, or is not blank when ISA03 = 00 |
| 014 / 015 | ISA09 is not a valid YYMMDD date / ISA10 is not a valid HHMM time |
| 016 | ISA11 (5010 repetition separator) is alphanumeric, or the same as another delimiter |
| 017 | ISA12 is not 5 digits |
| 018 | ISA13 is not 9 digits |
| 019 | ISA14 is not 0 or 1 |
| 020 | ISA15 is not P or T |
| 021 | IEA01 is missing, not numeric, or not the number of GS segments |
| 022 | GS/GE out of balance, a segment outside a functional group, a TA1 inside one, or content after the IEA |
| 023 | No IEA, a file ending inside a group, a new ISA before the IEA, or an ISA cut short |
| 024 | A GS missing GS01–GS08, or an interchange with no functional groups (and no TA1) |
| 025 | Duplicate: the same sender (ISA05:ISA06) already used this ISA13 for this tenant within `X12Interchange:DuplicateWindowDays` (default 365) |
| 026 | Element separator is alphanumeric or whitespace |
| 027 | ISA16 is alphanumeric, or the same as the element separator or segment terminator |

028–031 cover deferred delivery requests, which an ISA cannot carry. They have
descriptions (`Ta1NoteCodes.Describe`) but nothing produces them.

**Decision.** TA105 is the first finding (X12 allows one note per TA1).
Separators are checked first, because nothing after a bad one can be trusted.
TA104 is:

- `A` / `000`: no findings.
- `E`: every finding is in `X12Interchange:NotedNoteCodes` (empty by default,
  so every error rejects). The contents are processed.
- `R`: anything else. The contents are **not** processed: the 837/834/270
  parser never sees the interchange, so no 999 is produced for it.

The duplicate check runs only on an interchange that passed every other check,
so a rejected interchange does not use up its control number and the
corrected file can be resent with the same ISA13. The check is atomic in
MongoDB: an upsert keyed on `{tenant}|{sender}|{ISA13}`, so two concurrent
receipts of the same interchange cannot both get through. Setting
`DuplicateWindowDays` to 0 turns the check off. That is for sandboxes where the
same sample file is uploaded again and again; leave it on in production.

**When a TA1 is produced.** When ISA14 = 1, or whenever TA104 is not `A`. With
ISA14 = 0 and a clean envelope there is no TA1. The TA1 goes back the way the
interchange came: ISA05/06 and ISA07/08 are swapped, the received delimiters
and version are reused when they are valid, ISA14 = 0, and IEA01 = 0, because a
TA1 is not inside a functional group. If the received ISA09/ISA10 are invalid,
TA102/TA103 carry the time the TA1 was made.

**Responses.**

- 837 / 834 rejected: `400`, with `interchangeAcknowledgmentCode = "R"`,
  `acknowledgmentTa1` and `ta1AcknowledgmentIds`. No 999.
- 837 / 834 accepted: the usual result, plus `interchangeAcknowledgmentCode`,
  `interchangeNoteCodes` (837 only), `acknowledgmentTa1` when one is due, and
  `ta1AcknowledgmentIds`. `ClaimImportTransaction.Ta1ControlNumber` links each
  claim row to its TA1, the same way `Acknowledgment999ControlNumber` links it
  to its 999.
- 270 rejected: `400 text/plain` whose body is the TA1. No 271.
- 270 accepted: the 271 as before. The stored TA1's id is in the
  `X-TA1-Acknowledgment-Id` header.

A file whose content does not start with `ISA`, or whose ISA cannot be split
into 16 elements, gets no TA1, because there is no ISA13 to acknowledge. It is
refused with a plain error.

## Outbound: partner TA1s

`IOutboundInterchangeTracker.RecordSentAsync` stores the envelope of each
interchange CHO sends: ISA13, sender, receiver, ISA14, transaction type, and
the owning record id. It never stores the content, which may carry PHI.
Tracking failures are logged and never block sending. payment-service wraps
`IEraEnvelopeRepository` (`TrackingEraEnvelopeRepository`), so payment and
reversal runs needed no changes.

`POST {prefix}/ta1/inbound` takes a partner's TA1 file as a text body (at most
1 MB). For each TA1 segment it:

1. Stores the TA1 (`Direction = Received`).
2. Matches it to the outbound interchange with ISA13 = TA101 **and** receiver
   = the TA1's sender (ISA06). It never matches on control number alone,
   because ISA13 is only unique per sender/receiver pair.
3. Sets that interchange's `AckStatus` to `Accepted`, `AcceptedWithErrors`
   or `Rejected`, with the note code and description.

**Rejections show up** in three places: in `GET {prefix}/outbound?status=Rejected`,
as a warning log (partner id, transaction type, control number, note code; no
content), and in the `cho.edi.ta1.total` counter with
`cho.ta1.direction=Received` and `cho.ta1.ack_code=R`. Alert on that counter.
A TA1 that matches nothing is stored and reported as `Unmatched`.

## Endpoints

All tenant-scoped, under the service's default read permission (GET) and
default write permission (POST).

| Method | Path | |
|---|---|---|
| GET | `{prefix}/ta1?direction=&ackCode=&controlNumber=&senderId=&limit=` | Stored TA1s, newest first |
| GET | `{prefix}/ta1/{id}` | One stored TA1 (findings, codes, ids) |
| GET | `{prefix}/ta1/{id}/edi` | Its X12 text |
| POST | `{prefix}/ta1/inbound` | A partner's TA1 file |
| GET | `{prefix}/outbound?status=&limit=` | Interchanges sent, with TA1 status |

## Storage

MongoDB, in the service's own database (tenant-scoped databases work too):

- `x12-interchange-receipts`: `_id = tenant|sender|ISA13`, `ReceivedAt`.
- `x12-interchange-acknowledgments`: generated and received TA1s.
- `x12-outbound-interchanges`: envelope identifiers and TA1 status.

When no `IMongoDatabase` is registered (Cosmos-only and test hosts), a
process-local store is used, and the duplicate window then covers only that
pod. TA1s hold envelope identifiers only, never transaction content.

## Configuration (`X12Interchange`)

| Key | Default | |
|---|---|---|
| `Enabled` | `true` | `false` skips the envelope gate (and so the TA1) |
| `DuplicateWindowDays` | `365` | 0 turns off the duplicate check |
| `NotedNoteCodes` | `[]` | Codes that give E instead of R |
| `KnownReceiverIds` | `[]` | When set, any other ISA08 gets 009 |
| `SupportedVersions` | `["00401","00501"]` | Others get 003 |
| `FallbackSenderQualifier` / `FallbackSenderId` | `ZZ` / `CHO` | The TA1's sender when the received ISA07/08 are unusable |

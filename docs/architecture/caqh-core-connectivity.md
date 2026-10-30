# CAQH CORE real-time connectivity (270/271, 276/277)

HIPAA requires a health plan to support the CAQH CORE Phase I and II
operating rules for eligibility (270/271) and claim status (276/277)
(45 CFR 162.920). Among other things those rules require that a provider, or a
clearinghouse acting for one, can send a single 270 or 276 over the internet
and get the 271 or 277 back in the same call. Before this work CHO answered a
raw 270 in real time only through its own API (`POST api/eligibility/270`,
which needs a CHO token), and handled 276/277 only as batch files over SFTP
(`infrastructure/argo-workflows/x12-276-ingest.yaml`).

This document describes the CORE endpoint, what is done, and what is still
open.

## Status

| Piece | State |
| --- | --- |
| CORE Connectivity vC2.2.0 envelope (SOAP 1.2 + WSDL, MIME multipart) | Done (shared library, `Edi/Core`) |
| Submitter authentication: X.509 client certificate, username/password, CHO OAuth token | Done |
| Real-time 270 → 271 at `/core/eligibility` | Done |
| 999 on rejection only, TA1 for a rejected interchange | Done for 270 |
| "Unable to respond" 271 (AAA 42) at the response budget | Done |
| Response-time metric and audit log line | Done |
| Real-time 276 → 277 at `/core/claimstatus` | Done (claims-service: `EDI/ClaimStatusInquiry`) |
| 271 data content rules: every requested service type, CORE generic service types, patient financials, TRN echo | Done |
| 271 subscriber-not-found AAA codes and name normalisation | Later PR, see below |
| System availability (86% per week) reporting and published downtime schedule | Done (`GET /core/eligibility/availability`, `GET /core/claimstatus/availability`) |
| CORE certification test (CORE-authorised testing vendor) | Not started; optional |

The endpoint is off by default (`CoreConnectivity:Enabled`).

## Endpoints

Each service that owns the data hosts its own CORE endpoint, built from the
same shared processor:

| URL | Service | Request PayloadType | Response PayloadTypes |
| --- | --- | --- | --- |
| `POST /core/eligibility` | eligibility-service | `X12_270_Request_005010X279A1` | `X12_271_Response_005010X279A1`, `X12_999_Response_005010X231A1`, `X12_TA1_Response_00501X231A1` |
| `POST /core/claimstatus` | claims-service | `X12_276_Request_005010X212` | `X12_277_Response_005010X212`, 999, TA1 |

`GET {url}?wsdl` returns the WSDL for the SOAP form. The companion guide a plan
publishes lists both URLs. CORE does not require a single URL for all
transactions.

The request's `Content-Type` selects the envelope: `application/soap+xml` for
SOAP 1.2, `multipart/form-data` for MIME. The response uses the same envelope.

## What happens to a request

1. **Envelope.** The processor reads the SOAP body
   (`COREEnvelopeRealTimeRequest`) or the MIME form fields and checks them:
   all mandatory fields present (`{Field}Required`), `CORERuleVersion` is
   `2.2.0` (`VersionMismatch`), `ProcessingMode` is `RealTime`, the
   PayloadType is one this endpoint answers, `PayloadID` is a UUID,
   `TimeStamp` is an XML Schema dateTime, `ReceiverID` is one of
   `CoreConnectivity:ReceiverIds` (when set), and `PayloadLength` and the
   SHA-1 `Checksum` match the payload when sent. A failure is answered with
   HTTP 400 and the error code in the envelope. A body that is not a SOAP
   envelope at all gets a SOAP 1.2 Fault.
2. **Authentication.** See below. A failure is HTTP 401 `UnAuthorized`. A
   submitter registered for other payload types only gets HTTP 403
   `UnAuthorized`.
3. **Payload.** The handler runs inside `CoreConnectivity:ResponseBudget`
   (default 15 seconds, leaving room in CORE's 20-second round trip). For a
   270:
   - The interchange envelope goes through the same X12 intake as the batch
     and API paths. A rejected ISA/IEA is answered with the TA1 as the payload.
   - A 270 that cannot be parsed is answered with a 999 that rejects its
     transaction sets (IK5 R, AK9 R). An accepted 270 gets no 999, as the CORE
     real-time acknowledgement rule says.
   - Otherwise the inquiry runs through the same eligibility service as
     `POST api/eligibility/270` and the 271 is the payload.
   - If coverage cannot be checked (a dependency is down, an unexpected
     error) or the budget runs out, the payload is a 271 whose information
     source loop says `AAA*Y**42*R`: unable to respond at current time,
     resubmission allowed. The envelope still says `Success`, because the
     envelope was fine.
4. **Response.** `SenderID` and `ReceiverID` are swapped, `PayloadID` is
   echoed so the submitter can match the response, and `TimeStamp` is now.

## Claim status (276 → 277)

claims-service reads the 276 (005010X212) with `Edi276Parser` and answers
every 2200 inquiry loop (TRN) in one 277 (`Edi277ClaimStatusGenerator`). The
interchange goes through the same X12 intake as other paths (TA1 for a
rejected envelope, 999 for a 276 that cannot be parsed).

Finding the claim (`ClaimStatusLookup`):

1. `REF*1K` (payer claim control number) is CHO's claim id; else
2. `REF*EJ` (patient control number) is the claim's CLM01 (`ClaimNumber`); else
3. the subscriber's claims for the provider and service dates are searched.

A candidate is only returned when it belongs to the subscriber the 276 names
(member or subscriber id), and it must also match the provider NPI (billing or
rendering), the total charge (`AMT*T3`) and the service dates when they are
sent, and the dependent's last name for a dependent inquiry. At most 10 claims
are returned per inquiry. Knowing a claim id is never enough to read a claim.

STC01 by CHO status:

| CHO status | STC01 | Meaning |
| --- | --- | --- |
| Submitted, Received | `A2:20` | Accepted into adjudication |
| InAdjudication | `P1:20` | Pending, in process |
| Pended | `P2:20` | Pending, payer review |
| Approved (Pay), Deny | `P1:3` | Adjudicated, awaiting payment cycle (not final until the 835) |
| Paid, PartiallyPaid | `F1:65` | Finalized, paid (STC05 paid amount, STC06 adjudication date, STC08 paid date) |
| Denied | `F2:1` | Finalized, denied; see the remittance advice |
| Voided | `F0:1` | Finalized; see the remittance advice |
| (no matching claim) | `A4:35` | Claim not found |
| (lookup failed, or past the response budget) | `E1:0` | Response not possible, system status |

Each 2200 loop echoes the inquiry's TRN, and returns `REF*1K` (claim id),
`REF*EJ` (CLM01) and `DTP*472`. A lookup failure answers only that inquiry
with E1; the other inquiries in the same 276 still get their status.

The claims-service 277CA generator (`ClaimAcknowledgmentService`) uses
single-letter categories (`A:20`, `F:2`) and status codes 2/4/15/16/19 that do
not mean what its comments say in the X12 Claim Status Code list (2 is "more
detailed information in letter", 65 is "paid"). The real-time 277 uses the
codes above; the 277CA should be aligned in a separate change.

## Authentication

CORE credentials are not CHO tokens, so the endpoint is anonymous to the
platform's token check and the processor authenticates every request itself,
before it reads the payload. The tenant a call acts within always comes from
the submitter's registration in `CoreConnectivity:Submitters`, never from the
envelope.

A request may present:

- **An X.509 client certificate** (the CORE vC2.2.0 certificate option).
  Either Kestrel receives it directly, or the ingress terminates mutual TLS,
  verifies the chain, and passes the certificate as URL-encoded PEM in the
  header named by `CoreConnectivity:ClientCertificateHeader` (for nginx,
  `ssl-client-cert`). The ingress must remove any copy of that header sent by
  the client. CHO checks the validity dates and compares the certificate's
  SHA-256 thumbprint with the registration's `CertificateThumbprints`.
- **A CHO OAuth bearer token** (the OAuth option of the voluntary CORE vC4.0.0
  rule). Its client id (`azp`) must be in the registration's
  `OAuthClientIds`, its tenant must be the registration's tenant, and it must
  carry the endpoint's permission (`eligibility:check`). Turn this off with
  `AllowBearerTokens: false`.
- **A username and password**: the MIME `UserName` / `Password` fields, or a
  WS-Security UsernameToken with a `PasswordText` password in the SOAP header.
  The registration holds a PBKDF2 hash, not the password.

Whichever is used, the registration must also carry the envelope's `SenderID`.

To make a password hash:

```bash
python3 -c 'import hashlib,os,base64,sys; s=os.urandom(16); i=210000; h=hashlib.pbkdf2_hmac("sha256", sys.argv[1].encode(), s, i, 32); print(f"pbkdf2-sha256${i}${base64.b64encode(s).decode()}${base64.b64encode(h).decode()}")' 'the-password'
```

Keep the hash in the secret store (Key Vault / Secret Manager), not in plain
configuration.

### Configuration

```json
"CoreConnectivity": {
  "Enabled": true,
  "ReceiverIds": [ "CHOPAYER" ],
  "ResponseBudget": "00:00:15",
  "ClientCertificateHeader": "ssl-client-cert",
  "Submitters": [
    {
      "SenderId": "AVAILITY",
      "Name": "Availity",
      "TenantId": "plan-a",
      "PayloadTypes": [ "X12_270_Request_005010X279A1", "X12_276_Request_005010X212" ],
      "CertificateThumbprints": [ "3F1A..." ]
    },
    {
      "SenderId": "1234567890",
      "Name": "Example clinic",
      "TenantId": "plan-a",
      "UserName": "clinic-1234567890",
      "PasswordHash": "pbkdf2-sha256$210000$...$..."
    }
  ]
}
```

## Timing and availability

The CORE rules require:

- **Response time:** a real-time response within 20 seconds round trip, for at
  least 90% of responses in a calendar month.
- **Availability:** the system available at least 86% of each calendar week,
  with regular downtime published and other planned downtime announced a week
  ahead.

Every exchange records `cho.core.realtime.duration` (seconds), tagged with
`cho.core.payload_type`, `cho.core.error_code` and `cho.core.outcome`
(`answered`, `rejected_ta1`, `rejected_999`, `unable_to_respond`,
`envelope_error`, ...). The monthly 90th percentile, in PromQL:

```promql
histogram_quantile(0.9, sum by (le, cho_core_payload_type) (
  increase(cho_core_realtime_duration_seconds_bucket[30d])))
```

### Availability and downtime

The rule asks for the system to be available at least 86% of each calendar
week (at most about 23.5 hours down), counting every kind of downtime, and
for the plan to publish:

- its **regular** downtime (the weekly maintenance window),
- **non-routine planned** downtime at least a week ahead,
- **unscheduled or emergency** downtime as soon as it is known.

CHO keeps that schedule and record in `CoreConnectivity:Availability` and
publishes it, without credentials, at `GET {endpoint}/availability`
(`/core/eligibility/availability` and `/core/claimstatus/availability`). The response gives the current status, the
downtime in force, the weekly regular windows, the windows coming up in the
next 30 days (`PublishHorizon`), and the availability of this week and last
week (Sunday 00:00 UTC to Sunday). Planned windows announced less than a week
ahead are flagged `shortNotice`.

```json
"CoreConnectivity": {
  "Availability": {
    "Regular": [
      { "Day": "Sunday", "Start": "06:00:00", "Duration": "02:00:00", "Reason": "Weekly maintenance" }
    ],
    "Windows": [
      { "Kind": "Planned", "Start": "2026-12-06T04:00:00Z", "End": "2026-12-06T10:00:00Z",
        "AnnouncedAt": "2026-11-20T15:00:00Z", "Reason": "Database upgrade" },
      { "Kind": "Unscheduled", "Start": "2026-11-03T14:12:00Z", "End": "2026-11-03T14:55:00Z",
        "Reason": "Coverage database failover" }
    ]
  }
}
```

Operations add an `Unscheduled` window (with no `End` while it is still going
on) when an outage happens, and give it an `End` once it is over, so the
weekly figure counts it. Each host reads its own `CoreConnectivity` section,
so keep the `Availability` block in shared configuration (the Helm values or
the config map both services mount).

During a published window, a request that still reaches CHO is answered at
once with the handler's "unable to respond" payload (271 with AAA 42),
instead of running the inquiry and waiting out the budget. The audit line
and metric record it with outcome `downtime`.

At startup each host logs a warning for a schedule that breaks the rule on its
own: regular downtime above 23.5 hours a week, a planned window announced less
than a week ahead, or a planned or emergency window with no end.

The published record is what CHO itself knows. The figure that counts in a
CORE audit is the measured one, so keep an uptime probe outside the cluster
(the platform's uptime checks) on `GET /core/eligibility?wsdl`, and use its
weekly figure to check that every outage made it into the record.

## Audit

Each exchange writes one log line with the PayloadType, PayloadID, SenderID,
ReceiverID, tenant, authentication method, envelope format, HTTP status, error
code, outcome, response payload type, the SHA-256 of the request payload, and
the elapsed time. It never logs the payload, the credential or member data.
The SHA-256 lets an investigator match the line with the submitter's copy of
the 270. The 270 itself is stored the same way as on the API path (the
eligibility inquiry record and the X12 interchange records).

## 271 data content

The 271 generator follows the CORE eligibility data content rule:

- **Service types.** The parser reads every `EQ` segment and every repeat
  in `EQ01`. For a generic inquiry (`EQ*30`) the 271 answers the CORE
  service types (1, 33, 35, 47, 86, 88, 98, AL, MH, UC) the plan covers,
  plus any other type asked for; otherwise it answers the types asked for.
  Each answered type gets `EB*1` (EB11 = Y when it needs prior
  authorization) and its patient share.
- **Plan categories to codes.** Benefit plans keep free-text categories
  ("Office Visit", "Urgent Care"). `X12ServiceTypes.FromCategory` maps a
  category that already is a code, or a common label, to its X12 code. A
  benefit whose category maps to nothing is left out of the 271, because EB03
  may only carry a code. Plan-wide benefits (category 30) go under 30.
- **Patient financials.** Deductible (`EB*C`) and out-of-pocket (`EB*G`) give
  the plan amount in EB07 for the calendar year (EB06 23, or 22 for a service
  year), the amount met year to date (24) when there is one, and the
  remaining amount (29) always, zero included. Co-payment is `EB*B` with
  EB06 27 (per visit) and EB07; co-insurance is `EB*A` with EB08 as a
  decimal (20% is 0.2); visit limits are `EB*F` with EB09 VS and EB10. EB12
  says in network (Y) or out (N).
- **Trace numbers.** Each `TRN*1` in the 270 comes back as `TRN*2` with the
  same number, originator and supplemental ID; BHT03 echoes the 270's
  reference. A 270 without a trace gets CHO's own `TRN*2`.
- **Inactive coverage.** `EB*6` and the reason in `MSG`, with no `AAA`.
  Before this, CHO sent `AAA*N**42*Y`, which tells the submitter to try again
  later.

Still to do: when CHO cannot find the subscriber, the 271 should carry the
CORE AAA codes (72, 73, 75, 76) and apply the normalised-name matching rule.
The eligibility check returns "not covered" for an unknown member today, with
no separate "not found" signal to map from.

## To check against the licensed rule text

These follow the published rule and its examples, and should be checked
against the CORE rule documents before a certification test: the exact
spelling of each envelope error code (CORE Connectivity vC2.2.0 section 4),
that the response echoes the request's PayloadID, and the payload type
spelling for the TA1 (`X12_TA1_Response_00501X231A1`).

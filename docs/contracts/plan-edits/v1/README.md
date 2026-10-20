# Plan Edit Service Contract, Version 1

A health plan's own claim edits, written in the plan's code and run in the
plan's environment, applied by Cloud Health Office (CHO) to every claim the
plan adjudicates. The design is [ADR 019](../../../adr/019-extension-model-plan-edit-service.md).

| | |
|---|---|
| Direction | CHO (claims-service) → the plan's service |
| Transport | HTTPS `POST` to the configured endpoint, `application/json` |
| Request schema | [`request.schema.json`](request.schema.json) |
| Response schema | [`response.schema.json`](response.schema.json) |
| .NET types | `CloudHealthOffice.PlanEdits.Contracts` (request, response, signature, response rules) |
| Worked example | `src/samples/CloudHealthOffice.PlanEditService.Sample` (two illustrative rules; tested end to end with claims-service) |

## The request

```http
POST /v1/claims HTTP/1.1
Host: edits.plan.example
Content-Type: application/json
X-CHO-Timestamp: 1792519200
X-CHO-Signature: v1=5f2b…
X-CHO-Request-Id: 9c1e4a…
Idempotency-Key: 9c1e4a…
```

```json
{
  "contractVersion": "1",
  "requestId": "9c1e4a…",
  "tenantId": "plan-tenant",
  "purpose": "adjudication",
  "claim": {
    "claimId": "c-123",
    "claimVersionId": "c-123",
    "versionNumber": 1,
    "claimNumber": "CLM-2026-000123",
    "claimType": "Professional",
    "frequencyCode": "1",
    "lineOfBusiness": "Medicaid",
    "benefitPlanId": "STAR-2026",
    "memberId": "M100045",
    "member": { "birthDate": "2012-04-09", "gender": "F" },
    "billingProviderNpi": "1234567893",
    "placeOfService": "11",
    "serviceDateFrom": "2026-09-16",
    "serviceDateTo": "2026-09-16",
    "totalCharge": 12180.00,
    "diagnoses": [ { "code": "J45.909", "qualifier": "ABK", "pointer": 1 } ],
    "lines": [
      { "lineNumber": 1, "procedureCode": "99214", "modifiers": [], "units": 1, "charge": 180.00,
        "serviceDateFrom": "2026-09-16", "serviceDateTo": "2026-09-16", "diagnosisPointers": [1] },
      { "lineNumber": 2, "procedureCode": "J2357", "modifiers": [], "units": 1, "charge": 12000.00,
        "serviceDateFrom": "2026-09-16", "serviceDateTo": "2026-09-16", "diagnosisPointers": [1],
        "nationalDrugCode": "50242004062" }
    ]
  }
}
```

No names, addresses, pay-to details or free-text notes are sent. `purpose` is
`approval-readjudication` when an examiner approved the pended claim and it is
being adjudicated again with their decisions: return the same edits for the
same facts.

### Verifying the request

1. Read `X-CHO-Timestamp` and refuse it if it is more than five minutes from
   your clock.
2. Compute HMAC-SHA256 with the shared secret over the ASCII timestamp, a full
   stop, and the exact request body bytes.
3. Compare `v1=` + the lower-case hex digest with `X-CHO-Signature` in constant
   time. Refuse the request on any mismatch (CHO treats your 401 as no usable
   answer).

`PlanEditSignature.Verify` does all three for a .NET service.

## The response

`200 OK`, `Content-Type: application/json`:

```json
{
  "contractVersion": "1",
  "edits": [
    {
      "ruleId": "PLAN-HIDOLLAR-DRUG",
      "message": "Drug line over $10,000: confirm the dose against the authorization.",
      "lineNumbers": [2]
    },
    {
      "ruleId": "PLAN-NC-0417",
      "message": "Not a covered service under this benefit plan.",
      "lineNumbers": [2],
      "suggestedCarc": "96",
      "suggestedRarc": "N130"
    }
  ]
}
```

No edits fired: `{"contractVersion":"1","edits":[]}`.

| Field | Rule |
|---|---|
| `ruleId` | Required. Letters, digits, `.`, `_`, `-`; starts with a letter or digit; at most 40 characters. Keep it stable across your releases: an examiner's decision is matched to the edit by rule and lines. |
| `message` | Required, at most 500 characters. Shown to the examiner and stored on the claim. |
| `lineNumbers` | Lines on the claim in the request. Empty or absent means the whole claim. |
| `suggestedCarc` | Makes the edit a **denial edit**: one to three upper-case letters or digits, and `lineNumbers` must name at least one line. |
| `suggestedRarc` | Optional, with a `suggestedCarc` only: one or two upper-case letters and up to four digits. |

At most 100 edits and 262,144 bytes. Two edits may not share both `ruleId`
(compared without case) and `lineNumbers`. Unknown properties are ignored.

### What CHO does with each edit

| Edit | Examiner upholds | Examiner overrides |
|---|---|---|
| Review edit (no CARC) | Recorded as reviewed; no payment change | Recorded with the examiner's reason |
| Denial edit (CARC + lines) | Those lines denied in full, group CO, your CARC/RARC, on the 835 and the EOB | Recorded with the examiner's reason; lines paid as adjudicated |

Overriding needs the permission the tenant's `PLAN_EDIT` override policy sets
(default: any examiner, with a reason).
An examiner who approves the claim as a whole, without deciding edits one by
one, upholds every plan edit. A plan edit that comes back on the approval
re-run with a rule and lines nobody decided pends again.

### When there is no usable answer

Any of these means CHO did not get an answer, never that no edits fired:

- no complete response (headers and body) within the timeout, or the connection fails;
- any status other than 200 (redirects are not followed);
- a content type other than JSON, or a body over the size limit;
- a body that breaks any rule above.

The claim then pends `SVCUNAVAIL` (retried when an examiner approves it) or, if
the plan configured `WhenUnavailable: Pass`, continues without your edits.
CHO sends a request again once after a transient failure; `Idempotency-Key`
lets you recognise the repeat.

## Configuring CHO

```json
"Adjudication": {
  "PlanEdits": {
    "Tenants": {
      "plan-tenant": {
        "Endpoint": "https://edits.plan.example/v1/claims",
        "SigningSecret": "<from the secret store>",
        "Mode": "PendForReview",
        "WhenUnavailable": "Pend",
        "TimeoutMilliseconds": 3000
      }
    }
  }
}
```

Start with `"Mode": "SoftValidation"` to see which claims your edits would pend
without pending any, then switch to `PendForReview`. claims-service refuses to
start if the endpoint is not https, is a CHO service host, or the secret is
shorter than 32 characters. The endpoint may not carry credentials, a query
string or a fragment: authenticate with the signature, not a URL key.

## Versioning

Within version 1, fields are only added, and both sides ignore what they do not
know. A breaking change is version 2, and CHO keeps accepting version 1 until
an ADR retires it.

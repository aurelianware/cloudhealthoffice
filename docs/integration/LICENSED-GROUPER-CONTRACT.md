# Licensed grouper contract (APR-DRG and EAPG)

Texas and California Medicaid price inpatient hospital stays by **APR-DRG**,
and Texas prices outpatient hospital claims by **EAPG**. Both groupers belong
to 3M/Solventum and are licensed per plan. CHO does not ship or reimplement
them. Instead, the plan runs its licensed grouper behind a small adapter that
speaks the JSON below, and CHO calls it during adjudication.

The grouper returns a **classification only**: the APR-DRG with its severity
of illness, or an EAPG and payment action for each line. Weights, base rates
and adjustors come from the state's published tables, which CHO loads
separately. That keeps a change of grouper vendor away from rates.

Code: `src/engines/CloudHealthOffice.GrouperEngine/Licensed/`.

## Status

The seam is in place. No pricing method reads it yet, so turning it on
changes no payment. The APR-DRG and EAPG pricing methods follow in later PRs.

## Configuration

```json
"LicensedGrouper": {
  "BaseUrl": "https://grouper.plan.internal/",
  "AprDrgPath": "apr-drg/v1/group",
  "EapgPath": "eapg/v1/group",
  "ApiKeyHeader": "X-Api-Key",
  "TimeoutSeconds": 15,
  "AllowInsecureHttp": false
}
```

- `BaseUrl` empty (the default) means no licensed grouper. Every request then
  answers `notConfigured`, and a method that needs a group pends the claim.
  Nothing ever falls back to a group the hospital billed.
- Put `ApiKey` in a secret store (`LicensedGrouper__ApiKey`), never in a
  settings file.
- Requests carry diagnoses, age and sex, so plain `http` is refused at startup
  unless the host is loopback or `AllowInsecureHttp` is set for a sidecar on a
  private network.
- A `BaseUrl` that is set but unusable stops the host at startup.

## APR-DRG

`POST {BaseUrl}/apr-drg/v1/group`

Request:

```json
{
  "claimReference": "clm-8c1f",
  "principalDiagnosis": { "code": "J189", "presentOnAdmission": "Y" },
  "secondaryDiagnoses": [ { "code": "N179", "presentOnAdmission": "N" } ],
  "admittingDiagnosis": "R0602",
  "procedures": [ { "code": "0BH17EZ", "date": "2026-10-12" } ],
  "admissionDate": "2026-10-11",
  "dischargeDate": "2026-10-15",
  "ageYears": 67,
  "ageDays": 24601,
  "sex": "F",
  "dischargeStatus": "01",
  "birthWeightGrams": null,
  "lengthOfStayDays": 4,
  "requestedVersion": "40",
  "billedDrg": "139"
}
```

- Codes have no decimal point.
- `claimReference` is CHO's id for the claim and carries no PHI. Echo it into
  your logs, not back in the response.
- `requestedVersion` is the APR-DRG version the state prices this discharge
  under. States adopt versions on their own schedule, so the grouper must run
  that version, or refuse. If it is null, the grouper chooses.
- Fields with no value are omitted.

Response:

```json
{
  "grouped": true,
  "returnCode": "0",
  "aprDrg": "139",
  "soi": 3,
  "rom": 2,
  "mdc": 4,
  "version": "40.0",
  "grouperName": "3M APR-DRG",
  "messages": []
}
```

A refusal by the grouper (invalid principal diagnosis, ungroupable stay) is
still HTTP 200 with `"grouped": false`, its own `returnCode` and `messages`.
CHO records both on the claim's pend reason.

CHO accepts a grouped answer only when:

- `aprDrg` is exactly three digits;
- `soi` is 1 to 4, and `rom`, if present, is 1 to 4;
- `version` is the requested one. `40` matches `40` and `40.0`, but not `41` or `400`.

## EAPG

`POST {BaseUrl}/eapg/v1/group`

Request:

```json
{
  "claimReference": "clm-91d2",
  "principalDiagnosis": "S52501A",
  "secondaryDiagnoses": ["W010XXA"],
  "reasonForVisitDiagnoses": ["M25531"],
  "typeOfBill": "0131",
  "fromDate": "2026-10-01",
  "throughDate": "2026-10-01",
  "ageYears": 34,
  "sex": "M",
  "dischargeStatus": "01",
  "requestedVersion": "3.18",
  "lines": [
    { "lineNumber": 1, "revenueCode": "0450", "procedureCode": "99284", "modifiers": ["25"],
      "units": 1, "chargeAmount": 1840.00, "serviceDate": "2026-10-01" },
    { "lineNumber": 2, "revenueCode": "0320", "procedureCode": "73090", "modifiers": [],
      "units": 1, "chargeAmount": 410.00, "serviceDate": "2026-10-01" }
  ]
}
```

Response:

```json
{
  "grouped": true,
  "returnCode": "0",
  "version": "3.18",
  "grouperName": "3M EAPG",
  "messages": [],
  "lines": [
    { "lineNumber": 1, "eapg": "00500", "eapgType": "Medical visit", "eapgCategory": "Emergency",
      "action": "payable", "discountFactor": 1.0, "visitNumber": 1, "messages": [] },
    { "lineNumber": 2, "eapg": "00421", "eapgType": "Ancillary", "action": "packaged",
      "visitNumber": 1, "messages": [] }
  ]
}
```

`action` is one of `payable`, `discounted`, `packaged`, `consolidated` or `notPayable`.

CHO accepts a grouped answer only when:

- every requested `lineNumber` appears exactly once, with no extra lines;
- each `payable` or `discounted` line carries an `eapg`;
- a `payable` line has `discountFactor` 1 (or omits it);
- a `discounted` line has `discountFactor` strictly between 0 and 1;
- `version` is the requested one.

The factor on `packaged`, `consolidated` and `notPayable` lines is ignored and
recorded as 0, because those lines pay nothing separately.

## What CHO does with each outcome

| Outcome | When | Effect once a pricing method uses it |
| --- | --- | --- |
| `grouped` | Answer passed every check | Priced from the state's weight table |
| `notConfigured` | `BaseUrl` empty | Claim pends |
| `rejectedByGrouper` | `"grouped": false` | Claim pends with the grouper's return code and messages |
| `unavailable` | Non-2xx, timeout, connection failure | Claim pends; a re-run can succeed |
| `invalidResponse` | Unreadable JSON or a failed check | Claim pends |

## Writing the adapter

The adapter is the plan's code, or its grouper vendor's, because it links the
licensed grouper library. It should:

1. Map the request onto the grouper's input record, including POA indicators
   and the requested version.
2. Return the grouper's own return code and messages unchanged.
3. Answer the stated version or refuse. Never substitute a newer one.
4. Keep PHI out of logs except where the plan's HIPAA logging policy allows it.

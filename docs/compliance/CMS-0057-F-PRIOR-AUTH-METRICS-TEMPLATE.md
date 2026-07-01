# CMS-0057-F Prior Authorization Metrics Template

**Status as of:** July 2026
**Purpose:** provide a payer-ready template for annual public
prior-authorization metrics and the supporting pilot data collection plan.

This template is not legal advice. Payers should confirm final metric
definitions, exclusions, aggregation level, publication format, and dates with
their compliance counsel.

## Regulatory Anchor

CMS states that certain operational/process prior-authorization policies begin
in 2026. The Federal Register final rule requires impacted payers to publicly
report prior-authorization metrics by March 31 for the previous calendar year,
with aggregation level varying by payer type. The final rule excludes drugs from
these prior-authorization metrics.

The recurring required metrics include:

- List of items and services requiring prior authorization.
- Percentage of standard requests approved.
- Percentage of standard requests denied.
- Percentage of standard requests approved after appeal.
- Percentage of requests with an extended review timeframe that were approved.
- Percentage of expedited requests approved.
- Percentage of expedited requests denied.
- Average and median elapsed time from submission to determination/decision for
  standard requests.
- Average and median elapsed time from submission to decision for expedited
  requests.

## Publication Header Template

| Field | Value |
| --- | --- |
| Payer legal name | |
| Reporting entity level | MA contract / state / plan / issuer |
| Line(s) of business | |
| Calendar year reported | |
| Publication date | |
| Data extraction date | |
| Exclusions | Drugs; other payer-approved exclusions |
| Source systems | |
| Compliance reviewer | |
| Notes | |

## Public Metrics Table

| Metric | Numerator | Denominator | Value | Source field(s) | Reconciled? |
| --- | --- | --- | --- | --- | --- |
| Items and services requiring prior authorization | n/a | n/a | Link or attached list | UM policy repository | No |
| Standard requests approved | Standard requests approved | All standard requests | | Authorization status, level of service | No |
| Standard requests denied | Standard requests denied | All standard requests | | Authorization status, level of service | No |
| Standard requests approved after appeal | Standard requests approved after appeal | Standard requests appealed or all standard requests, per payer-approved definition | | Appeal outcome, original auth id | No |
| Requests with extended timeframe and approved | Requests extended and approved | All prior-auth requests or all extended requests, per payer-approved definition | | Extension flag, status | No |
| Expedited requests approved | Expedited requests approved | All expedited requests | | Authorization status, level of service | No |
| Expedited requests denied | Expedited requests denied | All expedited requests | | Authorization status, level of service | No |
| Standard elapsed time average | Sum elapsed time for standard requests | Standard requests with decision | | Submitted timestamp, decision timestamp | No |
| Standard elapsed time median | Median elapsed time for standard requests | Standard requests with decision | | Submitted timestamp, decision timestamp | No |
| Expedited elapsed time average | Sum elapsed time for expedited requests | Expedited requests with decision | | Submitted timestamp, decision timestamp | No |
| Expedited elapsed time median | Median elapsed time for expedited requests | Expedited requests with decision | | Submitted timestamp, decision timestamp | No |

## Pilot Data Collection Plan

| Data element | Required for | Candidate source | Owner | Status |
| --- | --- | --- | --- | --- |
| Authorization id | Traceability, dedupe | authorization-service / payer UM system | | Not started |
| Tenant / payer id | Multi-tenant separation | tenant middleware / payer config | | Not started |
| Line of business | Reporting scope | payer config | | Not started |
| Level of service | Standard vs expedited | X12 278, PAS Claim, UM system | | Not started |
| Submitted timestamp | Elapsed time | PAS submission, UM intake | | Not started |
| Decision timestamp | Elapsed time | UM decision event | | Not started |
| Status | Approved/denied/pended/modified | authorization-service / UM system | | Not started |
| Denial reason | Denial transparency | denial taxonomy / ClaimResponse | | Not started |
| Extension flag | Extended timeframe metric | UM workflow event | | Not started |
| Appeal id / outcome | Approved-after-appeal metric | appeals service / payer appeal system | | Not started |
| Item/service code | PA-required list and stratification | UM policy repository | | Not started |
| Drug exclusion flag | CMS-0057-F exclusion handling | benefit/UM classification | | Not started |

## Calculation Notes

- Calculate percentages as numerator divided by denominator, multiplied by 100,
  rounded according to payer-approved reporting policy.
- Define "standard" and "expedited" from payer-approved source fields before
  producing public values.
- Define whether modified/partial approvals count as approved, denied, or a
  separate internal category before publishing.
- Define whether withdrawn, duplicate, incomplete, and no-decision requests are
  excluded from denominators.
- Preserve an audit snapshot for every published value: query version,
  extraction timestamp, source-system identifiers, reviewer, and approval date.
- Keep internal stratifications by item/service, provider group, channel, and
  adapter mode for operations, even if public reporting is aggregated.

## Evidence Queries to Build

| Query | Output | Purpose |
| --- | --- | --- |
| Prior-auth request inventory | One row per request with status, LOB, level, timestamps, item/service, extension, appeal link. | Metric source table. |
| PA-required item/service list | Current policy list with effective dates. | Public list and policy reconciliation. |
| Denial reason audit | Denied requests with reason code, reason text, and correspondence mapping. | Denial transparency. |
| SLA breach audit | Requests over 72 hours expedited or 7 calendar days standard. | Operational remediation. |
| Appeal outcome join | Original authorization linked to appeal outcome. | Approved-after-appeal metric. |

## Publication Review Checklist

- [ ] Reporting entity level matches payer type and contract/plan structure.
- [ ] Calendar year and extraction date are visible.
- [ ] Drug exclusions and other approved exclusions are documented.
- [ ] Numerators and denominators are approved by compliance.
- [ ] Public values reconcile to source-system snapshot.
- [ ] Reviewer and approval date are recorded.
- [ ] Published file is accessible from payer website by the required date.
- [ ] Internal audit package is retained under payer retention policy.

## References

- CMS fact sheet:
  <https://www.cms.gov/newsroom/fact-sheets/cms-interoperability-prior-authorization-final-rule-cms-0057-f>
- Federal Register final rule:
  <https://www.federalregister.gov/documents/2024/02/08/2024-00895/medicare-and-medicaid-programs-patient-protection-and-affordable-care-act-advancing-interoperability>

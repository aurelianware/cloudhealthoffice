# Incident and Support Contacts — Founding-Partner Pilot

**Status as of:** September 2026
**Applies to:** paid CMS-0057-F Compliance Accelerator (Layer 1)
**These are targets for a founding-partner pilot, not a published enterprise SLA.** A custom SLA is an Order Form exhibit.

## Contacts

| Role | Channel |
| --- | --- |
| Sales / commercial | sales@cloudhealthoffice.com |
| Licensing | licensing@cloudhealthoffice.com |
| Security disclosure (non-public) | Follow [SECURITY.md](../../SECURITY.md) — do not file PHI in public GitHub issues |
| Pilot delivery owner | Named on the Order Form (CHO) |
| Customer technical contact | Named on the Order Form |
| Customer compliance owner | Named on the Order Form |

GitHub issues and Discussions are for non-PHI product defects only.

## Severity

| Severity | Meaning | Target response (pilot) |
| --- | --- | --- |
| S1 | FHIR / SMART outage in the named pilot environment, or suspected PHI exposure | 4 hours during CHO business hours; next calendar day otherwise |
| S2 | Adapter mis-label (Live header on Demo data), failed PAS `$submit` for the demo script, auth issuer down | 1 business day |
| S3 | Documentation, non-blocking defects, new fixture requests | 3 business days |

PHI exposure is handled under the BAA, not as a public issue.

## What we need in an incident report

- Tenant id
- Environment (synthetic demo vs named PHI environment)
- Adapter-status JSON from the time of the incident if available
- Correlation id / timestamp
- Whether PHI was involved (yes / no / unknown)
- Reproduction without PHI

## Business hours

Monday–Friday, US Eastern, excluding US federal holidays, unless the Order Form says otherwise. After-hours S1 is best-effort until a paid 24/7 exhibit exists.

## Customer obligations

- Do not open the synthetic demo tenant to the public internet with production credentials.
- Do not load PHI into `demo-tenant`.
- Rotate any credential that appeared in a ticket or screenshot.

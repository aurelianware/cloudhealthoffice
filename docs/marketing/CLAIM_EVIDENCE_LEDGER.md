# Marketing Claim-Evidence Index

**Reviewed:** October 19, 2026 (America/Phoenix).

[Capability Reconciliation](../CAPABILITY_RECONCILIATION.md) is the authoritative
claim-evidence ledger: use its CAP IDs, classifications, verification dates,
implementation/test paths and remaining limits. This file is a marketing usage
index, not a second capability-status table.

| Marketing topic | Canonical evidence |
| --- | --- |
| PAS submission, inquiry, service-line decisions | CAP-01, CAP-02 |
| CRD uncertainty handling | CAP-03 |
| DTR durable creation/read-back and incomplete update/search/package paths | CAP-04, CAP-09, CAP-15 |
| SMART launch context, approval withdrawal and scope handling | CAP-05, CAP-11 |
| Reporting, receipt deadlines and ordinary authorization lifecycle | CAP-06, CAP-07, CAP-23 |
| Tenant/payer binding and Mongo concurrency | CAP-08, CAP-10, CAP-12, CAP-21 |
| Claims PA-lookup failure handling | CAP-13 |
| Bulk export, default mock data sources, integration and model limits | CAP-14, CAP-16–20, CAP-22 |

## Copy controls

Describe verified implemented behavior directly. Do not repeat resolved historical
gaps or imply full standards conformance, production validation, autonomous AI,
certification, customer deployments or savings without evidence. BSL 1.1 terms
remain governed by [LICENSE](../../LICENSE); production use requires a commercial
license. Code includes advisory AI paths; the named fraud-model asset is a
placeholder (CAP-18), not proof that all AI capability is missing.

## Source and runtime alignment

Messaging sources are [MESSAGE_SHEET.md](../../src/site/MESSAGE_SHEET.md) and
[assistant knowledge](../../src/site/assistant/knowledge.md). The runtime assistant
is hardcoded; it does not automatically ingest those Markdown files. Required
future JavaScript/HTML copy changes are tracked in the canonical ledger's final
review. No runtime changes or publication are included in this PR.

## Maintenance

Before strengthening a claim, update the canonical ledger with current evidence,
commit, verification date and limits, then review the affected copy. Preserve
historical assessments with a dated supersession note rather than copying their
statuses into a new table.

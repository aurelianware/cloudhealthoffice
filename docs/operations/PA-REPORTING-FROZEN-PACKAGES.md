# Frozen PA reporting packages

Baseline: merged #1549, main `14e4691a4287d370e51eda7a033ffaff3aaf6fc9`.
Proposed [ADR 033](../adr/033-pa-reporting-frozen-packages.md) extends
[ADR 030's annual draft](PA-ANNUAL-REPORTING-EVIDENCE.md).

## API contract

`POST /api/authorizations/cms-0057/packages` freezes a private, unapproved package.
Require both `authorizations:read` and `authorizations:reporting-freeze`. No role
mapping is changed; grant the capability deliberately to reporting stewards.
The service must be CHO-native/authoritative; Augment never falls back to CHO.

Synthetic example, not accepted plan mapping or approved reporting policy:

```json
{
  "requestKey": "annual-2025-review-1",
  "planId": "synthetic-plan",
  "planScopeReference": "mapping:review-1",
  "year": 2025,
  "timeZone": "America/Los_Angeles",
  "lineOfBusiness": "Medicaid",
  "outcomeCutoff": "2026-03-01T00:00:00Z",
  "knowledgeCutoff": "2026-03-01T00:00:00Z"
}
```

Both cutoffs are required, normalized to UTC milliseconds, and must fall between
the cohort start and server time. The existing local-calendar cohort and receipt/
final-outcome/initial-timing definitions remain unchanged. Captured facts after
the cutoffs do not silently enter results. Legacy evidence stays diagnostic.
The plan ID/reference declares a proposed mapping; it does not filter multiple
plans within the same tenant/LOB or prove they should be combined. Every response
retains `PublicationReady=false`, `PlanMappingVerified=false`, `ApprovalStatus=Unapproved`.

An exact key/scope/cutoff replay returns the original package/recorder/hash even
after later source changes or retention. Changed scope under that key is 409.
Restatements require another request key and retained review/reconciliation history.
Both parties in a same-key race receive the committed winner when available;
transient infrastructure failure may still require an exact retry.

`GET /api/authorizations/cms-0057/packages/{id}` retrieves the protected header.
`GET /api/authorizations/cms-0057/packages/{id}/inputs?afterOrdinal=-1&pageSize=100`
retrieves retained minimal inputs, ordered by zero-based ordinal, with page sizes
1–250. Continue after the last ordinal until the header's input count is reached.
All endpoints require both permissions; GET access is audited and responses use
`Cache-Control: no-store`. Missing/other-tenant package IDs are 404. Neither route
publishes anything or follows the submitted evidence reference as a URL.

## Verification and failure behavior

- Header hash covers identity/scope, build/format, recorder/time, input count/bytes,
  input aggregate hash and exact canonical report JSON. Input row hashes cover the retained
  projection. Aggregate input hash is SHA-256 over concatenated uppercase ASCII
  row-hash strings in ordinal order; the empty stream has its normal SHA-256 hash.
  `ReportJson` retains the exact versioned output, including computed labels/gaps;
  `FrozenReport` exposes that original JSON document. The internal typed `Report`
  helper is not persisted or exposed as the evidentiary output.
- `PaReportingPackageHash` defines version-1 compact System.Text.Json byte encoding
  and field order. Reproduce with the matching build/version or preserve exact
  canonical bytes in the controlled evidence export. A later serializer/model
  change requires a format-version and migration/reader design; do not rewrite old
  packages to make a new checker pass.
- GET checks the header hash; input reads check each row and expected ordinal/count.
  Verify the entire ordered input hash stream against the header before accepting a
  complete export. Per-page success is not complete-package validation. These are
  hashes, not externally signed certificates or regulatory acceptance.
- Scope validation returns 400; key reuse with different content returns 409;
  bounded-size refusal returns 422 with no partial package; unsupported transaction
  topology/infrastructure and integrity failures return 503. Retry exact keys for
  transient failures; investigate integrity failures and never treat them as passes.
- `PaReportingPackages:MaxRecords` defaults to 100000; `MaxInputBytes` defaults to
  134217728. Individual inputs above 8 MiB also abort. The transaction may time out
  earlier on a managed deployment. No path silently samples/truncates the cohort.

## Deployment, ownership and acceptance

New collections `PaReportingPackages` and `PaReportingPackageInputs` belong only to
authorization-service. Freeze creates their indexes before the transaction. Ordinary
PA creation/decision paths do not acquire new transaction requirements. Snapshot
creation does require a transaction-capable replica set/sharded deployment; there
is no standalone fallback. Source writers continue normally under snapshot isolation.

Retained authorization IDs, evidence references and recorder identities are private
operational data. Restrict DB/endpoint access, protect backup/restore, approve
retention/legal hold and arrange reconciliation before treating packages as report
records. Packages are not automatically deleted when original authorizations purge.
No public git artifact should contain real package data.

For this 250k-member plan, test actual annual PA volume and fact sizes, transaction
lifetimes, managed Mongo topology/failover and recovery. Membership does not establish
that a 100k-input bound or one transaction can hold the annual reporting workload.

Next acceptance work remains authoritative all-channel intake and appeal/extension
sources, source-to-package reconciliation, actual plan mapping, service inventory,
approved timing/restatement definitions, two-person publication review, a public
artifact and DHCS-specific quarterly/annual/QIC deliverables. Freeze is not approval.

## Validation record

New tests cover retained-input replay/hashes, no full member-detail retention, later
appeals and source deletion, concurrent duplicate keys, tenant separation, record/
byte-limit transaction abort, tampered headers/inputs/missing ordinals, legacy/empty
cohorts and standalone refusal. Scope/hash cases and real-host permission/recorder/
error cases accompany existing reporting regressions in `pa-reporting-packages.yml`.

Local SDK 8.0.426 again fails before build on sandbox process metadata. No successful
local executable test run is claimed. The focused CI result below supplies executable evidence for its pinned source.
Diff/link checks are separate from runtime proof; no regulator, DHCS, bank or public
website submission occurs in this change.

### Focused CI evidence

[PA Reporting Packages run 40220718946](https://github.com/aurelianware/cloudhealthoffice/actions/runs/40220718946)
on source commit `17880d3452545c930fa306d0c239dfb12121c1ee` built successfully and
passed **78 standard VSTest cases**: 63 calculation/persistence cases and 15 real-host
HTTP cases, zero failures. The selection includes real replica-set freeze/replay,
concurrent-key and rollback/tenant/tamper/standalone cases, exact retained output,
existing reporting regressions, both permissions, token recorder, no-store and
private-read audit behavior. Earlier retry-context and HTTP fixture issues were
corrected before this run. Documentation-only follow-ups do not constitute a new
execution. Broader CI and managed-cloud/annual-volume/source acceptance remain
separate; no production or public regulatory acceptance is claimed.

Interpretation: [CMS reporting/process FAQ](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/frequently-asked-questions/improving-prior-authorization-processes),
reviewed October 25, 2026. It distinguishes individual requests, plan-level reporting
and final outcomes across appeal levels. It does not mandate this private package
format or approve this calculator's publication semantics.

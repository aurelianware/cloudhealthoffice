# Production acceptance workstream

Target: **CHO as the native payer core** (owner-selected October 22, 2026).
Source planning baseline: October 22, 2026, main dc23d47e. This is a staged
engineering/acceptance plan, not evidence that the work is already complete.
The [capability ledger](../CAPABILITY_RECONCILIATION.md) governs current claims.

| Order | Work | Required evidence before closure |
| --- | --- | --- |
| 1 | Merge #1485–1487 after green checks; reconcile the current ledger | Actual merge commits; no required failing/running checks; retain original test baselines and scoped limitations |
| 2 | Production source configuration | FHIR boots with all sources native and synthetic permission false; any selected mock still fails outside approved demos. Eligibility runs without fabricated historical snapshots. Native failures remain explicit; no fallback to synthetic data. |
| 3 | CMS reporting | Track linked PA appeals and legitimate extensions at authoritative write paths; denominator/period/type rules and all-channel coverage tested; generate an annual public artifact with reconciliation. No invented zero values for legacy untracked data. Patient Access unique-member/repeat-use reporting is separate. |
| 4 | Durable exchange | Persist asynchronous jobs, scope members/groups and output types, generate actual NDJSON, authorize download/expiry and prove restart/retry/revocation behavior. Complete clinical and structured/unstructured PA document export; validate deployed profiles, terminology and confinement. A 501 boundary is not closure. |
| 5 | Financial lifecycle | Replay invoice, cash application, settlement, return, capitation, reversal and reissue through subledgers/GL/ERP. Stable business keys, durable outbox, balanced entries, duplicate handling and period boundaries must be verified. Define the source of due dates/open items before rewriting AR aging; account-period net balances alone cannot establish invoice age. |
| 6 | Core replacement qualification | Replay a representative plan/contract/claim corpus with dollar-level expected results; migration balances and history reconcile; prove throughput, concurrent reservations, recovery and partner acceptance. No production data is committed to this repository. |

Large financial/exchange changes need an ADR or scoped issue before broad
rewrites (CONTRIBUTING.md). Split implementation by durable data boundary and
keep acceptance failures visible. Public positioning changes follow demonstrated
capability, not a planned milestone or aggregate test count.

## External core integration is engagement-scoped

QNXT/Facets/HealthEdge adapter implementation is intentionally tailored to the
payer's version, configuration, licensed interfaces, hosting and solution
architecture using the [existing QRE](../features/questionnaires/qre-core-admin-adapter-questions.md).
Do not make a universal adapter a prerequisite for native CHO acceptance.
Incumbent comparison data can be supplied through an agreed extract/replay
contract; direct connectivity is separately scoped if the payer requires it.

# Receipt-based authorization decision deadlines

CHO persists `decisionReceivedAt`, `decisionDueAt` and
`decisionDeadlinePolicy=cho-receipt-v1` when an authorization is created.
The receipt is the original `submittedDate`; PAS supplies the receipt captured
before mapping/adjudication. Public intake stamps receipt on entry and cannot
choose its own deadline.

The operational target remains 72 elapsed hours for expedited (`U`, ignoring
case) and seven calendar days for standard requests, in UTC, including weekends
and holidays. This preserves the existing CHO target while making it durable.
It is not a declaration that every LOB or drug request has that statutory ceiling.
CMS excludes FFE QHP issuers from these decision-timeframe requirements; drugs,
shorter state rules and program-specific extensions require separate policies.
See [CMS decision-timeframe guidance](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/frequently-asked-questions/improving-prior-authorization-processes).

RFAI/document arrival can return a pended authorization to review and retain the
legacy `slaResumedAt` timestamp for compatibility. It never pauses, extends or
restarts the receipt-based decision clock. Turnaround summaries likewise include
time waiting for documents.

The SLA endpoint and watchdog use the persisted target. Warning/critical levels
remain 24/8 hours before an expedited deadline and 48/24 hours before a standard
deadline. Reaching the deadline marks a breach; it does not fabricate a clinical
approval or denial. Alert delivery is separate (G-19).

Legacy open records are initialized on a watchdog sweep, using the original
receipt rather than deployment time. The write is tenant-explicit, revision-fenced
and changes only deadline metadata plus revision. A concurrent writer causes a
retry on a later tick. Ordinary document updates also initialize missing metadata.
Existing records can already be overdue at initialization; that breach is real.
Reads without stored metadata use the receipt policy as a compatibility fallback.

The watchdog's existing limitation for `MongoDb:UseTenantScoping=true` remains
(D-38): that background mode is disabled until tenant-database enumeration is
implemented. New intake still stores deadlines in that mode. No production
backfill command or deployment was executed by this change.

This closes the clock-reset defect D-09 and the persisted-target portion of G-18.
Program-specific extension eligibility, a controlled extension operation, extension
metrics, and a complete set of state/LOB deadline rules remain open. No extension
is silently granted by this implementation.

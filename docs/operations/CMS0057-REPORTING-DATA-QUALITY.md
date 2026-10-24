# CMS-0057 reporting data quality

The authorization-service `cms-0057/metrics` endpoint is an authenticated
**operational receipt-cohort report**, not a complete annual public report.
`ReportingBasis` states this explicitly. Publication must use a separately
reconciled annual artifact; an authenticated portal does not satisfy public
publication.

The calculator excludes drug authorizations using the existing
`PriorAuthorizationApiScope` policy. That policy is request-level: mixed
medical/drug requests are excluded as a whole; line-level allocation is not
implemented. Validate plan coding/classification before public reporting.

The period and elapsed time use persisted `DecisionReceivedAt`, falling back
to `SubmittedDate` for legacy records, as the deadline policy already does.
Missing receipt or decision timestamps, or a decision before receipt, yield
unavailable timing rather than a fabricated zero-hour response.
`DecisionTimingUnavailable` counts affected decided requests. They remain in
outcome counts; the mean/median and the optional timeliness percentage use
only measurable times. Reconcile that missing-data count before publication.

Approval/denial percentages use all received requests of their respective
standard/expedited type, including undecided requests, following the expected
population in CMS's reporting template. Empty populations return null; a
nonempty population with no approvals returns zero. This changes the prior
"percentage of decided requests" contract. The portal explanation is aligned.
These percentages need not add to 100 while requests are open.

Appeal-linked outcomes, legitimate review extensions, final annual outcome
cohorts, historical as-of reconstruction, the public items/services list and
publication remain open. The endpoint's `NotYetTracked` list remains present;
its existence is not a complete conformance inventory. Do not publish the
current receipt cohort unchanged as a compliant annual report. Do not infer
that an untracked appeal or extension had a zero count.

No destructive migration is required. Historical documents are not rewritten.
The reporting query also selects records received during the interval, while
retaining its existing submitted/decided predicates for other consumers.
Validate the new query's index/query plan against the deployment's actual
MongoDB-compatible store before operating at scale.

Sources reviewed October 24, 2026:

- [CMS metrics overview and template](https://www.cms.gov/prior-authorization-metrics-reporting-overview-template)
- [CMS process and reporting FAQ](https://www.cms.gov/initiatives/burden-reduction/overview/interoperability/frequently-asked-questions/improving-prior-authorization-processes)

The template supplies expected denominators; its format is recommended, not a
separate regulation. Requirements, deployment acceptance and standards
validation remain separate from these arithmetic fixes.

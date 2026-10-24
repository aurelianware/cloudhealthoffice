# PCP panel capacity unavailable

`HttpPanelCounter` now reports `PanelCountUnavailableException` when its source
is unconfigured, refuses the call (including 404), fails, times out or returns
an unreadable/null body. Only a valid empty roster measures zero. Caller
cancellation remains cancellation.

When an assignment needs a configured `PanelLimit`, this failure propagates
before old assignments are ended or a new assignment is inserted. The API
answers 503 with a retryable capacity-unavailable message. An outage therefore
cannot bypass a panel limit by appearing to be an empty panel. Assignments
whose participation has no panel limit do not call the counter.

Configure and verify the actual roster source and service credentials before
rollout. The current HTTP counter requests coverage-service's
`/api/v1/coverage/by-pcp/{npi}` contract. A URL labelled capitation-service is
not evidence that host implements this contract; configure a host that does.
Restoring a healthy source restores assignment without data migration.

This mitigates unavailable-data handling only. The read-before-insert race,
count scope/effective dating, overlapping member coverages and unscheduled
panel reconciliation remain open. Do not describe this change as atomic
capacity reservation or full California network-adequacy qualification.

# ADR 030: PA reporting evidence and annual drafts

## Status

Proposed. Scoped implementation included for review; this does not approve a
regulatory publication policy or silently accept this ADR.

## Context

The operational CMS console reads mutable authorizations. It cannot reproduce
what was known at a historical cutoff, infer whether a decision followed appeal,
or prove a lawful review extension. Claim appeals in appeals-service require
claim identities and are not evidence of PA appeals. ADRs 006/010/017 favor
domain-owned Mongo evidence without imposing full event sourcing.

## Decision

1. Embed a server-owned `ReportingLedger` in each newly created native
   authorization. Capture receipt, request type, drug scope and LOB once. Append
   native determination evidence in the same revision-fenced document write as
   its outcome. Ordinary full-model HTTP input/output excludes the ledger;
   repository replacements preserve stored evidence rather than trusting callers.
2. Record **attested PA reporting facts** through a separate endpoint requiring
   both decision and reporting-evidence permissions. Source system, idempotency
   key, effective time, durable opaque evidence reference and token actor are
   required. Exact retries retain the original recorder/time; conflicting keys
   and stale writes fail. No existing role receives the new permission implicitly.
3. This endpoint does not adjudicate an appeal, fulfill a notice, grant a legal
   extension, alter the clinical authorization, change the watchdog deadline, or
   convert a claim appeal into a PA appeal. Actual PA source integrations and
   their all-level completeness still need acceptance contracts.
4. An authenticated annual **draft** streams a tenant/LOB receipt cohort using
   an explicit calendar time zone and exclusive year end. It filters effective
   outcome time and server recording time independently. Final evidenced appeal
   outcomes count the original request once; initial determination turnaround is
   a separately named diagnostic. Equal-time contradictory final outcomes are
   unresolved, not guessed from arrival order.
5. Legacy documents without a ledger and unsupported versions produce
   diagnostics, not fabricated receipt/appeal history or zero-valued metrics.
   No migration asserts missing evidence. Tenant/LOB is not silently equated to
   a statutory plan. Every response remains `publicationReady: false`.

## Consequences and limits

Single-document atomicity avoids a state/event dual write and fits existing
optimistic concurrency. Evidence is not a universal event-sourced core. Query
reads are batched and only timing samples are retained for exact median; a
million-request benchmark and managed-Mongo acceptance remain unperformed.

The ledger lives under the authorization's existing retention boundary. An
approved report artifact, correction/supersession protocol, archival evidence,
all-channel source reconciliation, plan mapping, required-services inventory and
compliance-approved reporting definitions remain separate work. A cursor is not
a multi-document snapshot. Recording timestamps are assigned before the Mongo
write, not at its commit; an in-flight write can become visible after a report
with the same cutoff. Trusted clocks and retained facts support investigation,
but approved reproducibility requires a reconciled persisted freeze.

See [the runbook](../operations/PA-ANNUAL-REPORTING-EVIDENCE.md) for contracts,
validation and rollout. This changes no bank/state submission or public website.

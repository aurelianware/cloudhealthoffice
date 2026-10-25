# ADR 033: Private frozen PA reporting evidence packages

## Status

Proposed; scoped implementation for review. This does not approve a regulatory
report, change ADR 030's calculation semantics or assert CMS-0057-F conformance.

## Decision

Authorization-service owns insert-only private reporting packages and retained
minimal inputs in Mongo (ADRs 006/017/030). Freeze reads the same tenant/LOB receipt
cohort as the annual draft, in deterministic authorization-ID order, through a
snapshot/primary/majority transaction. It retains authorization identity/revision,
reporting ledger and legacy scope/time diagnostics, not the full clinical/member
record. Input rows and the final aggregate/header commit together or not at all.
No partially written manifest is returned. Unsupported topology refuses the freeze.

The package records explicit year, time zone, effective/knowledge cutoffs, declared
plan and opaque mapping reference, recorder, build/calculator/format versions,
input count/bytes and SHA-256 hashes. Tenant and recorder come from authenticated
context. A tenant-scoped request key identifies one scope/cutoff tuple; exact replay
returns the existing package even if source data changed. Changed scope under that
key conflicts. A later restatement uses a new key; it does not overwrite history.

Both authorization read and a new reporting-freeze permission protect creation and
retrieval. The native authoritative backend is required. Input retrieval is paged,
tenant-scoped and checks row hashes/ordinal completeness; header retrieval verifies
its hash. Responses are not cacheable. There is no public endpoint, update, delete,
approval or publication operation. A declared plan mapping is explicitly unverified;
`PublicationReady` remains false. No permission is added to role mappings.

## Limits and consequences

Application immutability and hashes do not provide a digital signature, WORM storage,
or protection against a database administrator rewriting both content and hashes.
Deployment must restrict database writes, approve retention/legal hold, retain build
provenance and protect the source references and authorization IDs as sensitive.
Clients verifying a complete package must reconstruct the ordinal input hash stream
and compare it to the header; checking one page is not whole-package verification.

Defaults bound a freeze to 100,000 inputs, 128 MiB of serialized input and 8 MiB per
input. Exceeding a limit aborts, never truncates. Managed Mongo compatibility,
transaction duration and annual-volume performance for a 250k-member plan require
qualification; raising limits is not that qualification. Transaction reads identify
the exact retained snapshot, not every write whose application timestamp preceded
a cutoff. In-flight/later commits require source reconciliation and new restatements.

This closes the missing **persisted reproducible draft snapshot** mechanism only.
It does not supply non-API intake, PA appeals, lawful extensions, notices, accepted
plan mapping, required-services inventory, all-level/channel reconciliation,
reporting-definition approval, DHCS templates or approved public publication.

See [operation and validation](../operations/PA-REPORTING-FROZEN-PACKAGES.md).

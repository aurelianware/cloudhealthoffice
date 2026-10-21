# Document catalog and attachment isolation v1

This .NET 8 profile aligns with the catalog introduced by PR #1439 and the
attachment gateway in PR #1440. It runs both real Kestrel services, real MongoDB
repositories/access audit, and real uploaded/downloaded synthetic bytes. It
accepts assembly paths and a new evidence directory, never a remote target.

The byte store is the existing Development-only InMemory provider. Scan state
is an explicit Mongo fixture: the registered NoDocumentScanner stays unchanged.
Pending, Infected and Failed must refuse; a synthetic Clean state permits a
byte-for-byte download. This proves the gate, not malware detection. Both hosts
use Development CHO user/service identities. No production guard is weakened.

## Alignment with the document-store changes

- PR #1439 (`68191269`) adds tenant-and-subject catalog reads, exact-only sensitive
  permission, Part 2 withholding, scan gating, legal hold and append-only access
  audit. Test these through HTTP with actual persisted catalog records.
- PR #1440 (`310a41d9`) routes attachment downloads through the catalog using a
  service token. Test the user's `documents:read` and parent-read requirements at
  the gateway, the persisted document/parent binding, catalog refusal status,
  and audit attribution to `attachment-service` **and** the initiating user.
- An on-behalf-of value cannot grant rights. A user-supplied spoof must not be
  recorded as a delegated identity; malformed service-supplied identity refuses.
- Attachments are existing-state Mongo fixtures referencing HTTP-uploaded catalog
  documents. Attachment HTTP intake, parent lookup and failure rollback remain
  outside this campaign; the existing attachment regression suite runs alongside.

## Exact catalog: 39 scenarios

| Cases | Assertions |
| --- | --- |
| `catalog.upload-control`, `catalog.tenant-b-control`, `catalog.download-control`, `catalog.metadata-control` | HTTP multipart uploads; separate tenant content; exact downloaded bytes with attachment/nosniff headers; metadata omits storage location. |
| `catalog.anonymous`, `catalog.bad-token`, `catalog.tenant-conflict` | Identity and token/header tenant binding. |
| `catalog.foreign-document`, `catalog.wrong-subject`, `catalog.missing-context`, `catalog.subject-permission`, `catalog.document-permission` | Cross-tenant IDs, unlinked subjects, missing context and missing rights cannot return bytes. |
| `catalog.pending-denial`, `catalog.infected-denial`, `catalog.failed-denial`, `catalog.clean-restoration` | Scan-state gate and healthy restoration. |
| `sensitive.exact-control`, `sensitive.reader-denial`, `sensitive.wildcard-denial`, `sensitive.part2-denial` | Explicit sensitive permission works; ordinary/wildcard access refuses; Part 2 remains closed even to explicit sensitive readers. Refusals do not expose tag names or content canaries. |
| `sensitive.list-withholding`, `sensitive.hidden-dedup` | Listings withhold restricted document IDs; duplicate bytes cannot attach the caller to a hidden sensitive record or modify its links. |
| `upload.signature-denial`, `upload.size-denial` | Invalid PDF signature and configured 4 KiB lab limit refuse before changing catalog/attachment state. The production 64 MiB boundary is not claimed tested. |
| `hold.set-control`, `hold.link-denial`, `hold.release-reason` | Hold reply excludes full metadata; held links cannot change; release requires a reason. |
| `audit.user-spoof`, `audit.service-header-invalid` | Delegation cannot be spoofed by a user or silently dropped for a malformed service header. |
| `attachment.download-control`, `attachment.foreign-tenant`, `attachment.document-permission`, `attachment.subject-permission`, `attachment.wrong-parent`, `attachment.sensitive-denial` | Real service-to-service download, gateway permissions, tenant/parent binding and catalog refusal pass-through. |
| `attachment.audit-identity` | Persisted successful download audit binds tenant, document, subject, service actor and initiating user. |
| `audit.available-control`, `audit.outage-denial`, `audit.recovery-control` | After proving audit availability, an owned Mongo validator rejects audit inserts while storage/catalog remain healthy. A content request returns 500 without bytes or catalog changes; restoring the validator restores exact-byte downloads. |

Denied requests require expected HTTP status, absent content canaries/tag names,
and unchanged catalog/attachment snapshots. Audit writes are excluded from these
snapshots because refused requests should be audited. The validator fault is
restored in `finally`; there is no production fault-injection switch.

## Reproduce

From the repository root, with .NET 8, Python 3.12+ and MongoDB download access:

```sh
dotnet build src/services/document-service --configuration Release
dotnet build src/services/attachment-service --configuration Release
dotnet test tools/SecurityChallenge.Tests --configuration Release
dotnet test src/services/document-service/DocumentService.Tests --configuration Release
dotnet test src/services/attachment-service/AttachmentService.Tests --configuration Release

dotnet tools/SecurityChallenge/bin/Release/net8.0/SecurityChallenge.dll documents \
  src/services/document-service/bin/Release/net8.0/document-service.dll \
  src/services/attachment-service/bin/Release/net8.0/attachment-service.dll \
  /tmp/cho-document-isolation-new

# Clean committed checkout required; runs a healthy baseline first.
python3 tools/SecurityChallenge/run-fhir-sensitivity.py documents \
  src/services/attachment-service/bin/Release/net8.0/attachment-service.dll \
  /tmp/cho-document-isolation-sensitivity-new
```

Two disposable source archives separately bypass catalog sensitivity and the
attachment gateway's document-read requirement. Each mutation must match one
anchor, compile successfully, fail its specified negative cases, retain healthy
positive controls and report exactly 39 PASS/FAIL outcomes. Setup errors,
inconclusive cases and missing cases never count as detection. The checkout and
production configuration stay untouched.

Manifest/source/binary fingerprints, status/body hashes, state and audit
assertions, exact results, checksums and review-only knowledge-base drafts follow
the existing evidence format. No tokens or raw response bodies are retained.
Exit 0 needs all 39 expected cases and completed cleanup; execution/setup errors
return 1; bad invocation returns 2. Finding-bearing packets stay private pending
SECURITY.md triage. CI runs baseline and mutants without publishing artifacts.

## Explicit gaps

The current FHIR bulk-export controller supports kickoff/status/cancellation,
while BulkExportService produces output URLs; there is no implemented bulk-file
download route in this implementation. Existing FHIR/SMART evidence tests job
metadata only. Neither this campaign nor the earlier profile proves secure
NDJSON delivery, row confinement, signed-link expiry or cancellation of files.
Implement that delivery boundary before describing it as tested.

Also unsupported: cloud object ACLs and direct signed URLs, pre-signed upload
finalization/races, real malware scanning, attachment HTTP intake/rollback,
production identity/TLS/ingress, storage encryption, distributed concurrency and
comprehensive operational PHI detection. Passing this local profile is not
production certification or a finding that either referenced PR is vulnerability-free.

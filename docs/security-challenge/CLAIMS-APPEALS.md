# Claims and appeals security campaign

A separate **47-scenario, .NET 8** profile of the Open Security Challenge. It
starts the actual claims-service and appeals-service Kestrel processes and one
disposable shared MongoDB 7 database (tenant row filters), with two synthetic payers sharing natural claim,
appeal and member identifiers. Each payer has a distinct patient canary.
Positive controls are part of the scenario denominator, not additional attacks.

## Reproduce

From a clean source checkout with the pinned .NET 8 SDK:

```sh
dotnet build src/services/claims-service --configuration Release
dotnet build src/services/appeals-service --configuration Release
dotnet test tools/SecurityChallenge.Tests --configuration Release
dotnet tools/SecurityChallenge/bin/Release/net8.0/SecurityChallenge.dll claims-appeals \
  src/services/claims-service/bin/Release/net8.0/claims-service.dll \
  src/services/appeals-service/bin/Release/net8.0/appeals-service.dll \
  /tmp/cho-claims-appeals-unique-run
```

Existing output paths are refused. Unix evidence directories are owner-only;
Windows uses inherited filesystem permissions. Native MongoDB is downloaded by
EphemeralMongo on first use; Docker is not required. No remote-target option,
production data, external credentials or deployment is used.

Claims runs in **Testing** with explicit `CHO_TESTING=1`. Appeals runs in
**Development**, from a separate empty, owned temporary content root. This
selects the appeals host's existing Development-only NoOp field encryptor;
checked-in settings would otherwise enable key-backed encryption with no local
key provider. No production encryption guard or service configuration is
changed. This profile **does not test encryption at rest or key custody**.
Both processes trust public development identities, bind loopback only, receive
an allowlisted environment, disable Kafka delivery and OTLP, and use loopback
service URLs. Appeals inline/background outbox dispatch is disabled so persisted
business-event assertions are stable. This is not an OS network sandbox; use
trusted binaries on a disposable developer/CI host.

Claims are **direct-Mongo existing-state fixtures**, not claims submitted through
intake or adjudication: initially Received, no paid amounts or accumulator work.
Appeals are created through HTTP with references to each payer's seeded claim.
The campaign does not establish that appeals-service validates those references
against claims-service. It tests each service's actual HTTP boundaries and state
against the shared synthetic data set, not a complete claim-to-appeal integration.

## Catalog v1

Exact IDs: `ClaimsAppealsCampaign.ScenarioIds` in
[the executable catalog](../../tools/SecurityChallenge/ClaimsAppealsCampaign.cs).
Admin, read-only and worker identities are separate signed subjects. The worker
has claims:read/work and appeals:read/write, but no claims:void or appeals:admin.

| Scenario IDs | Required assertion |
| --- | --- |
| `{claims,appeals}.{a,b}.own-id` | 200, own patient canary present and foreign canary absent |
| `{claims,appeals}.{a,b}.foreign-id` | 404, no patient canary, no non-audit Mongo mutation |
| `{claims,appeals}.{a,b}.number` | Overlapping number resolves only the caller's record |
| `{claims,appeals}.{a,b}.search` | Shared-member search contains only the caller's canary |
| `{claims,appeals}.{a,b}.foreign-write` | Foreign claim status change / appeal submit returns 404 with no non-audit mutation; depends on the successful write control |
| `claims.reader-control`, `appeals.reader-control` | Restricted reader can read its own record |
| `claims.reader-write` | Reader's operational status write returns 403 without mutation |
| `claims.worker-control` | Worker changes Received to InAdjudication; HTTP 200 and stored status agree |
| `claims.worker-{void,delete,status-void}` | Dedicated void, DELETE and generic status-void all return 403 without mutation |
| `claims.inject-{approved,paid,denied}` | Generic status API returns 422 and cannot manufacture adjudicated/finalized outcomes, even for admin |
| `{claims,appeals}.anonymous` | No token: 401, no canary, no non-audit mutation |
| `{claims,appeals}.audit` | Mongo access audit includes the reader subject, payer and 200 outcome |
| `appeals.reader-{submit,note}` | Reader cannot submit or append a note; 403 without mutation |
| `appeals.create-overposting` | Injected tenant, creator and Closed status are ignored; Mongo shows caller tenant/actor and Draft |
| `appeals.invalid-transition` | Draft cannot jump directly into review: 409 without mutation |
| `appeals.writer-submit`, `appeals.begin-review-control` | Allowed submit and review transitions return 200 and persist their expected statuses |
| `appeals.writer-note`, `appeals.note-actor` | One note is stored with the token's actual subject, ignoring a body-supplied author |
| `appeals.note-replay` | Replaying the same event ID returns 200 with identical non-audit Mongo state, including appeal events and outbox data |
| `appeals.admin-replay-control`, `appeals.writer-replay-admin` | Admin can call replay (zero dead letters is valid); ordinary writer receives 403 with no mutation. Actual Kafka replay delivery is not tested |
| `appeals.withdraw`, `appeals.closed-reopen` | Withdraw persists Closed; submitting the closed appeal returns 409 without mutation |

Positive-control failures block dependent assertions as INCONCLUSIVE. Missing or
duplicate positive-control results cannot authorize a dependent case. A complete
PASS requires every ID in this profile exactly once; passing the authorization
profile cannot substitute for passing this profile.

Non-audit state hashes cover every non-empty collection in this disposable
MongoDB except ClaimAccessAudit, RecordAccessAudit and system collections. Empty
collection creation/index work does not count as a business-data mutation.
Access-audit writes are expected side effects. Hashes do not prove the absence of
external side effects, and canary checks are not a general PHI detector.

## Evidence and knowledge-base feed

The same [v1 result schema](scenario-result.schema.json) and
[review/publication policy](README.md#knowledge-base-feed-and-disclosure) apply.
The packet includes manifest, results, coverage, sanitized HTTP observations,
state/audit assertions, unreviewed finding summary, checksums, replay instructions
and an evidence-linked Markdown KB draft. Manifest records both services' managed
binary/dependency fingerprints, runtime/OS, source HEAD/dirty state, actual Mongo
version, fixture version, direct-Mongo claim seeding, Development NoOp encryption
and disabled Kafka delivery. Nothing is automatically published or uploaded.

Unsupported: per-tenant database mode; claims intake/adjudication; adjustment/replacement and finalized
claim flows; cross-service appeal/claim-link validation; financial or accumulator
correctness; encryption/key rotation; production identity; live Kafka delivery;
self-review separation; attachments; FHIR; mutation sensitivity. This campaign
asserts the existing permission contract; it does not invent a separate appeals
decision permission or claim that authors cannot review their own appeals.

## Operational status regression found by the campaign

The successful claims write control exposed a revision integration defect:
TryTransitionStatusAsync bumps the Mongo revision, but the status service then
replaced the pre-patch snapshot, causing its own CAS conflict. The service now
re-reads the current document before the remaining operational-field write. If
another writer has advanced its status, that newer state is returned without a
replacement. Ordinary CAS still fences a race after the re-read.

Regression tests use real Mongo to verify two consecutive transitions, notes,
receipt-date preservation and revision increments; a separate race test verifies
that a newer Paid state is not replaced. This fix does not implement atomic
payment/accumulator coordination or change claims:void permissions.

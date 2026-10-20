# CloudHealthOffice Open Security Challenge

An evidence-first, reproducible security testing series. Evidence bundles feed a
**reviewed security knowledge base**; articles and podcasts cite that knowledge
base. A green local run establishes only the assertions in its scenario catalog.
It does not establish production readiness, HIPAA compliance, ASVS conformance,
or independent penetration-test certification.

## First executable slice

The .NET **8** runner starts an actual authorization-service Kestrel process and
MongoDB 7 using EphemeralMongo. It accepts a local assembly path, never a target
URL. Two wholly synthetic payers deliberately share an authorization number and
member identifier, with different patient canaries. All writes go through HTTP;
the runner independently reads Mongo for state and audit checks.

This first slice has **33 scenarios**, including positive controls, within the proposed
30–50-case first campaign. Claims, appeals, payments,
FHIR, production identity, ingress, resilience and mutation sensitivity remain
explicitly unsupported. No live deployment has been tested by this harness.

## Reproduce

Run from the repository root with the pinned .NET 8 SDK, Git and network access
for NuGet and the initial EphemeralMongo MongoDB download. Docker is unnecessary.
Each evidence directory must be new; existing packets are never overwritten.

```sh
dotnet build src/services/authorization-service --configuration Release
dotnet build tools/SecurityChallenge --configuration Release
dotnet test tools/SecurityChallenge.Tests --configuration Release
dotnet tools/SecurityChallenge/bin/Release/net8.0/SecurityChallenge.dll \
  src/services/authorization-service/bin/Release/net8.0/authorization-service.dll \
  /tmp/cho-security-evidence-unique-run
```

The service gets an allowlisted environment, loopback-only binding, generated
throwaway database, explicit `Testing` identity configuration and `CHO_TESTING=1`.
Its outbound service URLs point to loopback port 1, and OTLP/Kafka are disabled.
HTTP proxy use and redirects are disabled. The runner uses the repository's **public development signing key**; this is
local authentication middleware coverage, not assurance of production OIDC,
key custody, certificates, gateway rules or workload identity. Startup failure,
port collision, Mongo failure and assertion exceptions produce ERROR, not PASS.
Requests time out after 10 seconds; readiness polls stop after 100 attempts.
The process tree is stopped before Mongo is disposed on ordinary completion or
exception. A force-killed runner may require local orphan-process cleanup.

This profile is not an OS network sandbox. Run it only on a disposable developer
or CI host, with trusted source binaries and synthetic data. The source's normal
background workers run; this fixture's future dates avoid expiry. Tests compare
only the authoritative Authorizations collection for mutation checks. They do
not assert the absence of every possible outbound or secondary-store side effect.

For local diagnosis only, `CHO_SECURITY_LAB_DEBUG=1` prints child-process logs to
stderr. Keep those logs restricted; they are **not** part of the evidence packet.

## Scenario catalog v1

The exact executable inventory is `EvidencePolicy.ScenarioIds` in the
[scenario catalog](../../tools/SecurityChallenge/EvidencePolicy.cs). Fixture creation must return 201 for
both payers before any scenario runs. Positive controls are included in the
33-scenario denominator, not reported as 33 attempted attacks.

| IDs | Required assertion |
| --- | --- |
| `isolation.{a,b}.own-id` | 200, own patient canary present and other payer canary absent |
| `isolation.{a,b}.foreign-id` | 404, no patient canary, unchanged authoritative authorization collection hash |
| `isolation.{a,b}.overlapping-number` | Same number resolves own canary, never other payer canary |
| `isolation.{a,b}.search` | Shared member search returns own canary and excludes other payer canary |
| `integrity.{a,b}.create-overposting` | Mongo tenant from token, actor exactly from token, approved units null and status Submitted despite injected approval |
| `identity.write.{anonymous,malformed,reader}` | 401/401/403 respectively, no patient canary, unchanged authorization collection hash |
| `privilege.reader-decision` | Read-only identity cannot cancel; 403 and unchanged authorization collection hash |
| `identity.reader-positive-control` | Same restricted identity can read own record; own canary only |
| `identity.tenant-header-conflict` | Payer A token plus payer B header denied with 403, no patient canary and no authorization mutation |
| `identity.wrong-{issuer,audience}` | Otherwise correctly signed synthetic admin token rejected with 401, no canary and no authorization mutation |
| `audit.authorized-read-attribution` | Mongo audit entry names payer A, its record and its actual token subject with 200 |
| `privilege.admin-decision-control` | Admin can cancel through the same endpoint; Mongo status becomes Cancelled |
| `isolation.{a,b}.foreign-decision` | Cross-payer admin cancellation returns 404 with no authorization mutation or canary |
| `identity.bad-signature` | Trusted issuer/audience with different signing key rejected with 401 and no authorization mutation |
| `member.approval-control` | Synthetic dependent authorization is created and approved through HTTP; Mongo confirms Approved |
| `member.own-positive-control` | Approved matching member/procedure/line/provider returns IsValid=true, MemberMatches=true and exact requested member echo |
| `member.{foreign-member,subscriber-substitution,case-sensitive-id}` | 200 business denial, IsValid=false, MemberMatches=false, MemberMismatch and requested identifier echo without the stored member identifier |
| `member.blank-id` | Explicit empty member query rejected with 400 |
| `member.{wrong-line,wrong-procedure,wrong-provider}` | Correct member still receives IsValid=false when another required dimension differs |
| `member.trimmed-id-control` | Whitespace-normalized correct member remains valid |

All member validation calls assert the authorization collection remains unchanged.
If approval or the matching-member validation control fails, dependent member
scenarios become INCONCLUSIVE; an already-invalid authorization cannot prove
member-mismatch protection.

Canary checks are explicit synthetic markers, not generic PHI detection. The
audit check covers an authorized record read, not rejected requests before the
controller, audit durability under failure, completeness or tamper resistance.
Member checks exercise the authorization-service validation endpoint with synthetic
identifiers. They do not establish a subscriber/dependent relationship graph,
claims-client enforcement, historical identity reconciliation, or approved-unit
accounting. Expired-token, key-rotation and production federation coverage remain
outside this campaign.

## Evidence contract v1.0

- `manifest.json`: run/time, exact service binary and dependency SHA-256 values,
  source HEAD plus dirty flag, runtime/OS, actual Mongo version, fixture/profile
  and publication state `REQUIRES_REVIEW`. Dirty runs cannot establish a clean
  commit-only reproduction; rebuild from clean source for a release claim.
- `scenario-results.jsonl`: [v1.0 JSON schema](scenario-result.schema.json);
  one ID, outcome and assertion reference per executed
  case. Outcomes are PASS, FAIL, ERROR, INCONCLUSIVE or UNSUPPORTED. This runner
  emits the first three and lists unsupported tracks separately in coverage.
- `http-observations.json`: scenario-linked request method/path, status,
  response-body digest and two canary-presence booleans. No bodies or tokens.
- `state-assertions.json`: scenario-linked before/after collection digests,
  document count and final cancellation predicate.
- `audit-assertions.json`: synthetic actor/tenant/record and matching-entry count.
- `coverage.json`: planned and executed denominators, PASS/FAIL/ERROR/INCONCLUSIVE counts and
  explicit unsupported tracks. A prerequisite error is not an executed scenario.
- `findings-summary.json`: unreviewed failed/error/inconclusive scenario IDs;
  confirmed findings are null until private review, never inferred as zero.
- `knowledge-base-draft.md`: result summary with limitations; always a draft.
- `reproduce.md`: source-linked replay instructions and the generated-ID limitation.
- `artifact-checksums.txt`: SHA-256 of every other packet file. Checksums detect
  accidental changes; they are not signed attestations or proof of independent
  execution. HTTP digests are references, not replayable response transcripts.

Exit 0 requires completed setup and the **exact** catalog, each once, all PASS.
Missing, duplicate, unknown, unsupported or inconclusive outcomes cannot satisfy
that policy. Exit 1 indicates a completed packet without a complete passing run;
invalid invocation returns 2. An I/O failure may prevent packet generation.

## Knowledge-base feed and disclosure

Flow: **test → restricted evidence → private triage → remediation → retest →
reviewed knowledge-base entry → series publication**. The repository's
[security policy](../../SECURITY.md) governs actual findings. Do not open public
issues, exploit PRs or publish a failing packet that exposes an unfixed weakness.
Report findings privately to the security contact in that policy.

The runner generates a local Markdown draft; it does **not** upload, publish,
update a public posture badge or declare a vulnerability fixed. A reviewer must
check synthetic-only content, exact binary/source provenance, complete coverage,
finding disclosure readiness and retest linkage before publishing. Preserve the
original packet; any edited public derivative gets its own checksum manifest.

Each reviewed KB entry should record: scenario/invariant, affected service and
profile, source/binary identity, linked run and evidence hashes, supported and
unsupported assertions, finding identifier, remediation commit, retest run,
reviewer and publication date. Use explicit states **proposed, tested, finding,
fixed, retested, independently reproduced**. Never infer a later state from a PR
merge or a previous green run. Keep restricted findings outside the public KB.

The series should progressively add claims-client member binding and ambiguous
authorization-line handling, claims and appeal authorization, payment idempotency and concurrency, FHIR/PAS
boundaries, sensitive-data canaries, production identity and ingress, then an
independent reproduction campaign. Each expansion must ship executable oracles
and a revised denominator before being described as tested.

# Contact workflow intake and documents

Date: October 25, 2026. Source baseline: main `7685fd3d`.
Status: proposed implementation; deployed acceptance remains outstanding.
Design: [ADR 034](../adr/034-contact-workflow-intake.md).

## Operating behavior

Save the caller, classification and summary on an open contact. Choose the line
of business and state, then explicitly open the case. The contact persists its
immutable intake intent before delivery. The owner reads that intent through
contact-service with the user's token. It does not trust browser receipt times.
The original contact start anchors the clock, even for intake recorded later.

- Member-self grievances and pre-service medical authorization appeals enter
  `MemberServiceCases` in appeals-service. No claim/provider identifiers are
  manufactured. Appeal intake verifies the member, adverse authorization and
  service date. The selected existing rule-pack evaluation is pinned at intake.
- Coverage requests require explicit patient/provider/service details. They enter
  the native authoritative authorization backend, preserving receipt and existing
  PA evidence/deadline behavior. External-core modes refuse this intake.
- Intake does not include provider disputes, representative authority, payment
  appeals, drug appeals or a complete state/contract legal qualification.
- Reviewers can find the latest 100 member service cases at `/service/cases`,
  open the encrypted summary and record one revision-fenced review outcome.
  `DecisionRecorded` does not mean statutory closure: notices, effectuation,
  extensions, hearing/IRE routing and complete reporting integration remain open.
- Follow-ups are `Inquiry` work in the existing work engine. A stable intent key
  retries the same work; a new key raises new work. Routing context and requester
  are frozen on the intent. The contact screen reads current work status and lets
  the lease holder complete work through the work engine's existing authority.
  Queue membership must be configured; no auto-grant is added.

Case/follow-up delivery failures leave visible pending intents. Reload and retry:
no token is persisted, and retries execute under current user permissions. Do not
edit the database to unlock a request. Case intents are immutable; a permanently
rejected or incorrectly configured case request needs owner-led reconciliation
before correction. This increment does not implement an amendment/cancellation
protocol. It must be accepted before enabling unrestricted live intake.

Closure requires classification, a receipt for a case classification, no pending
follow-up delivery, and a created follow-up for callback/follow-up outcomes.
Closing the contact does not close the case or its follow-up work.

## Documents

Call document access requires `service:read` plus `documents:read`, linked-subject
context and the existing sensitivity checks. The panel on contacts uses the same
catalog as claims, authorizations, appeals and providers. Uploads use the user's
CHO identity and are linked to the current subject. PDF/PNG/JPEG previews use an
authenticated portal endpoint, restricted content types, no-store/nosniff, and a
sandboxed frame. Pending/infected/failed files cannot be opened by the panel.
Part 2 records remain withheld until consent integration is implemented.

Select real scanning with:

```text
Documents__Scanner__Provider=ClamAv
Documents__Scanner__ClamAv__Host=<private-clamd-host>
Documents__Scanner__ClamAv__Port=3310
Documents__Scanner__ClamAv__TimeoutSeconds=60
Documents__Scanner__ClamAv__MaxBytes=67108864
Documents__AllowUnscannedContent=false
```

The adapter implements ClamAV's documented `zINSTREAM` protocol. Only a complete
`stream: OK` verdict is Clean. Detection, oversized input, malformed/truncated
responses, daemon failure and timeouts never produce Clean. Keep the daemon on a
private trusted network: this TCP protocol does not authenticate or encrypt the
connection. Operate signature updates and monitoring, set daemon StreamMaxLength
at least as high as the application limit, and qualify the chosen deployment with
clean and standard antivirus test fixtures. The included protocol tests use a
synthetic daemon; they do not establish actual engine detection or fresh signatures.
Default Provider=None continues to leave documents Pending. No production scanner
or cloud resources are provisioned by this change. Existing Pending/Failed files
need an accepted rescan/reupload procedure; background rescan is not added here.

## Deployment and acceptance

Compose includes contact-service and explicit owner URLs. Kubernetes defaults
use the existing service DNS names. Standalone local service configurations use
5034 (contacts), 5013 (appeals), 5005 (authorization) and 5031 (work management).
Configure the same issuer/audience and tenant permissions across these services.

The focused GitHub workflow runs the six affected test projects, including their
real-Mongo cases. Before pilot acceptance also verify:

1. Actual service-to-service identity and permissions, tenant separation and audit.
2. Lost responses/restarts/concurrent intake never create duplicate cases/work.
3. Original receipt and pinned rules survive reload; unsupported tracks refuse.
4. Case reviewers can find and work intake; downstream legal workflows have owners.
5. Follow-up queue membership, lease ownership and completed-work visibility.
6. Deployed scanner, quarantine behavior, upload/download/preview and access denial.
7. Recovery/reconciliation of permanently rejected immutable case intents.

This is not complete contact-center integration, regulatory case management or
production acceptance. Telephony, recording ingestion, reporting, representative
tracks, amendments, notices and fulfillment retain their existing separate scope.

## Local Mac check

Use synthetic data only. From this branch, with Docker Desktop running:

```bash
git pull --ff-only
docker compose --profile core up -d --build contact-service document-service appeals-service authorization-service work-management-service
```

Run the portal in a second terminal. Compose exposes document-service on 5032;
the portal's standalone development default is 5027, so override that address:

```bash
ASPNETCORE_ENVIRONMENT=Development Services__DocumentService=http://localhost:5032 dotnet run --project src/portal/CloudHealthOffice.Portal/CloudHealthOffice.Portal.csproj --no-launch-profile --urls http://localhost:5090
```

Open `http://localhost:5090/service` using the existing LocalDemo sign-in. Create
a synthetic contact, save classification/summary, open a grievance, reload it,
then create a follow-up. Queue membership is configured at
`/admin/work-management`; pull work through `/my-work` before completing it from
the contact. Review member cases at `/service/cases` with reviewer permissions.
Default scanning is None: uploaded documents stay Pending in the panel and do not
preview. Scanner-enabled acceptance requires a private ClamAV deployment and the
configuration above; do not bypass quarantine to claim that acceptance.

Contact-service's development settings now explicitly trust the same development
issuers as the existing services. Localhost forwarding is allowed only in the
Development settings of the relevant callers. Production settings are unchanged.

## Dated test evidence

At commit `e064ae61`, GitHub Actions run `40260055343` passed all six full projects:
contacts 98, cases 473, authorization 271, documents 120, work 255 and portal 1,282;
2,499 passed, zero failed/skipped, including the projects' real-Mongo tests.
The user's Mac independently passed all 98 contact tests at that head.
Later startup/pipeline and reviewer-concurrency additions require their own head
results; this historical count must not be attributed to a later commit.

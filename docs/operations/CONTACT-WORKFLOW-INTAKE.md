# Contact workflow intake and documents

Date: October 25, 2026. Source baseline: main `7685fd3d`.
Status: implemented; deployed acceptance is partial. See the October 26 GCP recovery results below.
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
edit the database to unlock a request. Case-intake recovery now distinguishes saved, unknown and explicitly rejected
requests. A delivery attempt saves `Unknown` before calling its owner. Retry the
saved request to reconcile a lost response; do not infer absence from a timeout.
Only owner HTTP 400/422 rejections enable **Correct rejected intake** in the portal.
Supply a reason, save the correction, then retry delivery. The prior intent,
reason, actor and time are retained; stale revisions refuse the correction.

Corrections may change jurisdiction/LOB, clinical details and the denied
authorization reference. Member, caller, classification, summary, original
receipt time, owning service and case identity stay fixed. There is no cancellation
or cross-owner rerouting. A created case is corrected through its owner.
Owners return an intake fingerprint. If an older concurrent request already
created the case, or a legacy owner cannot confirm the fingerprint, the contact
attaches the existing receipt and warns that correction application is not
confirmed. Review that case with its owner; do not open a replacement contact to
work around the warning. Existing pinned case rules are not rewritten.

Roll out contact-service, appeals-service, authorization-service and portal
together. Older owners can return receipts without fingerprints, which require
manual review. No history backfill or automatic retry job is added. The bounded
recovery protocol and managed-cloud race/restart behavior still require acceptance
before unrestricted live intake.

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
7. Recovery of rejected intake, unknown-outcome reconciliation, stale/concurrent
   retries, amendment history reload and existing-case fingerprint warnings.

This is not complete contact-center integration, regulatory case management or
production acceptance. Telephony, recording ingestion, reporting, representative
tracks, amendments, notices and fulfillment retain their existing separate scope.

## Docker Desktop Kubernetes check

With the `docker-desktop` node Ready, from `feat/contact-workflow-documents`:

```bash
git pull --ff-only
bash scripts/deploy-contact-workflow-local.sh
kubectl --context docker-desktop -n cho-contact-local port-forward svc/portal 5090:8080
```

Open `http://localhost:5090/service`. Keep the port-forward terminal running.
This builds eight images and deploys the portal, seven backend services, Mongo,
Redis, the local GCS emulator, ClamAV and a private synthetic-only Vault dev server in `cho-contact-local`.
All Kubernetes commands explicitly select Docker Desktop. Existing namespaces
are not modified. Docker Desktop kind nodes must be visible to `docker inspect`;
enable **Show system containers** if the script requests it. Built images are
imported into each visible node's containerd store; no registry push is required.
Use `--skip-build` only when these same local images already exist.

The appeal encryption key is generated once into a Kubernetes Secret and loaded
into Vault on every start. Keep that Secret with the Mongo data: deleting it
makes old case bodies unreadable. Vault's fixed development token is synthetic
and must never be used outside this isolated local setup. No service is exposed
outside the cluster until port-forwarding. Use synthetic data only.

Mongo metadata and the local GCS emulator document bytes survive pod restarts through their PVCs.
ClamAV scans new uploads; only clean verdicts permit downloads. Its signature
database also uses a PVC. The first start downloads signatures and can take up
to 20 minutes; reserve at least 4 GiB for the scanner. Storage and scanner
services stay private inside the cluster. The emulator requires no cloud
credentials and exercises the existing GCS provider. It does not validate real
GCP IAM, Workload Identity, signed URLs or bucket policy. Existing in-memory
uploads are not migrated;
create fresh synthetic documents after switching. Deleting the PVCs or resetting
Docker Desktop destroys local data. This is local workflow evidence, not
production storage, disaster recovery, or malware-detection certification. Other
platform backends are outside this focused environment.

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

## Local GCS and scanner acceptance - October 25, 2026

Synthetic contact `6fee1dea-6db9-4eb4-8a75-cb90b60df613` was used on Docker
Desktop Kubernetes with the existing GCS provider and real ClamAV INSTREAM engine.

- Clean PNG `ae76be2a510647e185bee01c2d250173` received a Clean verdict and
  downloaded through the authenticated portal with HTTP 200.
- Replaced both document-service and GCS emulator pods. The same download still
  returned HTTP 200 with SHA-256
  `a3f53368195a4ef1ccc3b86d64ae13bc2918284ef8a5da4b6699214e8854a850`,
  matching the original upload byte for byte.
- A valid PDF containing an embedded harmless EICAR test attachment received an
  Infected verdict (`c39f75d30e5e448691374d816d305455`). The UI withheld Open
  and Preview, and a direct authenticated portal download returned HTTP 409.
- An exploratory PNG with an appended EICAR string received Clean; this was not
  used as evidence of blocking. The embedded-file PDF is the positive test.
- The existing 14 document-store registration tests passed with no failures or
  skips. Kustomize rendering, Bash syntax, and whitespace checks passed.

These checks establish the observed local paths only. They do not establish
real GCP IAM, cloud bucket policy, signed URL support, backups, disaster recovery,
scanner outage recovery, or comprehensive malware detection. The old Pending
in-memory document was not migrated or rescanned.

## Claim-link and follow-up acceptance - October 25, 2026

The local setup now includes claims-service and tenant-service. Its idempotent
seed creates only the synthetic local-demo directory entry (MemberServices) and
claim `synthetic-contact-link-20261025`; it never replaces existing records.

For follow-up membership, open `/admin/work-management`, select Memberships,
and add user `local-demo-admin` to `Follow-up: Other`. The directory validates
`service:contact` before accepting membership. Open `/my-work`, click Get Next,
and enter a resolution on the contact before clicking Complete follow-up.

Observed results:
- Contact `b81e96b6-be5f-441a-a48d-0064812a6d90` linked the synthetic claim.
  Claims-service persisted note `4eb945dc-d058-4a2b-b255-7161860a2441`, authored
  by `local-demo-admin`, referencing contact `C-20261026-598DC864`.
- The existing follow-up on contact `6fee1dea-6db9-4eb4-8a75-cb90b60df613`
  was leased through Get Next and completed through the portal, displaying Done.
- The tenant directory originally refused membership because its MemberServices
  catalogue lacked the contact/work permissions already present in the shared
  policy. The catalogue now uses that shared role policy; 65 focused role cases
  passed, including role equivalence and denial of claims adjudication access.

GCP: the existing document-service Workload Identity wrote and read a unique
78-byte synthetic object in `cloud-health-office-demo-cho-documents` with matching
SHA-256 `3b5c0670a8cecc2f25782c53abf8ae78dd338d6dd7968c048020891d1be48a14`.
The test object was removed with a generation precondition. Bucket metadata showed
uniform bucket-level access and enforced public access prevention. This validates
that observed identity/storage path, not all IAM policy or full cloud workflow.
The inspected GKE workload still ran pre-merge images; contact-service referenced
an Azure registry image and had zero available replicas. No cloud deployment or
IAM changes were made. The cloud demo also permits unscanned content and needs a
separate scanner deployment review before scanner acceptance there.

## Intake recovery validation boundary — October 26, 2026

Regression cases cover rejected-request corrections, unknown outcomes, stable
identity/receipt, legacy receipts, stale writes, late responses, Mongo reload and
revision races, tenant isolation and portal controls. Local .NET SDK 8.0.426 and
direct MSBuild abort on unavailable sandbox process metadata before compilation.
No executable local pass, deployed GCP recovery run or production acceptance is
claimed. The contact-workflow CI matrix remains the executable validation gate.

## GCP recovery acceptance — October 26, 2026 (partial)

[Deployment run 40355961985](https://github.com/aurelianware/cloudhealthoffice/actions/runs/40355961985)
succeeded on attempt 2 with images from `c8e009a234326a9cc3d95045d056786f827d09ee`.
Three initial image pushes failed on transient identity-token refresh timeouts;
rerunning failed jobs recovered them. Checks verified updated, ready replicas,
not just deployment image templates. All records in this run were synthetic.

Live intake found that appeals-service, authorization-service and
work-management-service still read contacts from Development's `localhost:5034`.
The GCP renderer now explicitly sets `Services__ContactServiceUrl` to
`http://contact-service` for these three owners. The same correction was applied
to the demo deployments and their rollouts completed before resuming validation.
All 36 GCP manifest tests passed, including a regression for the callback URLs.

Observed results (33 individual checks passed; overall acceptance remains partial):

- Document `fcdfc7619f3e4e92ad070c21b68ef49d` from the earlier scanner-outage test
  remained Failed and blocked before retry. Real ClamAV returned Clean on retry;
  the download matched the original catalog SHA-256, with unchanged identity,
  links, size, content version and retention policy. Anonymous, cross-tenant and
  missing-subject-permission retries were denied. Clean and Infected documents
  could not use retry; the EICAR fixture remained blocked. ScanRetry audit events
  recorded the acting user. No scanner outage was introduced in this run.
- Contact `75bc818e-672b-4594-b2c8-2063a2753dba` preserved its Unknown intent while the
  callback was unavailable and refused amendment in that state. After the route
  correction, retrying the saved request through contact-service produced an
  explicit rejection for synthetic state ZZ. Amendment required a reason,
  retained the previous intent/author/time, preserved contact identity and
  receipt details, and refused a stale revision.
- The corrected TX request reached appeals-service but failed before creating
  an owner receipt: `AppealsService StaleEncryptionKeyException` reported missing
  `appeal-body-encryption-key-v1`. The demo uses `SecretProvider:Provider=None`.
  The contact correctly remains Unknown with its saved corrected intent; the
  deterministic owner receipt returned 404. Acceptance of corrected intake and
  duplicate-case verification remain outstanding. Encryption was not bypassed.
- Follow-up `demo.inquiry-contact-ccad22e1a958274fa76dfdca7e72a1e6` was created once across a repeated
  request. With the user's explicit approval, `local-demo-admin` was added to
  the demo's Follow-up: Complaints queue and only this synthetic item was
  assigned to that operator. Completion through the deployed portal persisted
  Done, completion time and resolution. The Completed audit event retained the
  actor and resolution; another tenant could not read the item. General Get
  Next was not used. Contact history retained rejection, amendment and follow-up
  creation events.

Next prerequisite: configure a persistent versioned appeal encryption key through
an actual secret-provider integration. Preserve the current corrected intent and
reconcile it through contact-service once that prerequisite is available. This
run does not establish full case acceptance, regulatory closure, notice delivery,
effectuation, disaster recovery or production readiness.

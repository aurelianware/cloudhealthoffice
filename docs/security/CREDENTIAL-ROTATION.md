# Credential Rotation — Committed Secrets

**Status:** Items 1 and 2 both closed 2026-09-23 — the hosting subscription was deleted. No rotation outstanding.
**Last full-history scan:** 2026-09-21, gitleaks 8.30.1, 3,426 commits, root commit `5bc015b`

Every value listed here was committed to a **public** repository. Git history has
deliberately **not** been rewritten — rewriting would break every existing clone,
fork and open pull request, and would not undo the exposure, because anything
pushed to a public remote must be assumed to have been fetched and indexed.

**Treat every credential below as compromised, regardless of how weak or
"development-only" it looks.** Removing it from `HEAD` stops the bleeding; only
rotation ends the exposure.

## 1. Real credentials — both resolved, see each row

| # | Credential | Purpose | First committed | Removed from HEAD | Action |
|---|---|---|---|---|---|
| 1 | *(value not reproduced here — see `328828f5`, `b8818bff`, `7aa9059b`, `5a3d8b24`)* | SFTP password for the self-hosted SFTP server in namespace `cho-sftp`, reachable in-cluster at `sftp-service.cho-sftp.svc.cluster.local` and publicly on a LoadBalancer IP at port 22. Originally the `logicapp` account; **renamed to `cho-edi` in `8281d4d1` (2026-03-22) with the password carried forward unchanged.** | `328828f5` — 2026-02-04, *"Add SFTP workflows: test job and X12 275 attachment upload"* | `c11950b9` (#1184, 2026-09-21). It was live at `HEAD` for ~7½ months. | **No action — CLOSED 2026-09-23.** The hosting subscription was deleted, so the server, the account and the IP no longer exist. Dev-only environment; never held PHI. See "Item 1" below. |
| 2 | *(value not reproduced here — see `2f0f7cc3`, `449cc8d8`)* | PostgreSQL password for `reference-data-service`, in a manifest labelled `ASPNETCORE_ENVIRONMENT: "Production"` despite targeting an in-cluster StatefulSet | `2f0f7cc3` — 2026-02-05, *"Add Reference Data Service — CPT/ICD-10/HCPCS code validation with PostgreSQL"* | `7ce7cd2d` (#1173, 2026-09-17) | **No action — CLOSED 2026-09-23.** See "Item 2" below. |

Item 1 is the serious one. It is a 24-character generated password — not a
placeholder — it named a specific account on a specific host, and it sat in
three X12 job manifests and three documents at `HEAD`.

The value itself is no longer printed in this document. It can be recovered from
history if needed for rotation verification; the commits that carried it are
`328828f5`, `b8818bff`, `7aa9059b`, `5a3d8b24`, and it was removed in `c11950b9`.

### Item 1 — what this actually was

**Status: CLOSED — decommissioned. 2026-09-23.**

The Azure subscription that hosted this environment **no longer exists**, and the
repository owner confirms it was a development environment that never held PHI or any
production payer data.

Deleting a subscription deprovisions every resource in it, so the questions that were
open before this attestation are answered by it:

| Previously open | Resolution |
|---|---|
| Is the SFTP server still running? | No. It was a Kubernetes `Deployment` and `type: LoadBalancer` `Service` in namespace `cho-sftp`; there is no AKS cluster, and no subscription to host one. |
| Does the `cho-edi` account need rotating? | No. There is no server, so there is no account. **No rotation is required.** |
| Is the public IP still allocated? | No. It was released to Microsoft's address pool along with the subscription. |
| Did the `sftp-data-pvc` volume survive, and could it hold 834/837 files? | No — and the question is moot: the environment never carried PHI or production data. |
| Did anyone connect using the exposed password? | Not determinable; the server and its logs are gone. Given a dev-only environment with synthetic EDI, no live payer customers, and a DNS name never pointed at the address, the practical exposure is assessed as immaterial. |

This is recorded as an **owner attestation**, not an independently verified result. The
subscription no longer exists, so it cannot now be re-derived from Azure by anyone. The
supporting technical findings below were verified from the repository and stand on their
own.

**One consequence worth noting.** The LoadBalancer's public IP returned to Microsoft's
pool and may since have been assigned to an unrelated Azure tenant. It has therefore
been removed from documentation at `HEAD`: continuing to publish it as this platform's
SFTP endpoint would be both inaccurate and an invitation for a reader to probe an
address that now belongs to someone else. The address remains in git history, which is
not rewritten.

#### Verified from the repository (unchanged by the attestation)

A read-only investigation on 2026-09-23 found that two widely-held assumptions about
this credential are wrong, and both change the remediation:

1. **It was not a Logic App credential, and not an Azure Storage account SFTP local
   user.** It is the password for a Linux user on a self-hosted `atmoz/sftp` container
   (`infrastructure/k8s/sftp-server-deployment.yaml`), published to the internet by a
   `type: LoadBalancer` Service on a public IP at port 22 (the address is in git history; it is not reproduced here, having since returned to Microsoft's pool). Retiring Azure Logic Apps
   (ADR 004, `411e2fb2` / `70f8dd9f`, 2026-03-15 → 2026-03-22) could not have
   decommissioned it, because it was never a Logic App resource. `git log --all -S`
   for `isSftpEnabled`, `localUsers` and `sftpEnabled` returns zero commits — there
   was never a storage-account SFTP endpoint in this repository.

2. **The account was renamed, not retired.** `8281d4d1` (2026-03-22) renamed the SFTP
   user from `logicapp` to `cho-edi` across infra and scripts, but did not change the
   password. The value remained the hardcoded fallback
   (`SFTP_PASS=${SFTP_PASS:-"<value>"}`) for the **`cho-edi`** account in
   `x12-277-download-job.yaml`, `x12-278-upload-job.yaml` and
   `x12-837-claims-jobs.yaml` until `c11950b9`. Rotating an account called `logicapp`
   would therefore rotate nothing.

Note also that the Argo migration replaced the *orchestrator*, not the SFTP data path:
the workflows under `infrastructure/argo-workflows/` still mount the `sftp-credentials`
/ `sftp-users` Secrets at `HEAD`.

### Item 2 — reference-data PostgreSQL

**Status: CLOSED — decommissioned. 2026-09-23.**

Same resolution as item 1, and for the same reason: the database lived in the Azure
subscription that no longer exists.

- **Only one service uses PostgreSQL.** `reference-data-service` is the sole consumer
  (`Npgsql.EntityFrameworkCore.PostgreSQL` in `reference-data-service.csproj:25`; no
  other service references Npgsql).
- **It held reference code sets only — no PHI.** The schema is `cpt_codes`,
  `icd10_codes`, `hcpcs_codes`, `modifiers`, `drg_codes`, `place_of_service` and
  `revenue_codes` (`src/services/reference-data-service/Models/ReferenceCodes.cs`).
  These are public CMS and AMA code reference data. No member, claim or eligibility
  data was stored in PostgreSQL.
- **The server was in the deleted cluster.** The connection string targets
  `Server=postgres-0.postgres-service` (`appsettings.json:11`) — a Kubernetes
  StatefulSet hostname, not a managed database. There is no PostgreSQL manifest under
  `infrastructure/` at `HEAD`, and no PostgreSQL workload in the current Azure Container
  Apps environment.

The `ASPNETCORE_ENVIRONMENT: "Production"` label on the manifest was misleading: it set
the application environment for a service pointing at an in-cluster development
StatefulSet, not at a production managed instance. That label is what made this item
look more serious than it was during the original scan.

**No rotation is required**, because the instance no longer exists. As with item 1 this
is an owner attestation for the subscription's deletion; the technical findings above
are verifiable from the repository.

Remaining PostgreSQL use is local development only (`docker-compose.development.yml`,
`scripts/deploy-local.sh`), with credentials documented as local-dev defaults in
section 2.

### Why the scanner did not catch it

Two independent failures, both fixed in this change:

1. **`.gitleaks.toml` had no rules.** It declared a `[allowlist]` but no
   `[[rules]]` and no `[extend] useDefault = true`. Gitleaks loaded the config,
   ran with **zero detectors**, reported "no leaks found" and exited 0. The CI
   *Secret Scanning* job passed unconditionally and could never have failed —
   verified by planting a live-format AWS key and Stripe key in the tree and
   watching the scan report clean.
2. **A blanket path allowlist.** `k8s/.*\.yaml` and `infra/k8s/.*\.yaml` were
   allowlisted. Gitleaks matches path patterns unanchored, so these also covered
   `infrastructure/k8s/**` — every manifest in the repository.

`scripts/ci/verify-gitleaks-rules.sh` now plants a canary secret and fails the
build if gitleaks does not find it, so a rule set that detects nothing cannot be
reported as a passing control again.

## 2. Local-development defaults — rotate only if reused

These were never production credentials. They are committed defaults for
containers that listen on a local network, and they are documented as such in
`.env.example` and `.env.local.example`. They have been renamed to
`local-dev-only` so no reviewer has to guess whether a real secret leaked.

| Credential | Purpose | First committed |
|---|---|---|
| `securepassword123` | MongoDB root password, docker-compose + portal dev settings | `0e8ca4eb` — 2026-02-12 |
| `localdev123` | MongoDB / PostgreSQL password, local Kubernetes dev script | `4983c7d5` — 2026-03-24 |
| `chodevpass` | PostgreSQL password, docker-compose development profile | `b621683b` — 2026-03-23 |
| `changeme123`, `changeme456` | SFTP server accounts `cho-edi` and `clearinghouse` | `edbb0f8b` — 2026-02-04 |
| `dev-token-replace-in-production` | `backend-api-credentials` token | `cae35b69` — 2026-02-04 |

**Rotate any of these if the value was ever reused outside a local machine** —
in a shared dev cluster, a demo environment, or a pilot. `securepassword123`
deserves particular attention: it was not only a compose default, it was a
**hardcoded fallback in portal application code**
(`src/portal/CloudHealthOffice.Portal/Program.cs`), so any portal deployment
missing `MongoDB__ConnectionString` silently connected using it rather than
failing. That fallback has been removed; the portal now fails closed.

## 3. Checked and clean

The full-history scan found **no** committed private keys, cloud provider keys
(AWS/Azure/GCP), GitHub tokens, Stripe live keys, Anthropic keys, or MongoDB or
Cosmos connection strings carrying a real account key. The Cosmos
`AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==`
that appears in several files is the **Azure Cosmos DB Emulator's well-known
public key**, published by Microsoft and identical for every installation.

## 4. After rotating

1. Recreate the affected Kubernetes Secrets (`sftp-credentials`, `sftp-users`,
   and the reference-data PostgreSQL secret).
2. Confirm `gitleaks detect --no-git --source .` is clean at `HEAD`.
3. Update the "Last full-history scan" date above.

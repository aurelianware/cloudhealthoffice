# Credential Rotation — Committed Secrets

**Status:** action required by the repository owner
**Last full-history scan:** 2026-09-21, gitleaks 8.30.1, 3,426 commits, root commit `5bc015b`

Every value listed here was committed to a **public** repository. Git history has
deliberately **not** been rewritten — rewriting would break every existing clone,
fork and open pull request, and would not undo the exposure, because anything
pushed to a public remote must be assumed to have been fetched and indexed.

**Treat every credential below as compromised, regardless of how weak or
"development-only" it looks.** Removing it from `HEAD` stops the bleeding; only
rotation ends the exposure.

## 1. Rotate now — real credentials

| # | Credential | Purpose | First committed | Removed from HEAD | Action |
|---|---|---|---|---|---|
| 1 | *(value not reproduced here — see `328828f5`, `b8818bff`, `7aa9059b`, `5a3d8b24`)* | SFTP password for the self-hosted SFTP server in namespace `cho-sftp`, reachable in-cluster at `sftp-service.cho-sftp.svc.cluster.local` and publicly at `20.115.193.245:22`. Originally the `logicapp` account; **renamed to `cho-edi` in `8281d4d1` (2026-03-22) with the password carried forward unchanged.** | `328828f5` — 2026-02-04, *"Add SFTP workflows: test job and X12 275 attachment upload"* | `c11950b9` (#1184, 2026-09-21). It was live at `HEAD` for ~7½ months. | **Rotate the `cho-edi` SFTP account password — not `logicapp`, which no longer exists.** Then recreate the `sftp-users` Secret in `cho-sftp` and the `sftp-credentials` Secret in `cho-workflows`. See "Item 1 — what this actually was" below. |
| 2 | `CloudHealthOffice2026!` | PostgreSQL password for `reference-data-service`, in a manifest labelled `ASPNETCORE_ENVIRONMENT: "Production"` | `2f0f7cc3` — 2026-02-05, *"Add Reference Data Service — CPT/ICD-10/HCPCS code validation with PostgreSQL"* | Already removed, in PR #1173 (2026-09-18) | **Rotate the reference-data PostgreSQL password** in every environment where this manifest was ever applied. |

Item 1 is the serious one. It is a 24-character generated password — not a
placeholder — it named a specific account on a specific host, and it sat in
three X12 job manifests and three documents at `HEAD`.

The value itself is no longer printed in this document. It can be recovered from
history if needed for rotation verification; the commits that carried it are
`328828f5`, `b8818bff`, `7aa9059b`, `5a3d8b24`, and it was removed in `c11950b9`.

### Item 1 — what this actually was

**Status: OPEN — awaiting Azure-side verification. Last reviewed 2026-09-23.**
**Owner: repository owner. Target: before any external security review.**

This item is deliberately recorded as unresolved rather than closed. The endpoint has
not been confirmed decommissioned, and no evidence available from outside Azure can
confirm it either way, so the credential must be treated as potentially live until the
checks in "Resolving this item" below are run against the subscription.

#### What has already been ruled out

| Check | Date | Result | What it proves |
|---|---|---|---|
| `git log --all -S` for `isSftpEnabled`, `localUsers`, `sftpEnabled` | 2026-09-23 | zero commits | No Azure Storage account SFTP endpoint ever existed in this repository. The storage-account local-user check does **not** apply and must not be treated as an all-clear. |
| `dig sftp.cloudhealthoffice.com A` | 2026-09-23 | `NOERROR`, no answer | The DNS record was never created (`CHANGELOG.md:1452`). Says nothing about the IP. |
| TCP connect to `20.115.193.245` ports 22, 80, 443, 2222 | 2026-09-23 | no response on any port; control host on :22 succeeded | **Inconclusive.** Silent drops are produced both by a deallocated IP and by an NSG default-deny in front of a running server. This repository ships `scripts/setup/setup-sftp-dns-whitelist.sh` to configure exactly such an allowlist. |
| `kubectl get ns` on the only locally configured context | 2026-09-23 | no `cho-sftp` / `cho-workflows` | Rules out the local Docker Desktop cluster only. |

No authentication was attempted against any host at any point.

#### Resolving this item

Run the commands in "Checks the founder needs to run" below, then apply this reading:

| Finding | Action |
|---|---|
| No AKS cluster **and** no public IP `20.115.193.245` | Decommissioned. Replace this Status block with the confirmed date and the command output that established it. |
| Deployment or Service still exists | **Delete it rather than rotating** — this path is legacy; Argo replaced the orchestration and the SFTP server is no longer the intended data path. **Before deleting:** list the files on the volume and capture container logs. If any real EDI ever transited it, 834 and 837 payloads carry PHI, which makes this a potential-disclosure assessment rather than a leaked development password. Pre-pilot status makes synthetic data likely — confirm, do not assume. |
| Public IP exists but nothing is bound to it | Release it. It is orphaned, billable, and still named in public documentation. |

A read-only investigation on 2026-09-23 found that two widely-held assumptions about
this credential are wrong, and both change the remediation:

1. **It was not a Logic App credential, and not an Azure Storage account SFTP local
   user.** It is the password for a Linux user on a self-hosted `atmoz/sftp` container
   (`infrastructure/k8s/sftp-server-deployment.yaml`), published to the internet by a
   `type: LoadBalancer` Service at `20.115.193.245:22`. Retiring Azure Logic Apps
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

#### Checks the founder needs to run

```bash
# 1. Does the cluster still exist, and is the SFTP server still deployed?
az aks list -o table
az aks get-credentials -g <resource-group> -n <cluster-name>
kubectl get deployment sftp-server -n cho-sftp
kubectl get svc sftp-service -n cho-sftp -o wide

# 2. Which accounts exist, and with which passwords?
kubectl get secret sftp-users -n cho-sftp -o jsonpath='{.data.users\.conf}' | base64 -d
#    -> look for 'cho-edi:' AND any leftover 'logicapp:' entry

# 3. Is the public IP still allocated, and is anything bound to it?
az network public-ip list --query "[?ipAddress=='20.115.193.245']" \
  -o json --query "[].{name:name,rg:resourceGroup,ip:ipAddress,attachedTo:ipConfiguration.id}"
#    -> attachedTo == null means the IP is orphaned: release it.

# 4. BEFORE deleting anything: what was actually on the server?
#    834 and 837 payloads carry PHI. If real EDI ever transited this server, the
#    exposure assessment is different from a leaked development password, so
#    establish this first and record the output.
kubectl exec -n cho-sftp deployment/sftp-server -- ls -lAR /home/cho-edi/ 2>/dev/null
kubectl exec -n cho-sftp deployment/sftp-server -- ls -lAR /home/logicapp/ 2>/dev/null
kubectl logs -n cho-sftp deployment/sftp-server --all-containers --timestamps \
  --tail=-1 > sftp-server-access-history-$(date +%F).log
#    -> the log gives a record of connections; retain it with the incident notes.

# 5. Then decommission (preferred over rotation — this path is legacy)
kubectl delete -f infrastructure/k8s/sftp-server-deployment.yaml
#    -> removes the Deployment, Service (and therefore the LoadBalancer), PVC,
#       ConfigMap and the sftp-users Secret. Confirm the public IP is released
#       afterwards with the command in step 3, and delete it explicitly if Azure
#       retained it as a static address.
```

Note on the container logs: `atmoz/sftp` logs authentication events to the container
log, but the log is bounded by the container's lifetime and the node's log rotation. If
the pod has restarted since 2026-02-04, the log will not cover the full exposure
window, so its absence of suspicious entries is **not** evidence that nothing connected.

Unauthenticated checks already performed on 2026-09-23, none of them conclusive:
`az` CLI not installed and no Azure credentials present on the machine used;
`sftp.cloudhealthoffice.com` returns NODATA (the A record was never created, per
`CHANGELOG.md:1452`); TCP connects to `20.115.193.245` on ports 22, 80, 443 and 2222
all produced no response while a control connect to `github.com:22` succeeded. Silent
drops on every port are equally consistent with a deallocated IP and with an NSG
default-deny in front of a running server, which is the configuration
`scripts/setup/setup-sftp-dns-whitelist.sh` exists to create. No authentication was
attempted.

**Separately:** `scripts/setup/rotate-sftp-password.sh:63-66` restarts
`deployment/sftp-service`, but the Deployment is named `sftp-server`
(`infrastructure/k8s/sftp-server-deployment.yaml:82`); `sftp-service` is the Service.
The script will patch the Secret and then fail on the restart, so rotation can appear
to succeed while the running container keeps the old `users.conf`. Fix that before
relying on the script.

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

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
| 1 | `sJ8p8WAsE4Es6PgMbUACErOs` | SFTP password for the `logicapp` account on `sftp-service.cho-sftp.svc.cluster.local` | `328828f5` — 2026-02-04, *"Add SFTP workflows: test job and X12 275 attachment upload"* | **This change.** It was live at `HEAD` for ~7½ months. | **Rotate the `logicapp` SFTP account password.** Then recreate the `sftp-credentials` Secret in `cho-workflows` and the `sftp-users` Secret in `cho-sftp`. |
| 2 | `CloudHealthOffice2026!` | PostgreSQL password for `reference-data-service`, in a manifest labelled `ASPNETCORE_ENVIRONMENT: "Production"` | `2f0f7cc3` — 2026-02-05, *"Add Reference Data Service — CPT/ICD-10/HCPCS code validation with PostgreSQL"* | Already removed, in PR #1173 (2026-09-18) | **Rotate the reference-data PostgreSQL password** in every environment where this manifest was ever applied. |

Item 1 is the serious one. It is a 24-character generated password — not a
placeholder — it named a specific account on a specific host, and it sat in
three X12 job manifests and three documents at `HEAD`.

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

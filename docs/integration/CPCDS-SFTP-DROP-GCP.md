# Hosted CPCDS drop on GCP: SFTP into Cloud Storage

The hosted offer (Mode B) gives each plan an SFTP login for its daily CPCDS
extracts. On GCP there is no managed SFTP service, so CHO runs
[SFTPGo](https://github.com/drakkan/sftpgo) on GKE with Cloud Storage as each
user's filesystem. A plan's upload goes straight into the drop bucket, and
claims-service's drop worker loads it from there
([CPCDS-FILE-DROP-IMPORTER.md](CPCDS-FILE-DROP-IMPORTER.md)).

```
plan's core ──SFTP (key only, allow-listed)──▶ SFTPGo on GKE
                                                │ writes {tenant}/inbound/…
                                                ▼
                                  gs://<project>-cho-cpcds-drops
                                                ▲ reads, moves to processed/ or failed/
                                     claims-service drop worker ──▶ members, providers, claims
```

## What a plan sees

One SFTP user per plan, named after its tenant, on one static address:

| Folder | The plan can |
|---|---|
| `/inbound` | upload, overwrite, rename, delete: where the daily files go |
| `/processed/{run}` | list and download: files that loaded |
| `/failed/{run}` | list and download: files whose load failed, to fix and resend |

Sign-in is by SSH public key only; no password exists. The address is
allow-listed to the plan's own egress ranges. SFTPGo bans sources that keep
failing to authenticate.

Uploading to a temporary name and renaming is fine: names ending `.part`,
`.filepart`, `.tmp` or starting with `.` are never loaded, and a file is
loaded only after it has stopped changing for `CpcdsDrop:QuietPeriod`.

## Provisioning

1. **Terraform** (`infrastructure/gcp/cpcds-drops.tf`, `enable_cpcds_drops = true`):
   the private drop bucket (unversioned; every object deleted after
   `cpcds_drop_retention_days`, default 30, since loads are idempotent and a
   plan resends rather than CHO keeping raw extracts), claims-service's and
   SFTPGo's Google identities (Workload Identity, `roles/storage.objectUser`
   on that bucket only), and a static address for the SFTP load balancer.
   Set `cpcds_drop_kms_key_name` to encrypt the bucket with a CMEK key (the
   project's Cloud Storage service agent needs encrypt/decrypt on it).
2. **claims-service**: `deploy/gcp/render_manifests.py --cpcds-drop-bucket
   <bucket> --cpcds-drop-service-account <gsa>` runs claims-service as its own
   `claims-service` Kubernetes service account bound to the drop identity,
   with `CpcdsDrop__Enabled=true`, `CpcdsDrop__Store=Gcs` and the bucket.
3. **SFTPGo**: `SFTP_SOURCE_RANGES=<plan CIDRs> ./deploy/gcp/sftpgo/apply.sh`
   reads the Terraform outputs, creates the admin credential once (generated,
   stored only as a Kubernetes secret in `cho-sftp` and, for tenant-service,
   in `cloudhealthoffice`), and applies `deploy/gcp/sftpgo/sftpgo.yaml`: one
   replica (its user database is SQLite on a 1 GiB disk, which also keeps the
   SSH host keys plans pin), port 22 on the static address limited to those
   ranges, the REST API cluster-internal and open to tenant-service only, the
   web admin and web client off.
4. **Each plan**: its drop settings (`PUT /api/v1/claims/import/cpcds/drops/settings`)
   and its SFTP user. Both are on the portal's **Imports & EDI** page, under
   **Setup**: the plan's administrator pastes the core's SSH public key and
   tenant-service creates the user through SFTPGo's REST API
   (`PUT /api/v1/tenants/{tenantId}/cpcds-sftp`; see
   [Hosted SFTP logins](CPCDS-FILE-DROP-IMPORTER.md#hosted-sftp-logins)). This
   is the shape of the user it creates, which an operator can also create by hand:

```json
POST /api/v2/users
{
  "username": "plan-a",
  "status": 1,
  "public_keys": ["ssh-ed25519 AAAA... plan-a-core"],
  "home_dir": "/var/lib/sftpgo/users/plan-a",
  "permissions": {
    "/": ["list"],
    "/inbound": ["list", "upload", "overwrite", "rename", "delete"],
    "/processed": ["list", "download"],
    "/failed": ["list", "download"]
  },
  "filesystem": {
    "provider": 2,
    "gcsconfig": {
      "bucket": "<project>-cho-cpcds-drops",
      "key_prefix": "plan-a/",
      "automatic_credentials": 1
    }
  },
  "filters": { "denied_login_methods": ["password", "keyboard-interactive"] }
}
```

The username and key prefix must be the tenant id: claims-service reads
`{tenant}/inbound/` for each tenant whose drop is on.

## What this is not yet

* The hosted environment itself (Google BAA in place, CMEK by default, a
  private cluster) is separate work; until then this runs on the demo
  project, which holds synthetic data only.
* tenant-service's SFTP user provisioning (from signup and the portal's setup
  page, where a plan pastes its public key) is the next PR.
* The SFTPGo image is pinned to a minor tag; pin a digest before production.

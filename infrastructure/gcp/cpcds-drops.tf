# The hosted CPCDS file drop (docs/integration/CPCDS-SFTP-DROP-GCP.md).
#
# Off by default (enable_cpcds_drops = false). Enabled, this creates:
#   - a private Cloud Storage bucket holding each plan's daily CPCDS extracts
#     under {tenant}/inbound/, and claims-service's processing/, processed/
#     and failed/ prefixes beside it. These are member and claim files, so the
#     bucket is unversioned (a deleted extract is gone) and a lifecycle rule
#     deletes every object after cpcds_drop_retention_days, loaded or not
#   - cho-cpcds-drop: claims-service's Google identity, which reads, moves and
#     deletes objects in the bucket (claims-service KSA, Workload Identity)
#   - cho-sftpgo: the SFTP server's Google identity, which writes plans'
#     uploads into the bucket and lets them list their own folders (sftpgo
#     KSA, Workload Identity). SFTPGo confines each plan to {tenant}/
#   - a regional static IP for the SFTP load balancer, so a plan can allow-list
#     one address and pin one host key
#
# Not here, on purpose: a Google BAA, CMEK, a private cluster. Those are the
# hosted profile's (hosted.tf); with hosted = true the bucket encrypts with
# its storage key, unless cpcds_drop_kms_key_name names another.

variable "enable_cpcds_drops" {
  description = "Create the hosted CPCDS drop bucket, its two service accounts and the SFTP address."
  type        = bool
  default     = false
}

variable "cpcds_drop_bucket_name" {
  description = "Name of the drop bucket. Empty uses <project_id>-cho-cpcds-drops (bucket names are global)."
  type        = string
  default     = ""
}

variable "cpcds_drop_retention_days" {
  description = "Age in days after which the bucket deletes any drop object, loaded or not. Loads are idempotent, so a plan resends rather than CHO keeping raw extracts."
  type        = number
  default     = 30
}

variable "cpcds_drop_kms_key_name" {
  description = "Cloud KMS key for the drop bucket's default encryption (CMEK). Empty uses Google-managed keys."
  type        = string
  default     = ""
}

variable "cpcds_sftp_namespace" {
  description = "Namespace SFTPGo runs in (deploy/gcp/sftpgo)."
  type        = string
  default     = "cho-sftp"
}

locals {
  cpcds_drops = var.enable_cpcds_drops
}

resource "google_storage_bucket" "cpcds_drops" {
  count = local.cpcds_drops ? 1 : 0

  project  = var.project_id
  name     = coalesce(var.cpcds_drop_bucket_name, "${var.project_id}-cho-cpcds-drops")
  location = var.region
  labels   = local.labels

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  force_destroy               = false

  versioning {
    enabled = false
  }

  lifecycle_rule {
    condition {
      age = var.cpcds_drop_retention_days
    }
    action {
      type = "Delete"
    }
  }

  # An explicit cpcds_drop_kms_key_name wins; the hosted profile's storage
  # key otherwise.
  dynamic "encryption" {
    for_each = var.cpcds_drop_kms_key_name != "" ? [var.cpcds_drop_kms_key_name] : var.hosted ? [local.storage_kms_key] : []
    content {
      default_kms_key_name = encryption.value
    }
  }

  depends_on = [google_project_service.required, google_kms_crypto_key_iam_member.hosted_storage]
}

resource "google_service_account" "cpcds_drop" {
  count = local.cpcds_drops ? 1 : 0

  project      = var.project_id
  account_id   = "cho-cpcds-drop"
  display_name = "CHO CPCDS drop loader"
  description  = "claims-service's Kubernetes service account (Workload Identity): loads plans' CPCDS extracts from the drop bucket."
}

resource "google_storage_bucket_iam_member" "cpcds_drop_objects" {
  count = local.cpcds_drops ? 1 : 0

  bucket = google_storage_bucket.cpcds_drops[0].name
  role   = "roles/storage.objectUser"
  member = "serviceAccount:${google_service_account.cpcds_drop[0].email}"
}

resource "google_service_account_iam_member" "cpcds_drop_workload_identity" {
  count = local.cpcds_drops ? 1 : 0

  service_account_id = google_service_account.cpcds_drop[0].name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${var.kubernetes_namespace}/claims-service]"

  depends_on = [google_container_cluster.cho]
}

resource "google_service_account" "sftpgo" {
  count = local.cpcds_drops ? 1 : 0

  project      = var.project_id
  account_id   = "cho-sftpgo"
  display_name = "CHO SFTP server"
  description  = "SFTPGo's Kubernetes service account (Workload Identity): writes plans' SFTP uploads into the CPCDS drop bucket."
}

# objectUser, not objectCreator: SFTP clients upload to a temporary name and
# rename (copy + delete on a bucket), list their folders and overwrite a
# resent file. SFTPGo's per-user permissions decide which folder allows what.
resource "google_storage_bucket_iam_member" "sftpgo_objects" {
  count = local.cpcds_drops ? 1 : 0

  bucket = google_storage_bucket.cpcds_drops[0].name
  role   = "roles/storage.objectUser"
  member = "serviceAccount:${google_service_account.sftpgo[0].email}"
}

resource "google_service_account_iam_member" "sftpgo_workload_identity" {
  count = local.cpcds_drops ? 1 : 0

  service_account_id = google_service_account.sftpgo[0].name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${var.cpcds_sftp_namespace}/sftpgo]"

  depends_on = [google_container_cluster.cho]
}

resource "google_compute_address" "cpcds_sftp" {
  count = local.cpcds_drops ? 1 : 0

  project = var.project_id
  name    = "cho-cpcds-sftp"
  region  = var.region

  depends_on = [google_project_service.required]
}

output "cpcds_drop_bucket" {
  description = "GitHub variable CPCDS_DROP_BUCKET (empty when enable_cpcds_drops is false): claims-service's CpcdsDrop:Gcs:Bucket and SFTPGo's GCS bucket."
  value       = local.cpcds_drops ? google_storage_bucket.cpcds_drops[0].name : ""
}

output "cpcds_drop_service_account" {
  description = "GitHub variable CPCDS_DROP_SERVICE_ACCOUNT: the Google account claims-service acts as to load the drop."
  value       = local.cpcds_drops ? google_service_account.cpcds_drop[0].email : ""
}

output "cpcds_sftpgo_service_account" {
  description = "GitHub variable CPCDS_SFTPGO_SERVICE_ACCOUNT: the Google account SFTPGo acts as."
  value       = local.cpcds_drops ? google_service_account.sftpgo[0].email : ""
}

output "cpcds_sftp_address" {
  description = "The SFTP server's static address: what plans connect to and allow-list."
  value       = local.cpcds_drops ? google_compute_address.cpcds_sftp[0].address : ""
}

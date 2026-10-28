# Bulk Data $export files (G-34, owner decision Q-18).
#
# Off by default (enable_bulk_export = false). Enabled, this creates:
#   - a private Cloud Storage bucket for the NDJSON files fhir-service writes
#     for a Provider Access bulk export. It is not the documents bucket: these
#     files are member data held for a day, so the bucket is unversioned (a
#     deleted export is gone, not kept as a noncurrent version) and a
#     lifecycle rule deletes anything older than bulk_export_retention_days,
#     behind fhir-service's own deletion after BulkExport:PurgeAfter
#   - object access to that bucket for fhir-service's Google identity: the
#     managed FHIR store publisher when enable_managed_fhir_store is on (a
#     Kubernetes service account maps to one Google account), otherwise a
#     cho-fhir-service account bound to fhir-service-sa
#
# Clients never get a storage URL. They download through fhir-service with
# their own token (the manifest says requiresAccessToken: true).

variable "enable_bulk_export" {
  description = "Create the bucket fhir-service writes Bulk Data $export files to."
  type        = bool
  default     = false
}

variable "bulk_export_bucket_name" {
  description = "Name of the bulk export bucket. Empty uses <project_id>-cho-bulk-exports (bucket names are global)."
  type        = string
  default     = ""
}

variable "bulk_export_retention_days" {
  description = "Age in days after which the bucket deletes an export file whatever fhir-service did. Keep it at or above BulkExport:PurgeAfter (2 days)."
  type        = number
  default     = 2
}

locals {
  bulk_export = var.enable_bulk_export
  fhir_service_google_account = (
    local.managed_fhir ? google_service_account.fhir_store[0].email
    : local.bulk_export ? google_service_account.fhir_service[0].email
    : ""
  )
}

resource "google_storage_bucket" "bulk_exports" {
  count = local.bulk_export ? 1 : 0

  project  = var.project_id
  name     = coalesce(var.bulk_export_bucket_name, "${var.project_id}-cho-bulk-exports")
  location = var.region
  labels   = local.labels

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  # Nothing here outlives a few days by design.
  force_destroy = true

  versioning {
    enabled = false
  }

  lifecycle_rule {
    condition {
      age = var.bulk_export_retention_days
    }
    action {
      type = "Delete"
    }
  }

  depends_on = [google_project_service.required]
}

# fhir-service's Google identity when the managed FHIR store has not already
# given it one.
resource "google_service_account" "fhir_service" {
  count = local.bulk_export && !local.managed_fhir ? 1 : 0

  project      = var.project_id
  account_id   = "cho-fhir-service"
  display_name = "CHO fhir-service"
  description  = "fhir-service's Kubernetes service account (Workload Identity): writes and reads Bulk Data export files."
}

resource "google_service_account_iam_member" "fhir_service_workload_identity" {
  count = local.bulk_export && !local.managed_fhir ? 1 : 0

  service_account_id = google_service_account.fhir_service[0].name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${var.kubernetes_namespace}/fhir-service-sa]"

  depends_on = [google_container_cluster.cho]
}

resource "google_storage_bucket_iam_member" "bulk_exports_objects" {
  count = local.bulk_export ? 1 : 0

  bucket = google_storage_bucket.bulk_exports[0].name
  role   = "roles/storage.objectUser"
  member = "serviceAccount:${local.fhir_service_google_account}"
}

output "bulk_export_bucket" {
  description = "GitHub variable BULK_EXPORT_BUCKET (empty when enable_bulk_export is false)."
  value       = local.bulk_export ? google_storage_bucket.bulk_exports[0].name : ""
}

output "fhir_service_google_account" {
  description = "GitHub variable FHIR_SERVICE_GOOGLE_ACCOUNT: the Google account fhir-service-sa acts as (the managed FHIR store publisher when that is on)."
  value       = local.fhir_service_google_account
}

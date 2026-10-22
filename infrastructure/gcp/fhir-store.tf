# Managed FHIR store → BigQuery → Vertex AI (ADR 023).
#
# Off by default (enable_managed_fhir_store = false). Enabled, this creates:
#   - a Cloud Healthcare API dataset and an R4 FHIR store that fhir-service
#     publishes finalized claims' ExplanationOfBenefit into, at CHO's own ids
#     (enable_update_create). CHO stays the system of record (ADR 008); the
#     store is the plan's cloud-native copy
#   - BigQuery streaming from the store (ANALYTICS_V2 schema), so every write
#     lands as queryable rows within seconds, with no ETL to build or run
#   - Pub/Sub change notifications from the store
#   - a private Cloud Storage bucket for NDJSON exports of the store
#   - a Google service account for fhir-service, bound to its Kubernetes
#     service account through Workload Identity, allowed to write FHIR
#     resources and start exports in this store and nothing else
#   - with enable_vertex_ai, a BigQuery Cloud resource connection allowed to
#     call Vertex AI, so BigQuery ML remote models (Gemini) can run over the
#     streamed FHIR tables in SQL. See docs/architecture/managed-fhir-store-analytics.md.
#
# DEMO-GRADE like the rest of this directory: synthetic data only, no BAA, no
# CMEK. The FHIR store is a PHI store in production; see the ADR before
# pointing real members at it.

variable "enable_managed_fhir_store" {
  description = "Create the Cloud Healthcare API FHIR store fhir-service publishes to, with BigQuery streaming."
  type        = bool
  default     = false
}

variable "managed_fhir_store_location" {
  description = "Cloud Healthcare API location for the dataset (a Healthcare API region, e.g. us-central1)."
  type        = string
  default     = "us-central1"
}

variable "managed_fhir_dataset_id" {
  description = "Cloud Healthcare API dataset id."
  type        = string
  default     = "cho"
}

variable "managed_fhir_store_id" {
  description = "FHIR store id."
  type        = string
  default     = "cho-fhir-r4"
}

variable "managed_fhir_enabled_implementation_guides" {
  description = "ImplementationGuide canonical URLs the store enforces (e.g. CARIN Blue Button). Each IG must be loaded into the store first; leave empty on the first apply."
  type        = list(string)
  default     = []
}

variable "managed_fhir_bigquery_location" {
  description = "Location of the BigQuery dataset the store streams to and of the export bucket."
  type        = string
  default     = "US"
}

variable "managed_fhir_bigquery_dataset_id" {
  description = "BigQuery dataset the store streams to."
  type        = string
  default     = "cho_fhir"
}

variable "managed_fhir_export_retention_days" {
  description = "Days NDJSON exports are kept in the export bucket."
  type        = number
  default     = 7
}

variable "managed_fhir_force_destroy" {
  description = "Let terraform destroy delete the BigQuery tables and export objects. Off by default."
  type        = bool
  default     = false
}

variable "enable_vertex_ai" {
  description = "With the managed FHIR store: a BigQuery connection allowed to call Vertex AI, for BigQuery ML remote models over the FHIR tables."
  type        = bool
  default     = false
}

locals {
  managed_fhir             = var.enable_managed_fhir_store
  vertex_ai                = var.enable_managed_fhir_store && var.enable_vertex_ai
  healthcare_service_agent = "serviceAccount:service-${data.google_project.this.number}@gcp-sa-healthcare.iam.gserviceaccount.com"
}

resource "google_project_service" "managed_fhir" {
  for_each = local.managed_fhir ? toset(["healthcare.googleapis.com", "bigquery.googleapis.com", "pubsub.googleapis.com"]) : toset([])

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}

# The Healthcare service agent writes to BigQuery, Pub/Sub and the export
# bucket on the store's behalf; it exists once the API is enabled.
resource "google_project_service_identity" "healthcare" {
  provider = google-beta
  count    = local.managed_fhir ? 1 : 0

  project = var.project_id
  service = "healthcare.googleapis.com"

  depends_on = [google_project_service.managed_fhir]
}

resource "google_healthcare_dataset" "cho" {
  count = local.managed_fhir ? 1 : 0

  project   = var.project_id
  name      = var.managed_fhir_dataset_id
  location  = var.managed_fhir_store_location
  time_zone = "UTC"

  depends_on = [google_project_service.managed_fhir]
}

# ── Change notifications ─────────────────────────────────────────────────────

resource "google_pubsub_topic" "managed_fhir_changes" {
  count = local.managed_fhir ? 1 : 0

  project = var.project_id
  name    = "${var.managed_fhir_store_id}-changes"
  labels  = local.labels

  depends_on = [google_project_service.managed_fhir]
}

resource "google_pubsub_topic_iam_member" "healthcare_publisher" {
  count = local.managed_fhir ? 1 : 0

  project = var.project_id
  topic   = google_pubsub_topic.managed_fhir_changes[0].name
  role    = "roles/pubsub.publisher"
  member  = local.healthcare_service_agent

  depends_on = [google_project_service_identity.healthcare]
}

# ── BigQuery streaming destination ───────────────────────────────────────────

resource "google_bigquery_dataset" "managed_fhir" {
  count = local.managed_fhir ? 1 : 0

  project                    = var.project_id
  dataset_id                 = var.managed_fhir_bigquery_dataset_id
  location                   = var.managed_fhir_bigquery_location
  description                = "Streaming mirror of the CHO managed FHIR store (ANALYTICS_V2 schema)."
  delete_contents_on_destroy = var.managed_fhir_force_destroy
  labels                     = local.labels

  depends_on = [google_project_service.managed_fhir]
}

resource "google_bigquery_dataset_iam_member" "healthcare_bq_editor" {
  count = local.managed_fhir ? 1 : 0

  project    = var.project_id
  dataset_id = google_bigquery_dataset.managed_fhir[0].dataset_id
  role       = "roles/bigquery.dataEditor"
  member     = local.healthcare_service_agent

  depends_on = [google_project_service_identity.healthcare]
}

resource "google_project_iam_member" "healthcare_bq_job_user" {
  count = local.managed_fhir ? 1 : 0

  project = var.project_id
  role    = "roles/bigquery.jobUser"
  member  = local.healthcare_service_agent

  depends_on = [google_project_service_identity.healthcare]
}

# ── FHIR store ───────────────────────────────────────────────────────────────

resource "google_healthcare_fhir_store" "cho" {
  count = local.managed_fhir ? 1 : 0

  name    = var.managed_fhir_store_id
  dataset = google_healthcare_dataset.cho[0].id
  version = "R4"

  # CHO assigns ids (ExplanationOfBenefit id = the claim's version chain id),
  # so publishing is an idempotent PUT.
  enable_update_create = true

  # CHO publishes resources as their source events arrive, so an EOB can land
  # before the Patient or Coverage it references. Integrity is CHO's job
  # (it is the system of record); the copy accepts references it cannot
  # resolve yet.
  disable_referential_integrity = true

  complex_data_type_reference_parsing = "ENABLED"

  notification_configs {
    pubsub_topic = google_pubsub_topic.managed_fhir_changes[0].id
  }

  stream_configs {
    resource_types = []
    bigquery_destination {
      dataset_uri = "bq://${var.project_id}.${google_bigquery_dataset.managed_fhir[0].dataset_id}"
      schema_config {
        schema_type               = "ANALYTICS_V2"
        recursive_structure_depth = 3
      }
    }
  }

  dynamic "validation_config" {
    for_each = length(var.managed_fhir_enabled_implementation_guides) > 0 ? [1] : []
    content {
      disable_profile_validation    = false
      enabled_implementation_guides = var.managed_fhir_enabled_implementation_guides
    }
  }

  labels = local.labels

  depends_on = [
    google_pubsub_topic_iam_member.healthcare_publisher,
    google_bigquery_dataset_iam_member.healthcare_bq_editor,
    google_project_iam_member.healthcare_bq_job_user,
  ]
}

# Record who read and wrote FHIR resources (data access logs are off by default).
resource "google_project_iam_audit_config" "healthcare" {
  count = local.managed_fhir ? 1 : 0

  project = var.project_id
  service = "healthcare.googleapis.com"

  audit_log_config {
    log_type = "DATA_READ"
  }
  audit_log_config {
    log_type = "DATA_WRITE"
  }
}

# ── Exports ──────────────────────────────────────────────────────────────────

resource "google_storage_bucket" "managed_fhir_export" {
  count = local.managed_fhir ? 1 : 0

  project  = var.project_id
  name     = "${var.project_id}-cho-fhir-export"
  location = var.managed_fhir_bigquery_location
  labels   = local.labels

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  force_destroy               = var.managed_fhir_force_destroy

  lifecycle_rule {
    condition {
      age = var.managed_fhir_export_retention_days
    }
    action {
      type = "Delete"
    }
  }

  depends_on = [google_project_service.required]
}

resource "google_storage_bucket_iam_member" "healthcare_export_writer" {
  count = local.managed_fhir ? 1 : 0

  bucket = google_storage_bucket.managed_fhir_export[0].name
  role   = "roles/storage.objectAdmin"
  member = local.healthcare_service_agent

  depends_on = [google_project_service_identity.healthcare]
}

# ── fhir-service identity (Workload Identity) ────────────────────────────────

resource "google_service_account" "fhir_store" {
  count = local.managed_fhir ? 1 : 0

  project      = var.project_id
  account_id   = "cho-fhir-store"
  display_name = "CHO managed FHIR store publisher"
  description  = "fhir-service's Kubernetes service account (Workload Identity): writes CHO projections to the managed FHIR store."
}

resource "google_healthcare_fhir_store_iam_member" "fhir_store_writer" {
  count = local.managed_fhir ? 1 : 0

  fhir_store_id = google_healthcare_fhir_store.cho[0].id
  role          = "roles/healthcare.fhirResourceEditor"
  member        = "serviceAccount:${google_service_account.fhir_store[0].email}"
}

# Starting a store-level export is a store operation, not a resource one.
resource "google_healthcare_fhir_store_iam_member" "fhir_store_exporter" {
  count = local.managed_fhir ? 1 : 0

  fhir_store_id = google_healthcare_fhir_store.cho[0].id
  role          = "roles/healthcare.fhirStoreAdmin"
  member        = "serviceAccount:${google_service_account.fhir_store[0].email}"
}

resource "google_service_account_iam_member" "fhir_store_workload_identity" {
  count = local.managed_fhir ? 1 : 0

  service_account_id = google_service_account.fhir_store[0].name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${var.kubernetes_namespace}/fhir-service-sa]"

  depends_on = [google_container_cluster.cho]
}

# ── Vertex AI over the streamed FHIR tables ──────────────────────────────────

resource "google_project_service" "vertex_ai" {
  for_each = local.vertex_ai ? toset(["aiplatform.googleapis.com", "bigqueryconnection.googleapis.com"]) : toset([])

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}

# BigQuery calls Vertex AI as this connection's service account when a query
# uses a remote model (CREATE MODEL ... REMOTE WITH CONNECTION).
resource "google_bigquery_connection" "vertex_ai" {
  count = local.vertex_ai ? 1 : 0

  project       = var.project_id
  connection_id = "cho-vertex-ai"
  location      = var.managed_fhir_bigquery_location
  friendly_name = "CHO Vertex AI"
  description   = "BigQuery ML remote models over the CHO managed FHIR store tables."

  cloud_resource {}

  depends_on = [google_project_service.vertex_ai]
}

resource "google_project_iam_member" "vertex_ai_connection_user" {
  count = local.vertex_ai ? 1 : 0

  project = var.project_id
  role    = "roles/aiplatform.user"
  member  = "serviceAccount:${google_bigquery_connection.vertex_ai[0].cloud_resource[0].service_account_id}"
}

# ── Outputs ──────────────────────────────────────────────────────────────────

output "managed_fhir_store_id" {
  description = "GitHub variable MANAGED_FHIR_STORE (empty when enable_managed_fhir_store is false)."
  value       = local.managed_fhir ? google_healthcare_fhir_store.cho[0].id : ""
}

output "managed_fhir_store_service_account" {
  description = "GitHub variable MANAGED_FHIR_STORE_SERVICE_ACCOUNT (fhir-service's Google identity)."
  value       = local.managed_fhir ? google_service_account.fhir_store[0].email : ""
}

output "managed_fhir_bigquery_dataset" {
  description = "BigQuery dataset the FHIR store streams to (project.dataset)."
  value       = local.managed_fhir ? "${var.project_id}.${google_bigquery_dataset.managed_fhir[0].dataset_id}" : ""
}

output "managed_fhir_export_bucket" {
  description = "Destination for store-level NDJSON exports."
  value       = local.managed_fhir ? "gs://${google_storage_bucket.managed_fhir_export[0].name}" : ""
}

output "managed_fhir_vertex_ai_connection" {
  description = "BigQuery connection for remote models (project.location.connection), when enable_vertex_ai is true."
  value       = local.vertex_ai ? "${var.project_id}.${lower(var.managed_fhir_bigquery_location)}.${google_bigquery_connection.vertex_ai[0].connection_id}" : ""
}

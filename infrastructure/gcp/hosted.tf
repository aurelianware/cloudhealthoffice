# The hosted profile: the base for a HIPAA environment that holds real member
# data (docs/deploy/GCP-HOSTED-HIPAA.md).
#
# Off by default (hosted = false), which leaves the demo exactly as it was.
# Turn it on only in a NEW project, under an organization, in which the Google
# Cloud BAA has been accepted (console: IAM & Admin > Privacy). Never turn it on
# in the demo project: several settings below, such as the cluster's network
# and encryption, replace the cluster and the image repository.
#
# On, this creates or changes:
#   - customer-managed encryption keys (Cloud KMS, rotated every 90 days) for
#     the cluster's Kubernetes secrets, node and persistent disks, every Cloud
#     Storage bucket and the Artifact Registry repository, with each Google
#     service agent allowed to use its own key only
#   - a VPC with Private Google Access and flow logs, and Cloud NAT for
#     outbound traffic, so nodes have no external addresses
#   - a regional, private-node Standard cluster on that VPC with Shielded
#     nodes, a dedicated least-privilege node service account, balanced
#     persistent disks, and a control plane reached through its DNS endpoint
#     with IAM (no public IP access unless hosted_authorized_networks lists one)
#   - organization policies on the project: US-only locations, no service
#     account keys, no external VM addresses, no serial port, OS Login,
#     uniform and non-public buckets
#   - data access audit logs for every service, routed to a log bucket kept
#     for six years (lockable with hosted_lock_audit_logs)
#   - deletion protection on the cluster, whatever deletion_protection says
#
# Not here: the managed FHIR store copy (refused while hosted is on), the
# Kubernetes side (an encrypted StorageClass, Production hosts,
# Secret Manager), managed Kafka and Redis, the load balancer and IAP, backups.
# Those are the next workstreams in the build plan.

variable "hosted" {
  description = "Build the hosted HIPAA profile (customer-managed keys, private regional cluster, org policies, six-year audit logs). Only in a new project with the Google Cloud BAA accepted."
  type        = bool
  default     = false
}

variable "hosted_kms_protection_level" {
  description = "Cloud KMS protection level for the customer-managed encryption keys: SOFTWARE or HSM."
  type        = string
  default     = "HSM"

  validation {
    condition     = contains(["SOFTWARE", "HSM"], var.hosted_kms_protection_level)
    error_message = "hosted_kms_protection_level must be SOFTWARE or HSM."
  }
}

variable "hosted_subnet_cidr" {
  description = "Primary range of the hosted subnet (nodes)."
  type        = string
  default     = "10.10.0.0/20"
}

variable "hosted_pods_cidr" {
  description = "Secondary range for pods."
  type        = string
  default     = "10.20.0.0/14"
}

variable "hosted_services_cidr" {
  description = "Secondary range for Kubernetes services."
  type        = string
  default     = "10.24.0.0/20"
}

variable "hosted_authorized_networks" {
  description = "CIDR blocks allowed to reach the control plane's IP endpoint. Empty: no internet access by IP; use the DNS endpoint (gcloud container clusters get-credentials --dns-endpoint), which IAM guards."
  type = list(object({
    cidr_block   = string
    display_name = string
  }))
  default = []
}

variable "hosted_enforce_org_policies" {
  description = "Set the organization policy constraints on the project. Needs the project to sit in an organization and the applier to hold Organization Policy Administrator."
  type        = bool
  default     = true
}

variable "hosted_audit_log_retention_days" {
  description = "Days the audit log bucket keeps entries. HIPAA keeps required documentation six years."
  type        = number
  default     = 2192
}

variable "hosted_lock_audit_logs" {
  description = "Lock the audit log bucket's retention. Irreversible: a locked bucket can't be shortened or deleted until every entry ages out. Turn on once the retention is agreed."
  type        = bool
  default     = false
}

locals {
  hosted = var.hosted

  hosted_services = local.hosted ? toset([
    "cloudkms.googleapis.com",
    "compute.googleapis.com",
    "logging.googleapis.com",
    "orgpolicy.googleapis.com",
  ]) : toset([])

  # The KMS keys each resource encrypts with; null outside the hosted profile.
  gke_secrets_kms_key = one(google_kms_crypto_key.hosted_gke_secrets[*].id)
  disk_kms_key        = one(google_kms_crypto_key.hosted_disks[*].id)
  storage_kms_key     = one(google_kms_crypto_key.hosted_storage[*].id)
  artifacts_kms_key   = one(google_kms_crypto_key.hosted_artifacts[*].id)

  # Ninety days, in seconds.
  hosted_key_rotation = "7776000s"
}

resource "google_project_service" "hosted" {
  for_each = local.hosted_services

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}

# ── Customer-managed encryption keys ─────────────────────────────────────────

resource "google_kms_key_ring" "hosted" {
  count = local.hosted ? 1 : 0

  project  = var.project_id
  name     = "cho-cmek"
  location = var.region

  depends_on = [google_project_service.hosted]

  lifecycle {
    # The managed FHIR store copy (fhir-store.tf) writes to a Healthcare
    # dataset, BigQuery and a multi-region export bucket that this profile
    # does not encrypt with its keys yet.
    precondition {
      condition     = !var.enable_managed_fhir_store
      error_message = "The hosted profile does not cover the managed FHIR store copy yet; set enable_managed_fhir_store = false."
    }
  }
}

resource "google_kms_crypto_key" "hosted_gke_secrets" {
  count = local.hosted ? 1 : 0

  name            = "gke-secrets"
  key_ring        = google_kms_key_ring.hosted[0].id
  purpose         = "ENCRYPT_DECRYPT"
  rotation_period = local.hosted_key_rotation
  labels          = local.labels

  version_template {
    algorithm        = "GOOGLE_SYMMETRIC_ENCRYPTION"
    protection_level = var.hosted_kms_protection_level
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "google_kms_crypto_key" "hosted_disks" {
  count = local.hosted ? 1 : 0

  name            = "disks"
  key_ring        = google_kms_key_ring.hosted[0].id
  purpose         = "ENCRYPT_DECRYPT"
  rotation_period = local.hosted_key_rotation
  labels          = local.labels

  version_template {
    algorithm        = "GOOGLE_SYMMETRIC_ENCRYPTION"
    protection_level = var.hosted_kms_protection_level
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "google_kms_crypto_key" "hosted_storage" {
  count = local.hosted ? 1 : 0

  name            = "storage"
  key_ring        = google_kms_key_ring.hosted[0].id
  purpose         = "ENCRYPT_DECRYPT"
  rotation_period = local.hosted_key_rotation
  labels          = local.labels

  version_template {
    algorithm        = "GOOGLE_SYMMETRIC_ENCRYPTION"
    protection_level = var.hosted_kms_protection_level
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "google_kms_crypto_key" "hosted_artifacts" {
  count = local.hosted ? 1 : 0

  name            = "artifacts"
  key_ring        = google_kms_key_ring.hosted[0].id
  purpose         = "ENCRYPT_DECRYPT"
  rotation_period = local.hosted_key_rotation
  labels          = local.labels

  version_template {
    algorithm        = "GOOGLE_SYMMETRIC_ENCRYPTION"
    protection_level = var.hosted_kms_protection_level
  }

  lifecycle {
    prevent_destroy = true
  }
}

# Each Google service agent may use its own key, and nothing else.

data "google_storage_project_service_account" "gcs" {
  count   = local.hosted ? 1 : 0
  project = var.project_id
}

resource "google_project_service_identity" "artifact_registry" {
  provider = google-beta
  count    = local.hosted ? 1 : 0

  project = var.project_id
  service = "artifactregistry.googleapis.com"

  depends_on = [google_project_service.required]
}

resource "google_kms_crypto_key_iam_member" "hosted_gke_secrets" {
  count = local.hosted ? 1 : 0

  crypto_key_id = google_kms_crypto_key.hosted_gke_secrets[0].id
  role          = "roles/cloudkms.cryptoKeyEncrypterDecrypter"
  member        = "serviceAccount:service-${data.google_project.this.number}@container-engine-robot.iam.gserviceaccount.com"

  depends_on = [google_project_service.required]
}

resource "google_kms_crypto_key_iam_member" "hosted_disks" {
  count = local.hosted ? 1 : 0

  crypto_key_id = google_kms_crypto_key.hosted_disks[0].id
  role          = "roles/cloudkms.cryptoKeyEncrypterDecrypter"
  member        = "serviceAccount:service-${data.google_project.this.number}@compute-system.iam.gserviceaccount.com"

  depends_on = [google_project_service.hosted]
}

resource "google_kms_crypto_key_iam_member" "hosted_storage" {
  count = local.hosted ? 1 : 0

  crypto_key_id = google_kms_crypto_key.hosted_storage[0].id
  role          = "roles/cloudkms.cryptoKeyEncrypterDecrypter"
  member        = "serviceAccount:${data.google_storage_project_service_account.gcs[0].email_address}"
}

resource "google_kms_crypto_key_iam_member" "hosted_artifacts" {
  count = local.hosted ? 1 : 0

  crypto_key_id = google_kms_crypto_key.hosted_artifacts[0].id
  role          = "roles/cloudkms.cryptoKeyEncrypterDecrypter"
  member        = "serviceAccount:${google_project_service_identity.artifact_registry[0].email}"
}

# ── Network ──────────────────────────────────────────────────────────────────

resource "google_compute_network" "hosted" {
  count = local.hosted ? 1 : 0

  project                 = var.project_id
  name                    = "cho-hosted"
  auto_create_subnetworks = false

  depends_on = [google_project_service.hosted]
}

resource "google_compute_subnetwork" "hosted" {
  count = local.hosted ? 1 : 0

  project                  = var.project_id
  name                     = "cho-hosted-${var.region}"
  region                   = var.region
  network                  = google_compute_network.hosted[0].id
  ip_cidr_range            = var.hosted_subnet_cidr
  private_ip_google_access = true

  secondary_ip_range {
    range_name    = "pods"
    ip_cidr_range = var.hosted_pods_cidr
  }

  secondary_ip_range {
    range_name    = "services"
    ip_cidr_range = var.hosted_services_cidr
  }

  log_config {
    aggregation_interval = "INTERVAL_5_SEC"
    flow_sampling        = 0.5
    metadata             = "INCLUDE_ALL_METADATA"
  }
}

resource "google_compute_router" "hosted" {
  count = local.hosted ? 1 : 0

  project = var.project_id
  name    = "cho-hosted"
  region  = var.region
  network = google_compute_network.hosted[0].id
}

# Outbound only (image pulls outside Google, NPPES, CMS): nodes keep no
# external address.
resource "google_compute_router_nat" "hosted" {
  count = local.hosted ? 1 : 0

  project                            = var.project_id
  name                               = "cho-hosted"
  router                             = google_compute_router.hosted[0].name
  region                             = var.region
  nat_ip_allocate_option             = "AUTO_ONLY"
  source_subnetwork_ip_ranges_to_nat = "ALL_SUBNETWORKS_ALL_IP_RANGES"

  log_config {
    enable = true
    filter = "ERRORS_ONLY"
  }
}

# ── Node identity ────────────────────────────────────────────────────────────

# Nodes run as their own account with only what logging, monitoring and image
# pulls need, instead of the default compute account (Editor on the project).
resource "google_service_account" "nodes" {
  count = local.hosted ? 1 : 0

  project      = var.project_id
  account_id   = "cho-nodes"
  display_name = "CHO hosted GKE nodes"
}

resource "google_project_iam_member" "nodes" {
  for_each = local.hosted ? toset([
    "roles/logging.logWriter",
    "roles/monitoring.metricWriter",
    "roles/monitoring.viewer",
    "roles/stackdriver.resourceMetadata.writer",
    "roles/autoscaling.metricsWriter",
  ]) : toset([])

  project = var.project_id
  role    = each.value
  member  = "serviceAccount:${google_service_account.nodes[0].email}"
}

resource "google_artifact_registry_repository_iam_member" "hosted_nodes_pull" {
  count = local.hosted ? 1 : 0

  project    = var.project_id
  location   = google_artifact_registry_repository.cho.location
  repository = google_artifact_registry_repository.cho.name
  role       = "roles/artifactregistry.reader"
  member     = "serviceAccount:${google_service_account.nodes[0].email}"
}

# ── Organization policies ────────────────────────────────────────────────────

locals {
  hosted_boolean_policies = local.hosted && var.hosted_enforce_org_policies ? toset([
    "iam.disableServiceAccountKeyCreation",
    "compute.requireOsLogin",
    "compute.disableSerialPortAccess",
    "storage.uniformBucketLevelAccess",
    "storage.publicAccessPrevention",
  ]) : toset([])
}

resource "google_org_policy_policy" "hosted_boolean" {
  for_each = local.hosted_boolean_policies

  name   = "projects/${var.project_id}/policies/${each.value}"
  parent = "projects/${var.project_id}"

  spec {
    rules {
      enforce = "TRUE"
    }
  }

  depends_on = [google_project_service.hosted]
}

resource "google_org_policy_policy" "hosted_locations" {
  count = local.hosted && var.hosted_enforce_org_policies ? 1 : 0

  name   = "projects/${var.project_id}/policies/gcp.resourceLocations"
  parent = "projects/${var.project_id}"

  spec {
    rules {
      values {
        allowed_values = ["in:us-locations"]
      }
    }
  }

  depends_on = [google_project_service.hosted]
}

resource "google_org_policy_policy" "hosted_no_vm_external_ip" {
  count = local.hosted && var.hosted_enforce_org_policies ? 1 : 0

  name   = "projects/${var.project_id}/policies/compute.vmExternalIpAccess"
  parent = "projects/${var.project_id}"

  spec {
    rules {
      deny_all = "TRUE"
    }
  }

  depends_on = [google_project_service.hosted]
}

# ── Audit logs ───────────────────────────────────────────────────────────────

# Who read and wrote what, for every service (data access logs are off by
# default). Admin activity logs are always on.
resource "google_project_iam_audit_config" "hosted_all" {
  count = local.hosted ? 1 : 0

  project = var.project_id
  service = "allServices"

  audit_log_config {
    log_type = "ADMIN_READ"
  }
  audit_log_config {
    log_type = "DATA_READ"
  }
  audit_log_config {
    log_type = "DATA_WRITE"
  }
}

resource "google_logging_project_bucket_config" "hosted_audit" {
  count = local.hosted ? 1 : 0

  project        = var.project_id
  location       = var.region
  bucket_id      = "cho-audit"
  description    = "Cloud Audit Logs for the hosted CHO environment, kept six years."
  retention_days = var.hosted_audit_log_retention_days
  locked         = var.hosted_lock_audit_logs

  depends_on = [google_project_service.hosted]
}

# A copy of every audit log entry goes to the six-year bucket; _Default keeps
# its own 30 days for day-to-day use.
resource "google_logging_project_sink" "hosted_audit" {
  count = local.hosted ? 1 : 0

  project     = var.project_id
  name        = "cho-audit"
  destination = "logging.googleapis.com/${google_logging_project_bucket_config.hosted_audit[0].id}"
  filter      = "logName:\"cloudaudit.googleapis.com\""

  unique_writer_identity = true
}

# ── Outputs ──────────────────────────────────────────────────────────────────

output "hosted_disk_kms_key" {
  description = "KMS key for the hosted cluster's encrypted StorageClass (disk-encryption-kms-key). Empty outside the hosted profile."
  value       = local.hosted ? local.disk_kms_key : ""
}

output "hosted_storage_kms_key" {
  description = "KMS key every hosted bucket encrypts with by default. Empty outside the hosted profile."
  value       = local.hosted ? local.storage_kms_key : ""
}

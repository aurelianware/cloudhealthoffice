# Cloud Health Office demo environment on Google Cloud.
#
# DEMO-GRADE by default. This provisions a GKE cluster (Standard by default, or Autopilot), an Artifact Registry
# repository and keyless GitHub Actions access. It is not a HIPAA production
# environment: no Google BAA, CMEK, private cluster or Private Service Connect
# is configured. Use synthetic data only. See docs/deploy/GCP-DEMO.md.
#
# With hosted = true (hosted.tf), in a separate project under a Google Cloud
# BAA, the same module builds the base of the hosted HIPAA environment
# instead. See docs/deploy/GCP-HOSTED-HIPAA.md.

locals {
  services = [
    "artifactregistry.googleapis.com",
    "cloudresourcemanager.googleapis.com",
    "container.googleapis.com",
    "iam.googleapis.com",
    "iamcredentials.googleapis.com",
    "secretmanager.googleapis.com",
    "storage.googleapis.com",
    "sts.googleapis.com",
  ]

  labels = {
    app         = "cloudhealthoffice"
    environment = var.hosted ? "hosted" : "demo"
    managed-by  = "terraform"
  }
}

resource "google_project_service" "required" {
  for_each = toset(local.services)

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}

data "google_project" "this" {
  project_id = var.project_id
}

# ── Container images ─────────────────────────────────────────────────────────

resource "google_artifact_registry_repository" "cho" {
  project       = var.project_id
  location      = var.region
  repository_id = var.artifact_repository_id
  description   = var.hosted ? "Cloud Health Office service images" : "Cloud Health Office service images (demo)"
  format        = "DOCKER"
  labels        = local.labels
  kms_key_name  = local.artifacts_kms_key

  # Keep only recent images so the demo repository doesn't grow without bound.
  cleanup_policies {
    id     = "keep-recent"
    action = "KEEP"
    most_recent_versions {
      keep_count = 5
    }
  }

  depends_on = [google_project_service.required, google_kms_crypto_key_iam_member.hosted_artifacts]
}

# ── Cluster ──────────────────────────────────────────────────────────────────

# Two shapes, chosen by var.autopilot:
#   - Standard (default): one zonal node on standard persistent disk. Fits a
#     new billing account's default quotas (12 vCPU, 250 GB SSD, 4 external
#     IPs), which Google will not raise until the account has billing history.
#   - Autopilot: regional, scales with the workload; needs roughly 24 vCPU and
#     500 GB of SSD quota for the full demo.
# Switching between them replaces the cluster.

locals {
  # The hosted profile is always regional (three zones).
  cluster_location = var.autopilot || var.hosted ? var.region : var.zone
}

resource "google_container_cluster" "cho" {
  project  = var.project_id
  name     = var.cluster_name
  location = local.cluster_location

  enable_autopilot    = var.autopilot ? true : null # false conflicts with remove_default_node_pool
  deletion_protection = var.hosted ? true : var.deletion_protection

  # Hosted: on its own VPC (hosted.tf), VPC-native, with private nodes.
  network    = one(google_compute_network.hosted[*].id)
  subnetwork = one(google_compute_subnetwork.hosted[*].id)

  dynamic "ip_allocation_policy" {
    for_each = var.hosted ? [1] : []
    content {
      cluster_secondary_range_name  = "pods"
      services_secondary_range_name = "services"
    }
  }

  # Private nodes; the control plane is reached through Private Service
  # Connect inside the VPC, or from outside through its DNS endpoint, which
  # checks IAM on every call. Its IP endpoint takes only the listed networks.
  dynamic "private_cluster_config" {
    for_each = var.hosted ? [1] : []
    content {
      enable_private_nodes = true
    }
  }

  dynamic "control_plane_endpoints_config" {
    for_each = var.hosted ? [1] : []
    content {
      dns_endpoint_config {
        allow_external_traffic = true
      }
    }
  }

  dynamic "master_authorized_networks_config" {
    for_each = var.hosted ? [1] : []
    content {
      gcp_public_cidrs_access_enabled = false
      dynamic "cidr_blocks" {
        for_each = var.hosted_authorized_networks
        content {
          cidr_block   = cidr_blocks.value.cidr_block
          display_name = cidr_blocks.value.display_name
        }
      }
    }
  }

  # Kubernetes secrets are encrypted again, at the application layer, with a
  # customer-managed key.
  dynamic "database_encryption" {
    for_each = var.hosted ? [1] : []
    content {
      state    = "ENCRYPTED"
      key_name = local.gke_secrets_kms_key
    }
  }

  enable_shielded_nodes = var.autopilot ? null : true

  # Standard: the default pool is created with one small node and removed
  # once the cluster exists; google_container_node_pool.demo holds the nodes.
  remove_default_node_pool = var.autopilot ? null : true
  initial_node_count       = var.autopilot ? null : 1

  dynamic "node_config" {
    for_each = var.autopilot ? [] : [1]
    content {
      machine_type      = "e2-small"
      disk_type         = var.hosted ? "pd-balanced" : "pd-standard"
      disk_size_gb      = 30
      boot_disk_kms_key = local.disk_kms_key
      service_account   = one(google_service_account.nodes[*].email)
    }
  }

  release_channel {
    channel = "REGULAR"
  }

  # Hosted: the Backup for GKE agent, for the daily backup plan
  # (hosted-operations.tf).
  dynamic "addons_config" {
    for_each = var.hosted ? [1] : []
    content {
      gke_backup_agent_config {
        enabled = true
      }
    }
  }

  # Workload Identity: pods act as Google service accounts without keys
  # (document storage below). Autopilot always has it; Standard needs it set.
  workload_identity_config {
    workload_pool = "${var.project_id}.svc.id.goog"
  }

  resource_labels = local.labels

  depends_on = [
    google_project_service.required,
    google_kms_crypto_key_iam_member.hosted_gke_secrets,
    google_kms_crypto_key_iam_member.hosted_disks,
    google_compute_router_nat.hosted,
  ]

  lifecycle {
    # The default pool's settings only apply at creation.
    ignore_changes = [node_config]

    precondition {
      condition     = !(var.hosted && var.autopilot)
      error_message = "The hosted profile builds a Standard cluster; set autopilot = false."
    }
  }
}

resource "google_container_node_pool" "demo" {
  count = var.autopilot ? 0 : 1

  project    = var.project_id
  name       = var.hosted ? "primary" : "demo"
  cluster    = google_container_cluster.cho.name
  location   = local.cluster_location
  node_count = var.node_count

  node_config {
    machine_type = var.node_machine_type
    # pd-standard counts against the standard-disk quota, not the SSD quota.
    # Hosted: balanced (SSD) disks, encrypted with the customer-managed key.
    disk_type         = var.hosted ? "pd-balanced" : "pd-standard"
    disk_size_gb      = var.node_disk_size_gb
    boot_disk_kms_key = local.disk_kms_key
    oauth_scopes      = ["https://www.googleapis.com/auth/cloud-platform"]
    labels            = local.labels

    # Hosted: a least-privilege node account (hosted.tf). Demo: the default
    # compute account.
    service_account = one(google_service_account.nodes[*].email)

    dynamic "shielded_instance_config" {
      for_each = var.hosted ? [1] : []
      content {
        enable_secure_boot          = true
        enable_integrity_monitoring = true
      }
    }

    # Pods get their Kubernetes service account's Google identity, not the node's.
    workload_metadata_config {
      mode = "GKE_METADATA"
    }
  }

  management {
    auto_repair  = true
    auto_upgrade = true
  }

  # Replace the node in place rather than adding a surge node first: a second
  # node would not fit a new account's 12-vCPU quota. Upgrades and node
  # config changes (such as enabling Workload Identity) cause a short outage.
  # Hosted: surge one node first, so upgrades don't take a zone's capacity away.
  upgrade_settings {
    max_surge       = var.hosted ? 1 : 0
    max_unavailable = var.hosted ? 0 : 1
  }
}

# ── Document storage ─────────────────────────────────────────────────────────

# One private bucket for every document the services store (IDocumentStore's
# Gcs provider); each CHO container is a prefix inside it. Versioning keeps a
# replaced or deleted object recoverable for var.documents_noncurrent_days.
resource "google_storage_bucket" "documents" {
  project  = var.project_id
  name     = coalesce(var.documents_bucket_name, "${var.project_id}-cho-documents")
  location = var.region
  labels   = local.labels

  uniform_bucket_level_access = true
  public_access_prevention    = "enforced"
  force_destroy               = var.documents_force_destroy

  versioning {
    enabled = true
  }

  dynamic "encryption" {
    for_each = var.hosted ? [1] : []
    content {
      default_kms_key_name = local.storage_kms_key
    }
  }

  # Replaced and deleted versions are kept for a while, then removed.
  lifecycle_rule {
    condition {
      days_since_noncurrent_time = var.documents_noncurrent_days
    }
    action {
      type = "Delete"
    }
  }

  # Pre-signed uploads land in a "<path>.upload" staging object that finalize
  # moves into place; one never finalized is abandoned after a day.
  lifecycle_rule {
    condition {
      age            = 1
      matches_suffix = [".upload"]
    }
    action {
      type = "Delete"
    }
  }

  depends_on = [google_project_service.required, google_kms_crypto_key_iam_member.hosted_storage]
}

# Record who read and wrote stored documents (Cloud Storage data access logs
# are off by default).
resource "google_project_iam_audit_config" "storage" {
  project = var.project_id
  service = "storage.googleapis.com"

  audit_log_config {
    log_type = "DATA_READ"
  }
  audit_log_config {
    log_type = "DATA_WRITE"
  }
}

# The identity the document services run as (Workload Identity). It can read
# and write objects in the documents bucket only, and sign upload links as
# itself through the IAM signBlob API (pre-signed uploads).
resource "google_service_account" "documents" {
  project      = var.project_id
  account_id   = "cho-documents"
  display_name = "CHO document storage"
  description  = "Kubernetes service accounts of the CHO document services (Workload Identity)."
}

resource "google_storage_bucket_iam_member" "documents_objects" {
  bucket = google_storage_bucket.documents.name
  role   = "roles/storage.objectUser"
  member = "serviceAccount:${google_service_account.documents.email}"
}

resource "google_service_account_iam_member" "documents_sign_self" {
  service_account_id = google_service_account.documents.name
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = "serviceAccount:${google_service_account.documents.email}"
}

resource "google_service_account_iam_member" "documents_workload_identity" {
  for_each = toset(var.document_service_accounts)

  service_account_id = google_service_account.documents.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${var.kubernetes_namespace}/${each.value}]"

  # The workload pool exists once the cluster has Workload Identity.
  depends_on = [google_container_cluster.cho]
}

# ── Keyless GitHub Actions access (Workload Identity Federation) ─────────────

resource "google_iam_workload_identity_pool" "github" {
  project                   = var.project_id
  workload_identity_pool_id = "github-actions"
  display_name              = "GitHub Actions"
  description               = "Keyless deploys from GitHub Actions for the Cloud Health Office demo"

  depends_on = [google_project_service.required]
}

resource "google_iam_workload_identity_pool_provider" "github" {
  project                            = var.project_id
  workload_identity_pool_id          = google_iam_workload_identity_pool.github.workload_identity_pool_id
  workload_identity_pool_provider_id = "github"
  display_name                       = "GitHub OIDC"

  attribute_mapping = {
    "google.subject"       = "assertion.sub"
    "attribute.repository" = "assertion.repository"
    "attribute.ref"        = "assertion.ref"
  }

  # Only tokens minted for this repository are accepted.
  attribute_condition = "assertion.repository == \"${var.github_repo}\""

  oidc {
    issuer_uri = "https://token.actions.githubusercontent.com"
  }
}

resource "google_service_account" "deployer" {
  project      = var.project_id
  account_id   = "cho-demo-deployer"
  display_name = "Cloud Health Office demo deployer (GitHub Actions)"

  depends_on = [google_project_service.required]
}

resource "google_service_account_iam_member" "github_impersonation" {
  service_account_id = google_service_account.deployer.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github.name}/attribute.repository/${var.github_repo}"
}

resource "google_artifact_registry_repository_iam_member" "deployer_push" {
  project    = var.project_id
  location   = google_artifact_registry_repository.cho.location
  repository = google_artifact_registry_repository.cho.name
  role       = "roles/artifactregistry.writer"
  member     = "serviceAccount:${google_service_account.deployer.email}"
}

# The deploy installs the Strimzi operator, which creates cluster-scoped
# Kubernetes resources (CRDs, ClusterRoles, ClusterRoleBindings).
# roles/container.developer cannot create RBAC objects, so the deployer gets
# roles/container.admin. Acceptable for a single-purpose demo project; a
# production setup would install operators separately and grant less.
resource "google_project_iam_member" "deployer_gke" {
  project = var.project_id
  role    = "roles/container.admin"
  member  = "serviceAccount:${google_service_account.deployer.email}"
}

# Nodes pull images with the default compute service account.
resource "google_artifact_registry_repository_iam_member" "nodes_pull" {
  project    = var.project_id
  location   = google_artifact_registry_repository.cho.location
  repository = google_artifact_registry_repository.cho.name
  role       = "roles/artifactregistry.reader"
  member     = "serviceAccount:${data.google_project.this.number}-compute@developer.gserviceaccount.com"
}

# ── Optional budget alert ────────────────────────────────────────────────────

resource "google_billing_budget" "demo" {
  count = var.billing_account_id == "" ? 0 : 1

  billing_account = var.billing_account_id
  display_name    = "Cloud Health Office demo (${var.project_id})"

  budget_filter {
    projects = ["projects/${data.google_project.this.number}"]
  }

  amount {
    specified_amount {
      currency_code = "USD"
      units         = tostring(var.monthly_budget_usd)
    }
  }

  threshold_rules {
    threshold_percent = 0.5
  }
  threshold_rules {
    threshold_percent = 0.9
  }
  threshold_rules {
    threshold_percent = 1.0
  }
}

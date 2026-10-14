# Cloud Health Office demo environment on Google Cloud.
#
# DEMO-GRADE. This provisions a GKE Autopilot cluster, an Artifact Registry
# repository and keyless GitHub Actions access. It is not a HIPAA production
# environment: no Google BAA, CMEK, private cluster or Private Service Connect
# is configured. Use synthetic data only. See docs/deploy/GCP-DEMO.md.

locals {
  services = [
    "artifactregistry.googleapis.com",
    "cloudresourcemanager.googleapis.com",
    "container.googleapis.com",
    "iam.googleapis.com",
    "iamcredentials.googleapis.com",
    "secretmanager.googleapis.com",
    "sts.googleapis.com",
  ]

  labels = {
    app         = "cloudhealthoffice"
    environment = "demo"
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
  description   = "Cloud Health Office service images (demo)"
  format        = "DOCKER"
  labels        = local.labels

  # Keep only recent images so the demo repository doesn't grow without bound.
  cleanup_policies {
    id     = "keep-recent"
    action = "KEEP"
    most_recent_versions {
      keep_count = 5
    }
  }

  depends_on = [google_project_service.required]
}

# ── Cluster ──────────────────────────────────────────────────────────────────

resource "google_container_cluster" "cho" {
  project  = var.project_id
  name     = var.cluster_name
  location = var.region

  enable_autopilot    = true
  deletion_protection = var.deletion_protection

  release_channel {
    channel = "REGULAR"
  }

  resource_labels = local.labels

  depends_on = [google_project_service.required]
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

# Autopilot nodes pull images with the default compute service account.
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

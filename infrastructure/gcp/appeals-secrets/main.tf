# Opt-in: only appeals-service can read its own versioned encryption key.
# Key payloads are deliberately initialized outside Terraform so they never enter state.
terraform {
  required_version = ">= 1.6"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 8.0"
    }
  }
}

variable "project_id" {
  description = "Existing GCP project, with Secret Manager API and GKE Workload Identity enabled."
  type        = string
}

variable "kubernetes_namespace" {
  type    = string
  default = "cloudhealthoffice"
}

provider "google" {
  project = var.project_id
}

resource "google_service_account" "appeals" {
  project      = var.project_id
  account_id   = "cho-appeals"
  display_name = "CHO appeals encryption"
}

resource "google_secret_manager_secret" "appeal_body_key" {
  project   = var.project_id
  secret_id = "appeal-body-encryption-key-v1"
  replication {
    auto {}
  }
  lifecycle {
    prevent_destroy = true
  }
}

resource "google_secret_manager_secret_iam_member" "appeals_key_reader" {
  for_each  = toset(["roles/secretmanager.secretAccessor", "roles/secretmanager.viewer"])
  project   = var.project_id
  secret_id = google_secret_manager_secret.appeal_body_key.secret_id
  role      = each.value
  member    = "serviceAccount:${google_service_account.appeals.email}"
}

resource "google_service_account_iam_member" "appeals_workload_identity" {
  service_account_id = google_service_account.appeals.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${var.kubernetes_namespace}/appeals-service]"
}

output "appeals_secrets_project" {
  value = var.project_id
}

output "appeals_secrets_service_account" {
  value = google_service_account.appeals.email
}

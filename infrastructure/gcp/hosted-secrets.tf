# Secret Manager for the hosted profile (docs/deploy/GCP-HOSTED-HIPAA.md,
# "Deploy").
#
# The passwords of the hosted cluster's own databases are generated once by
# scripts/deploy-gke.sh (HOSTED=true) and kept here, so a rebuilt or restored
# cluster comes back with the same ones. Each secret is replicated in the
# region only and encrypted with its own customer-managed key. Only the
# deployer can read or add versions.
#
# The Kubernetes secrets the services read are copies, encrypted at rest with
# the gke-secrets key (application-layer secrets encryption, hosted.tf).

locals {
  hosted_secret_ids = local.hosted ? toset([
    "cho-mongodb-password",
    "cho-redis-password",
    "cho-postgres-password",
    "cho-pricing-admin-secret",
  ]) : toset([])
}

resource "google_kms_crypto_key" "hosted_secrets" {
  count = local.hosted ? 1 : 0

  name            = "secrets"
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

resource "google_project_service_identity" "secret_manager" {
  provider = google-beta
  count    = local.hosted ? 1 : 0

  project = var.project_id
  service = "secretmanager.googleapis.com"

  depends_on = [google_project_service.required]
}

resource "google_kms_crypto_key_iam_member" "hosted_secrets" {
  count = local.hosted ? 1 : 0

  crypto_key_id = google_kms_crypto_key.hosted_secrets[0].id
  role          = "roles/cloudkms.cryptoKeyEncrypterDecrypter"
  member        = "serviceAccount:${google_project_service_identity.secret_manager[0].email}"
}

resource "google_secret_manager_secret" "hosted" {
  for_each = local.hosted_secret_ids

  project   = var.project_id
  secret_id = each.value
  labels    = local.labels

  replication {
    user_managed {
      replicas {
        location = var.region
        customer_managed_encryption {
          kms_key_name = google_kms_crypto_key.hosted_secrets[0].id
        }
      }
    }
  }

  depends_on = [google_kms_crypto_key_iam_member.hosted_secrets]
}

resource "google_secret_manager_secret_iam_member" "hosted_deployer_read" {
  for_each = google_secret_manager_secret.hosted

  project   = var.project_id
  secret_id = each.value.secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${google_service_account.deployer.email}"
}

resource "google_secret_manager_secret_iam_member" "hosted_deployer_add" {
  for_each = google_secret_manager_secret.hosted

  project   = var.project_id
  secret_id = each.value.secret_id
  role      = "roles/secretmanager.secretVersionAdder"
  member    = "serviceAccount:${google_service_account.deployer.email}"
}

output "hosted_secrets_project" {
  description = "Project of the hosted deploy's Secret Manager secrets (HOSTED_SECRETS_PROJECT). Empty outside the hosted profile."
  value       = local.hosted ? var.project_id : ""
}

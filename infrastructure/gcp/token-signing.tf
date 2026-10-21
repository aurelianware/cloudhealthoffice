# Token signing and service identity for a Production-host deployment on GKE.
#
# Off by default (enable_token_signing_kms = false), so the demo cluster is
# unchanged. When enabled, this creates:
#   - a Cloud KMS key ring and an asymmetric signing key (EC P-256 / ES256)
#     that token-service signs CHO tokens with (TokenSigning:CloudKmsKeyVersion);
#     the private key never leaves Cloud KMS
#   - a Google service account for token-service, bound to its Kubernetes
#     service account through Workload Identity, allowed to sign with that key
#     and read its public key, and nothing else
#
# Services prove their own identity to token-service with projected
# Kubernetes service-account tokens (ServiceTokens:IdentityProvider=Kubernetes),
# which needs no cloud resources: the outputs below give token-service the
# cluster's service-account issuer. See the configuration guide at
# /docs/configure-tenant-security on cloudhealthoffice.com.

variable "enable_token_signing_kms" {
  description = "Create the Cloud KMS token-signing key and token-service's Workload Identity. Off for the demo, which uses the development key."
  type        = bool
  default     = false
}

variable "token_signing_protection_level" {
  description = "Cloud KMS protection level for the token-signing key: SOFTWARE or HSM."
  type        = string
  default     = "HSM"

  validation {
    condition     = contains(["SOFTWARE", "HSM"], var.token_signing_protection_level)
    error_message = "token_signing_protection_level must be SOFTWARE or HSM."
  }
}

variable "token_service_kubernetes_account" {
  description = "token-service's Kubernetes service account (in kubernetes_namespace)."
  type        = string
  default     = "token-service"
}

resource "google_project_service" "kms" {
  count = var.enable_token_signing_kms ? 1 : 0

  project            = var.project_id
  service            = "cloudkms.googleapis.com"
  disable_on_destroy = false
}

resource "google_kms_key_ring" "cho" {
  count = var.enable_token_signing_kms ? 1 : 0

  project  = var.project_id
  name     = "cho"
  location = var.region

  depends_on = [google_project_service.kms]
}

resource "google_kms_crypto_key" "token_signing" {
  count = var.enable_token_signing_kms ? 1 : 0

  name     = "token-signing"
  key_ring = google_kms_key_ring.cho[0].id
  purpose  = "ASYMMETRIC_SIGN"
  labels   = local.labels

  version_template {
    algorithm        = "EC_SIGN_P256_SHA256"
    protection_level = var.token_signing_protection_level
  }
}

resource "google_service_account" "token_service" {
  count = var.enable_token_signing_kms ? 1 : 0

  project      = var.project_id
  account_id   = "cho-token-service"
  display_name = "CHO token-service"
  description  = "token-service's Kubernetes service account (Workload Identity): signs CHO tokens with Cloud KMS."
}

resource "google_kms_crypto_key_iam_member" "token_service_sign" {
  count = var.enable_token_signing_kms ? 1 : 0

  crypto_key_id = google_kms_crypto_key.token_signing[0].id
  role          = "roles/cloudkms.signerVerifier"
  member        = "serviceAccount:${google_service_account.token_service[0].email}"
}

resource "google_kms_crypto_key_iam_member" "token_service_public_key" {
  count = var.enable_token_signing_kms ? 1 : 0

  crypto_key_id = google_kms_crypto_key.token_signing[0].id
  role          = "roles/cloudkms.publicKeyViewer"
  member        = "serviceAccount:${google_service_account.token_service[0].email}"
}

resource "google_service_account_iam_member" "token_service_workload_identity" {
  count = var.enable_token_signing_kms ? 1 : 0

  service_account_id = google_service_account.token_service[0].name
  role               = "roles/iam.workloadIdentityUser"
  member             = "serviceAccount:${var.project_id}.svc.id.goog[${var.kubernetes_namespace}/${var.token_service_kubernetes_account}]"

  depends_on = [google_container_cluster.cho]
}

output "token_signing_key_version" {
  description = "token-service's TokenSigning:CloudKmsKeyVersion (empty unless enable_token_signing_kms)."
  # An asymmetric key's first version is created with the key.
  value = var.enable_token_signing_kms ? "${google_kms_crypto_key.token_signing[0].id}/cryptoKeyVersions/1" : ""
}

output "token_service_google_account" {
  description = "Annotate token-service's Kubernetes service account with iam.gke.io/gcp-service-account set to this."
  value       = var.enable_token_signing_kms ? google_service_account.token_service[0].email : ""
}

output "kubernetes_service_account_issuer" {
  description = "token-service's ServiceTokens:Kubernetes:KubernetesIssuer (the cluster's service-account issuer)."
  value       = "https://container.googleapis.com/v1/projects/${var.project_id}/locations/${google_container_cluster.cho.location}/clusters/${google_container_cluster.cho.name}"
}

output "cluster_name" {
  description = "GitHub variable GKE_CLUSTER."
  value       = google_container_cluster.cho.name
}

output "cluster_location" {
  description = "GitHub variable GKE_LOCATION."
  value       = google_container_cluster.cho.location
}

output "artifact_registry_url" {
  description = "GitHub variable GAR_REPOSITORY (image prefix, e.g. us-west1-docker.pkg.dev/<project>/cho)."
  value       = "${google_artifact_registry_repository.cho.location}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.cho.repository_id}"
}

output "workload_identity_provider" {
  description = "GitHub secret GCP_WORKLOAD_IDENTITY_PROVIDER."
  value       = google_iam_workload_identity_pool_provider.github.name
}

output "deployer_service_account" {
  description = "GitHub secret GCP_DEPLOYER_SERVICE_ACCOUNT."
  value       = google_service_account.deployer.email
}

output "project_id" {
  description = "GitHub variable GCP_PROJECT_ID."
  value       = var.project_id
}

output "documents_bucket" {
  description = "GitHub variable DOCUMENTS_BUCKET (the services' DocumentStore:Gcs:Bucket)."
  value       = google_storage_bucket.documents.name
}

output "documents_service_account" {
  description = "GitHub variable DOCUMENTS_SERVICE_ACCOUNT (the Google identity of the document services)."
  value       = google_service_account.documents.email
}

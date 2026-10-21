variable "project_id" {
  description = "Google Cloud project that hosts the demo environment."
  type        = string
}

variable "region" {
  description = "Region for the Artifact Registry repository, and for the cluster when autopilot is true."
  type        = string
  default     = "us-west1"
}

variable "autopilot" {
  description = "Use a regional GKE Autopilot cluster instead of a single-node Standard cluster. Autopilot needs about 24 vCPU and 500 GB of SSD quota; new billing accounts get 12 and 250."
  type        = bool
  default     = false
}

variable "zone" {
  description = "Zone for the Standard cluster (ignored when autopilot is true)."
  type        = string
  default     = "us-west1-a"
}

variable "node_machine_type" {
  description = "Machine type for the Standard cluster's node pool."
  type        = string
  default     = "e2-standard-8"
}

variable "node_count" {
  description = "Nodes in the Standard cluster's node pool."
  type        = number
  default     = 1
}

variable "node_disk_size_gb" {
  description = "Boot disk size (standard persistent disk) for each node."
  type        = number
  default     = 100
}

variable "cluster_name" {
  description = "Name of the GKE cluster."
  type        = string
  default     = "cho-demo"
}

variable "artifact_repository_id" {
  description = "Artifact Registry Docker repository that holds the service images."
  type        = string
  default     = "cho"
}

variable "github_repo" {
  description = "GitHub repository (owner/name) allowed to deploy through Workload Identity Federation."
  type        = string
  default     = "aurelianware/cloudhealthoffice"
}

variable "deletion_protection" {
  description = "Protect the cluster from terraform destroy. Off by default so the demo can be torn down."
  type        = bool
  default     = false
}

variable "billing_account_id" {
  description = "Optional billing account ID (XXXXXX-XXXXXX-XXXXXX). When set, a monthly budget alert is created."
  type        = string
  default     = ""
}

variable "monthly_budget_usd" {
  description = "Monthly budget for the alert, in US dollars. Used only when billing_account_id is set."
  type        = number
  default     = 300
}

variable "kubernetes_namespace" {
  description = "Namespace the CHO services run in (scripts/deploy-gke.sh)."
  type        = string
  default     = "cloudhealthoffice"
}

variable "document_service_accounts" {
  description = "Kubernetes service accounts allowed to act as the document storage identity."
  type        = list(string)
  default     = ["document-service"]
}

variable "documents_bucket_name" {
  description = "Name of the documents bucket. Empty uses <project_id>-cho-documents (bucket names are global)."
  type        = string
  default     = ""
}

variable "documents_force_destroy" {
  description = "Let terraform destroy delete the documents bucket with objects in it. Off by default, so tearing down the demo never silently deletes stored documents."
  type        = bool
  default     = false
}

variable "documents_noncurrent_days" {
  description = "Days a replaced or deleted document version is kept before removal."
  type        = number
  default     = 30
}

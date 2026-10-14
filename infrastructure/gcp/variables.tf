variable "project_id" {
  description = "Google Cloud project that hosts the demo environment."
  type        = string
}

variable "region" {
  description = "Region for the GKE Autopilot cluster and the Artifact Registry repository."
  type        = string
  default     = "us-west1"
}

variable "cluster_name" {
  description = "Name of the GKE Autopilot cluster."
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

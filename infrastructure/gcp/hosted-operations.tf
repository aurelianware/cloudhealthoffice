# Hosted profile, operations: daily backups and security alerts
# (docs/deploy/GCP-HOSTED-HIPAA.md, workstreams 6 and 7).
#
# Only with hosted = true. This creates:
#   - a Backup for GKE plan that backs up every namespace, its secrets and its
#     volume data (MongoDB, Kafka, Redis, ClamAV signatures) every day,
#     encrypted with its own customer-managed key, kept hosted_backup_retain_days
#     and locked against deletion for the first hosted_backup_delete_lock_days
#   - alert policies on the audit logs, emailed to hosted_alert_emails: IAM
#     policy changes, encryption key changes, changes to log routing or the
#     audit log bucket, organization policy changes, and Backup for GKE
#     failures
#
# The restore drill is manual and documented; Backup for GKE restores into a
# new namespace or cluster without touching the source.

variable "hosted_backup_retain_days" {
  description = "Days each daily cluster backup is kept."
  type        = number
  default     = 35
}

variable "hosted_backup_delete_lock_days" {
  description = "Days after creation during which nobody can delete a backup, including project owners."
  type        = number
  default     = 7
}

variable "hosted_backup_cron" {
  description = "When the daily backup runs (cron, UTC). The default is 07:00 UTC, early morning in US time zones."
  type        = string
  default     = "0 7 * * *"
}

variable "hosted_alert_emails" {
  description = "Email addresses that receive the hosted environment's security alerts. Empty creates the alert policies with no one to notify."
  type        = list(string)
  default     = []
}

resource "google_project_service" "hosted_operations" {
  for_each = local.hosted ? toset([
    "gkebackup.googleapis.com",
    "monitoring.googleapis.com",
  ]) : toset([])

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}

# ── Daily backups ────────────────────────────────────────────────────────────

resource "google_kms_crypto_key" "hosted_backups" {
  count = local.hosted ? 1 : 0

  name            = "backups"
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

resource "google_project_service_identity" "gke_backup" {
  provider = google-beta
  count    = local.hosted ? 1 : 0

  project = var.project_id
  service = "gkebackup.googleapis.com"

  depends_on = [google_project_service.hosted_operations]
}

# The Backup for GKE service agent encrypts backups with the backup key, and
# reads and restores volumes that the disk key encrypts.
resource "google_kms_crypto_key_iam_member" "hosted_backups" {
  for_each = local.hosted ? {
    backups = google_kms_crypto_key.hosted_backups[0].id
    disks   = google_kms_crypto_key.hosted_disks[0].id
  } : {}

  crypto_key_id = each.value
  role          = "roles/cloudkms.cryptoKeyEncrypterDecrypter"
  member        = "serviceAccount:${google_project_service_identity.gke_backup[0].email}"
}

resource "google_gke_backup_backup_plan" "hosted" {
  count = local.hosted ? 1 : 0

  project  = var.project_id
  name     = "cho-daily"
  cluster  = google_container_cluster.cho.id
  location = var.region
  labels   = local.labels

  retention_policy {
    backup_retain_days      = var.hosted_backup_retain_days
    backup_delete_lock_days = var.hosted_backup_delete_lock_days
  }

  backup_schedule {
    cron_schedule = var.hosted_backup_cron
  }

  backup_config {
    all_namespaces      = true
    include_secrets     = true
    include_volume_data = true

    encryption_key {
      gcp_kms_encryption_key = google_kms_crypto_key.hosted_backups[0].id
    }
  }

  depends_on = [google_kms_crypto_key_iam_member.hosted_backups]
}

# ── Security alerts ──────────────────────────────────────────────────────────

resource "google_monitoring_notification_channel" "hosted_email" {
  for_each = local.hosted ? toset(var.hosted_alert_emails) : toset([])

  project      = var.project_id
  display_name = "CHO hosted security: ${each.value}"
  type         = "email"

  labels = {
    email_address = each.value
  }

  depends_on = [google_project_service.hosted_operations]
}

locals {
  # Each alert fires on one matching audit log entry, at most once every five
  # minutes per policy.
  hosted_alerts = local.hosted ? {
    iam-policy-changed = {
      name   = "IAM policy changed"
      filter = "logName:\"cloudaudit.googleapis.com%2Factivity\" AND protoPayload.methodName=\"SetIamPolicy\""
      doc    = "Someone changed who can do what in the hosted project. Check the principal and the binding in the log entry, and that the change was planned."
    }
    encryption-key-changed = {
      name   = "Encryption key disabled, destroyed or changed"
      filter = "logName:\"cloudaudit.googleapis.com%2Factivity\" AND protoPayload.serviceName=\"cloudkms.googleapis.com\" AND protoPayload.methodName=~\"(DestroyCryptoKeyVersion|DisableCryptoKeyVersion|UpdateCryptoKeyVersion|UpdateCryptoKeyPrimaryVersion|UpdateCryptoKey)$\""
      doc    = "A customer-managed key that encrypts member data was changed. A disabled or destroyed key makes the data it protects unreadable."
    }
    audit-logging-changed = {
      name   = "Audit log routing or retention changed"
      filter = "logName:\"cloudaudit.googleapis.com%2Factivity\" AND protoPayload.serviceName=\"logging.googleapis.com\" AND protoPayload.methodName=~\"(DeleteSink|UpdateSink|DeleteBucket|UpdateBucket|DeleteLog|UpdateCmekSettings)$\""
      doc    = "The audit log sink, the six-year audit bucket or a log was changed or deleted. Audit logs are HIPAA records."
    }
    org-policy-changed = {
      name   = "Organization policy changed"
      filter = "logName:\"cloudaudit.googleapis.com%2Factivity\" AND protoPayload.serviceName=\"orgpolicy.googleapis.com\" AND protoPayload.methodName=~\"(CreatePolicy|UpdatePolicy|DeletePolicy)$\""
      doc    = "A guardrail on the hosted project (US-only locations, no service account keys, no external addresses) was changed."
    }
    backup-failed = {
      name   = "Daily cluster backup failed"
      filter = "resource.type=\"gkebackup.googleapis.com/BackupPlan\" AND logName:\"gkebackup.googleapis.com%2Fbackup_change\" AND jsonPayload.changeType=\"UPDATE\" AND jsonPayload.inputBackup.state=\"FAILED\""
      doc    = "The daily Backup for GKE run failed. Check the backup plan in the console and run a backup by hand once the cause is fixed."
    }
  } : {}
}

resource "google_monitoring_alert_policy" "hosted" {
  for_each = local.hosted_alerts

  project      = var.project_id
  display_name = "CHO hosted: ${each.value.name}"
  combiner     = "OR"
  severity     = "CRITICAL"

  conditions {
    display_name = each.value.name

    condition_matched_log {
      filter = each.value.filter
    }
  }

  alert_strategy {
    notification_rate_limit {
      period = "300s"
    }
    auto_close = "604800s"
  }

  documentation {
    content   = each.value.doc
    mime_type = "text/markdown"
  }

  notification_channels = [for c in google_monitoring_notification_channel.hosted_email : c.id]

  depends_on = [google_project_service.hosted_operations]
}

# Hosted profile, ingress: the public front door of the hosted HIPAA
# environment (docs/deploy/GCP-HOSTED-HIPAA.md, workstream 3).
#
# Only with hosted = true. This creates the Google Cloud side of a GKE
# Ingress (deploy/gcp/hosted/ingress.yaml applies the Kubernetes side):
#   - a global static IP for the external Application Load Balancer
#   - an SSL policy: TLS 1.2 or later, MODERN ciphers
#   - a Cloud Armor policy on every backend: a per-IP rate limit that bans
#     abusive clients for ten minutes, Google's preconfigured WAF rules
#     (SQL injection, XSS, local file inclusion, remote code execution,
#     scanners; in preview until hosted_waf_enforce is set), and Adaptive
#     Protection against layer 7 DDoS
#   - who may pass Identity-Aware Proxy to the portal (hosted_portal_members).
#     IAP fronts only the portal: the FHIR API and the SMART authorization
#     server are public on purpose, protected by SMART tokens, because patient
#     apps and other payers call them from the internet
#
# The IAP OAuth client is made by hand in the console (Google retired the
# API that created it); see the doc.

variable "hosted_portal_members" {
  description = "Who may pass IAP to the portal, as IAM members: user:pat@plan.org, group:claims@plan.org, domain:plan.org. Each also needs an Active user in their CHO tenant."
  type        = list(string)
  default     = []
}

variable "hosted_waf_enforce" {
  description = "Enforce the preconfigured WAF rules. Off: they run in preview and only log matches, so false positives on FHIR queries can be tuned first."
  type        = bool
  default     = false
}

variable "hosted_rate_limit_per_minute" {
  description = "Requests per minute one client IP may send before it is banned for ten minutes."
  type        = number
  default     = 1200
}

locals {
  # Google's preconfigured WAF rule sets (ModSecurity CRS 3.3), at the least
  # false-positive-prone sensitivity.
  hosted_waf_rules = {
    2000 = "sqli-v33-stable"
    2001 = "xss-v33-stable"
    2002 = "lfi-v33-stable"
    2003 = "rce-v33-stable"
    2004 = "scannerdetection-v33-stable"
  }
}

resource "google_project_service" "hosted_ingress" {
  for_each = local.hosted ? toset(["iap.googleapis.com"]) : toset([])

  project            = var.project_id
  service            = each.value
  disable_on_destroy = false
}

resource "google_compute_global_address" "hosted_ingress" {
  count = local.hosted ? 1 : 0

  project = var.project_id
  name    = "cho-hosted-ingress"

  depends_on = [google_project_service.hosted]
}

resource "google_compute_ssl_policy" "hosted" {
  count = local.hosted ? 1 : 0

  project         = var.project_id
  name            = "cho-hosted-tls"
  profile         = "MODERN"
  min_tls_version = "TLS_1_2"

  depends_on = [google_project_service.hosted]
}

resource "google_compute_security_policy" "hosted" {
  count = local.hosted ? 1 : 0

  project     = var.project_id
  name        = "cho-hosted-edge"
  description = "Rate limit, WAF and DDoS protection for the hosted CHO load balancer."

  adaptive_protection_config {
    layer_7_ddos_defense_config {
      enable = true
    }
  }

  rule {
    action      = "rate_based_ban"
    priority    = 1000
    description = "Ban a client IP for ten minutes when it exceeds the per-minute limit."

    match {
      versioned_expr = "SRC_IPS_V1"
      config {
        src_ip_ranges = ["*"]
      }
    }

    rate_limit_options {
      conform_action   = "allow"
      exceed_action    = "deny(429)"
      enforce_on_key   = "IP"
      ban_duration_sec = 600

      rate_limit_threshold {
        count        = var.hosted_rate_limit_per_minute
        interval_sec = 60
      }
    }
  }

  dynamic "rule" {
    for_each = local.hosted_waf_rules
    content {
      action      = "deny(403)"
      priority    = rule.key
      preview     = !var.hosted_waf_enforce
      description = "Preconfigured WAF: ${rule.value}"

      match {
        expr {
          expression = "evaluatePreconfiguredWaf('${rule.value}', {'sensitivity': 1})"
        }
      }
    }
  }

  rule {
    action      = "allow"
    priority    = 2147483647
    description = "Default: allow."

    match {
      versioned_expr = "SRC_IPS_V1"
      config {
        src_ip_ranges = ["*"]
      }
    }
  }

  depends_on = [google_project_service.hosted]
}

# IAP lets these people through to the portal; CHO still requires an Active
# user with the same email in their tenant before issuing a token.
resource "google_iap_web_iam_member" "hosted_portal" {
  for_each = local.hosted ? toset(var.hosted_portal_members) : toset([])

  project = var.project_id
  role    = "roles/iap.httpsResourceAccessor"
  member  = each.value

  depends_on = [google_project_service.hosted_ingress]
}

output "hosted_ingress_address_name" {
  description = "Global static IP name for the hosted Ingress (kubernetes.io/ingress.global-static-ip-name). Empty outside the hosted profile."
  value       = local.hosted ? google_compute_global_address.hosted_ingress[0].name : ""
}

output "hosted_ingress_ip" {
  description = "The hosted load balancer's IP: point the portal, FHIR and SMART host names' DNS A records here (DNS only, not proxied)."
  value       = local.hosted ? google_compute_global_address.hosted_ingress[0].address : ""
}

output "hosted_ssl_policy" {
  description = "SSL policy name for the hosted Ingress's FrontendConfig."
  value       = local.hosted ? google_compute_ssl_policy.hosted[0].name : ""
}

output "hosted_security_policy" {
  description = "Cloud Armor policy name for the hosted BackendConfigs."
  value       = local.hosted ? google_compute_security_policy.hosted[0].name : ""
}

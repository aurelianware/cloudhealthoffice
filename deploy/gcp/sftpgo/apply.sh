#!/usr/bin/env bash
#
# Applies SFTPGo for the hosted CPCDS drop (docs/integration/CPCDS-SFTP-DROP-GCP.md).
#
#   SFTP_SOURCE_RANGES="203.0.113.10/32,198.51.100.0/28" ./deploy/gcp/sftpgo/apply.sh
#
# Reads the Terraform outputs of infrastructure/gcp (enable_cpcds_drops = true)
# and the current kubectl context. Creates the admin secret once, with a
# generated password that is written only to the cluster: tenant-service
# reads it from there, nobody else needs it.
#
# Environment:
#   SFTP_SOURCE_RANGES  comma-separated CIDRs allowed to reach port 22 (required:
#                       the plans' egress addresses; never 0.0.0.0/0 by default)
#   SFTPGO_IMAGE        default ghcr.io/drakkan/sftpgo:v2.6 (pin a digest for production)
#   STORAGE_CLASS       default cho-pd-standard (scripts/deploy-gke.sh creates it)
#   TF_DIR              default infrastructure/gcp

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
TF_DIR="${TF_DIR:-$REPO_ROOT/infrastructure/gcp}"
MANIFEST="$REPO_ROOT/deploy/gcp/sftpgo/sftpgo.yaml"

if [[ -z "${SFTP_SOURCE_RANGES:-}" ]]; then
  echo "Set SFTP_SOURCE_RANGES to the plans' source CIDRs (comma-separated)." >&2
  exit 2
fi

output() { terraform -chdir="$TF_DIR" output -raw "$1"; }

SFTPGO_GOOGLE_ACCOUNT="$(output cpcds_sftpgo_service_account)"
SFTP_ADDRESS="$(output cpcds_sftp_address)"
if [[ -z "$SFTPGO_GOOGLE_ACCOUNT" || -z "$SFTP_ADDRESS" ]]; then
  echo "The CPCDS drop is not provisioned: set enable_cpcds_drops = true in infrastructure/gcp and apply." >&2
  exit 2
fi

# YAML flow sequence of quoted CIDRs, each checked to look like one.
RANGES="["
IFS=',' read -ra CIDRS <<<"$SFTP_SOURCE_RANGES"
for cidr in "${CIDRS[@]}"; do
  cidr="$(echo "$cidr" | xargs)"
  if [[ ! "$cidr" =~ ^[0-9a-fA-F:.]+/[0-9]{1,3}$ ]]; then
    echo "Not a CIDR: '$cidr'" >&2
    exit 2
  fi
  RANGES+="\"$cidr\","
done
RANGES="${RANGES%,}]"

export SFTPGO_GOOGLE_ACCOUNT SFTP_ADDRESS
export SFTP_SOURCE_RANGES_YAML="$RANGES"
export SFTPGO_IMAGE="${SFTPGO_IMAGE:-ghcr.io/drakkan/sftpgo:v2.6}"
export STORAGE_CLASS="${STORAGE_CLASS:-cho-pd-standard}"

# The admin credential, before SFTPGo first starts (it creates the admin from
# it): generated once, never printed. tenant-service gets a copy in its own
# namespace to call the REST API.
kubectl create namespace cho-sftp --dry-run=client -o yaml | kubectl apply -f - >/dev/null
if ! kubectl -n cho-sftp get secret sftpgo-admin >/dev/null 2>&1; then
  PASSWORD="$(openssl rand -base64 36 | tr -d '\n/+=' | cut -c1-40)"
  for ns in cho-sftp cloudhealthoffice; do
    kubectl -n "$ns" create secret generic sftpgo-admin \
      --from-literal=username=cho-tenant-service \
      --from-literal=password="$PASSWORD" \
      --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  done
  unset PASSWORD
fi

kubectl apply -f - <<<"$(sed \
  -e "s|\${SFTPGO_GOOGLE_ACCOUNT}|$SFTPGO_GOOGLE_ACCOUNT|g" \
  -e "s|\${SFTP_ADDRESS}|$SFTP_ADDRESS|g" \
  -e "s|\${SFTP_SOURCE_RANGES}|$SFTP_SOURCE_RANGES_YAML|g" \
  -e "s|\${SFTPGO_IMAGE}|$SFTPGO_IMAGE|g" \
  -e "s|\${STORAGE_CLASS}|$STORAGE_CLASS|g" \
  "$MANIFEST")"

kubectl -n cho-sftp rollout status deployment/sftpgo --timeout=180s
echo "SFTPGo is serving sftp://$SFTP_ADDRESS:22 for $SFTP_SOURCE_RANGES"

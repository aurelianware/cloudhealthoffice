#!/usr/bin/env bash
# apply-ingress.sh — apply the hosted HIPAA environment's load balancer
# (deploy/gcp/hosted/ingress.yaml) to the current kubectl context.
#
# Hosted clusters only (infrastructure/gcp with hosted = true); refuses a
# context whose cluster name contains "demo". See docs/deploy/GCP-HOSTED-HIPAA.md.
#
# Required environment:
#   PORTAL_HOST  FHIR_HOST  AUTH_HOST   the three public host names
#   ADDRESS_NAME     terraform output hosted_ingress_address_name
#   SSL_POLICY       terraform output hosted_ssl_policy
#   SECURITY_POLICY  terraform output hosted_security_policy
# Optional (first run, or to rotate the IAP OAuth client):
#   IAP_CLIENT_ID  IAP_CLIENT_SECRET   the OAuth client made in the console;
#                  read from the environment, never from arguments

set -Eeuo pipefail
cd "$(git rev-parse --show-toplevel)"

NAMESPACE="cloudhealthoffice"
TEMPLATE="deploy/gcp/hosted/ingress.yaml"
VARS=(PORTAL_HOST FHIR_HOST AUTH_HOST ADDRESS_NAME SSL_POLICY SECURITY_POLICY)

err() { echo "  ✗ $*" >&2; exit 1; }
ok()  { echo "  ✓ $*"; }

for tool in kubectl python3; do command -v "$tool" >/dev/null || err "$tool is required"; done
for v in "${VARS[@]}"; do [[ -n "${!v:-}" ]] || err "$v is required"; done
host_pattern='^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$'
for v in PORTAL_HOST FHIR_HOST AUTH_HOST; do
  [[ "${!v}" =~ $host_pattern ]] || err "$v must be a lower-case host name (got '${!v}')"
done
name_pattern='^[a-z]([-a-z0-9]*[a-z0-9])?$'
for v in ADDRESS_NAME SSL_POLICY SECURITY_POLICY; do
  [[ "${!v}" =~ $name_pattern ]] || err "$v must be a Google Cloud resource name (got '${!v}')"
done

context="$(kubectl config current-context)"
[[ "$context" != *demo* ]] || err "kubectl context '$context' looks like the demo cluster; the hosted ingress never goes there"
ok "context $context"

# The IAP OAuth client: created or replaced only when both values are given.
if [[ -n "${IAP_CLIENT_ID:-}" || -n "${IAP_CLIENT_SECRET:-}" ]]; then
  [[ -n "${IAP_CLIENT_ID:-}" && -n "${IAP_CLIENT_SECRET:-}" ]] || err "IAP_CLIENT_ID and IAP_CLIENT_SECRET go together"
  IAP_CLIENT_ID="$IAP_CLIENT_ID" IAP_CLIENT_SECRET="$IAP_CLIENT_SECRET" NS="$NAMESPACE" python3 -c '
import base64, json, os
b64 = lambda v: base64.b64encode(v.encode()).decode()
print(json.dumps({"apiVersion": "v1", "kind": "Secret", "type": "Opaque",
  "metadata": {"name": "iap-oauth", "namespace": os.environ["NS"]},
  "data": {"client_id": b64(os.environ["IAP_CLIENT_ID"]), "client_secret": b64(os.environ["IAP_CLIENT_SECRET"])}}))
' | kubectl apply -f - >/dev/null
  ok "iap-oauth secret"
fi
kubectl get secret iap-oauth -n "$NAMESPACE" >/dev/null 2>&1 \
  || err "Secret iap-oauth is missing: set IAP_CLIENT_ID and IAP_CLIENT_SECRET once (see docs/deploy/GCP-HOSTED-HIPAA.md, Ingress)"

# Fill in exactly the listed values; anything else left as ${...} is an error.
python3 - "$TEMPLATE" "${VARS[@]}" <<'PY' | kubectl apply -f - >/dev/null
import os, re, sys
template, names = open(sys.argv[1]).read(), sys.argv[2:]
for name in names:
    template = template.replace("${" + name + "}", os.environ[name])
left = sorted(set(re.findall(r"\$\{([A-Z_]+)\}", template)))
if left:
    sys.exit("unfilled placeholders: " + ", ".join(left))
print(template)
PY
ok "ManagedCertificate, FrontendConfig, BackendConfigs, Ingress"

# Each backend uses its BackendConfig (IAP for the portal only) through
# container-native load balancing.
kubectl annotate service portal -n "$NAMESPACE" --overwrite \
  'cloud.google.com/neg={"ingress": true}' 'cloud.google.com/backend-config={"default": "cho-portal"}' >/dev/null
for svc in fhir-service smart-auth-service; do
  kubectl annotate service "$svc" -n "$NAMESPACE" --overwrite \
    'cloud.google.com/neg={"ingress": true}' 'cloud.google.com/backend-config={"default": "cho-public-api"}' >/dev/null
done
ok "service annotations (portal behind IAP; fhir-service and smart-auth-service public)"

echo
echo "Next: point ${PORTAL_HOST}, ${FHIR_HOST} and ${AUTH_HOST} at the hosted_ingress_ip (DNS only)."
echo "The certificate turns Active once DNS resolves: kubectl describe managedcertificate cho-hosted -n $NAMESPACE"
echo "Then read the portal backend service's audience for Authentication:CloudflareAccess:Audiences:"
echo "  gcloud compute backend-services list --filter='name~portal' --format='value(name,id)'"

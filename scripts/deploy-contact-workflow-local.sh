#!/usr/bin/env bash
# Synthetic-only contact workflow on Docker Desktop Kubernetes. Bash 3.2 compatible.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
context=docker-desktop
namespace=cho-contact-local
skip_build=false
case "${1:-}" in
  '') ;;
  --skip-build) skip_build=true ;;
  *) echo "Usage: bash scripts/deploy-contact-workflow-local.sh [--skip-build]" >&2; exit 1 ;;
esac
for executable in docker kubectl openssl; do
  command -v "$executable" >/dev/null || { echo "$executable is required" >&2; exit 1; }
done
# Never fall through to a remote context, even when it is the current context.
kubectl --context "$context" cluster-info >/dev/null
docker info >/dev/null
nodes=$(kubectl --context "$context" get nodes -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{end}')
[ -n "$nodes" ] || { echo 'Docker Desktop has no Kubernetes nodes.' >&2; exit 1; }
# Do not adopt a namespace that another deployment owns.
if kubectl --context "$context" get namespace "$namespace" >/dev/null 2>&1; then
  owner=$(kubectl --context "$context" get namespace "$namespace" -o jsonpath='{.metadata.labels.app\.kubernetes\.io/part-of}')
  [ "$owner" = cho-contact-local ] || { echo "Refusing to adopt existing namespace $namespace" >&2; exit 1; }
fi
services='claims-service tenant-service contact-service document-service appeals-service authorization-service work-management-service portal'
for service in $services; do
  image="cho-contact-local/$service:local"
  if [ "$skip_build" = false ]; then
    if [ "$service" = portal ]; then
      dockerfile=src/portal/CloudHealthOffice.Portal/Dockerfile
    else
      dockerfile="src/services/$service/Dockerfile"
    fi
    echo "Building $image"
    docker build -t "$image" -f "$dockerfile" .
  fi
  docker image inspect "$image" >/dev/null
  # Docker Desktop's kind provisioner has a containerd store inside each node.
  # Import there when the node container is visible. Kubeadm uses Desktop's store.
  for node in $nodes; do
    if docker inspect "$node" >/dev/null 2>&1; then
      echo "Loading $image into $node"
      docker save "$image" | docker exec -i "$node" ctr --namespace k8s.io images import - >/dev/null
    elif [ "$node" != docker-desktop ]; then
      echo "Cannot find node container '$node'. Enable Docker Desktop's Show system containers option and retry." >&2
      exit 1
    fi
  done
done
kubectl --context "$context" apply -f deploy/local/contact-workflow/namespace.yaml
# Generate once, retain across reruns and Vault restarts. Never print key material.
if ! kubectl --context "$context" -n "$namespace" get secret local-appeal-key >/dev/null 2>&1; then
  key_file=$(mktemp)
  chmod 600 "$key_file"
  trap 'rm -f "$key_file"' EXIT
  openssl rand -base64 32 | tr -d '\n' > "$key_file"
  kubectl --context "$context" -n "$namespace" create secret generic local-appeal-key --from-file="key=$key_file"
  rm -f "$key_file"
  trap - EXIT
fi
kubectl --context "$context" apply -k deploy/local/contact-workflow
kubectl --context "$context" -n "$namespace" rollout status deployment/local-vault --timeout=180s
kubectl --context "$context" -n "$namespace" rollout status deployment/mongo --timeout=180s
kubectl --context "$context" -n "$namespace" rollout status deployment/redis --timeout=120s
kubectl --context "$context" -n "$namespace" exec -i deployment/mongo -- mongosh --quiet < scripts/setup/seed-contact-workflow-local.js
for dependency in gcs-emulator clamav; do
  kubectl --context "$context" -n "$namespace" rollout status "deployment/$dependency" --timeout=1200s
done
# Local tags are rebuilt/imported above; restart only this isolated workload.
kubectl --context "$context" -n "$namespace" rollout restart deployment/claims-service deployment/tenant-service deployment/contact-service deployment/document-service deployment/appeals-service deployment/authorization-service deployment/work-management-service deployment/portal
for service in $services; do
  kubectl --context "$context" -n "$namespace" rollout status "deployment/$service" --timeout=240s
done
printf '\nContact workflow is running with synthetic development authentication.\n'
printf 'Document bytes persist in the local GCS emulator; catalog metadata persists in MongoDB.\n'
printf 'ClamAV scans new uploads; only clean files can be opened. Old in-memory uploads are not migrated.\n\n'
printf 'Open the portal in another terminal:\n'
printf 'kubectl --context docker-desktop -n cho-contact-local port-forward svc/portal 5090:8080\n'
printf 'Then visit http://localhost:5090/service\n'

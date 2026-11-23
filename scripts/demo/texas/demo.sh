#!/usr/bin/env bash
# demo.sh — Turn the Texas Medicaid demo on and off, and load it.
#
# The demo runs on the existing GKE demo cluster (infrastructure/gcp). Off, the
# cluster has no nodes: no VM is billed, the volumes (MongoDB, Kafka, Postgres)
# keep the data, and Google's free tier credits the zonal cluster's management
# fee. On, one e2-standard-8 node runs everything. Runbook, costs and the
# click-through script: docs/deploy/TEXAS-MEDICAID-DEMO.md.
#
#   ./scripts/demo/texas/demo.sh up        # add the node, wait for the services, apply the Texas settings
#   ./scripts/demo/texas/demo.sh connect   # port-forwards for the portal and the APIs (leave running)
#   ./scripts/demo/texas/demo.sh seed      # plans, providers, today's file drop, Texas PA clocks
#   ./scripts/demo/texas/demo.sh clocks    # today's Texas PA requests only, fresh (before a showing)
#   ./scripts/demo/texas/demo.sh status    # nodes, pods and what it is costing
#   ./scripts/demo/texas/demo.sh down      # remove the node: back to near-zero cost
#
# Other commands (they use kubectl's current context; "up" sets it):
#   overlay        apply only the Texas settings (up does this): the CPCDS drop
#                  folder and shadow adjudication on claims-service, the Texas
#                  decision clock on authorization-service, service categories
#                  on benefit-plan-service, the CRD MRI documentation rule on
#                  fhir-service. Idempotent; rerun after any deploy.
#   drop [DIR]     copy one day's drop into the tenant's inbound folder and load
#                  it (DIR defaults to a freshly generated drop for today)
#
# Settings (environment), defaults match infrastructure/gcp:
#   GCP_PROJECT_ID   the demo project (required for up, down and status)
#   GKE_CLUSTER      cho-demo
#   GKE_LOCATION     us-west1-a
#   GKE_NODE_POOL    demo
#   GKE_NODE_COUNT   nodes for "up" (default 1)
#   NAMESPACE        cloudhealthoffice
#   TENANT_ID        demo
#
# Synthetic data only. The cluster trusts the public development signing key,
# so it is reached by port-forward (or behind Cloudflare Access), never openly.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
HERE="$ROOT/scripts/demo/texas"
CLUSTER="${GKE_CLUSTER:-cho-demo}"
LOCATION="${GKE_LOCATION:-us-west1-a}"
POOL="${GKE_NODE_POOL:-demo}"
NODES="${GKE_NODE_COUNT:-1}"
NAMESPACE="${NAMESPACE:-cloudhealthoffice}"
TENANT="${TENANT_ID:-demo}"
# e2-standard-8 on-demand in us-west1, plus its 100 GB standard boot disk.
HOURLY_PER_NODE="0.28"

say()  { printf '\033[1;36m▶ %s\033[0m\n' "$*"; }
die()  { printf 'error: %s\n' "$*" >&2; exit 1; }

need() { for c in "$@"; do command -v "$c" >/dev/null || die "$c is required"; done; }

project() { [[ -n "${GCP_PROJECT_ID:-}" ]] || die "set GCP_PROJECT_ID to the demo project"; printf '%s' "$GCP_PROJECT_ID"; }

credentials() {
  need gcloud kubectl
  gcloud container clusters get-credentials "$CLUSTER" --location "$LOCATION" --project "$(project)" >/dev/null
}

cmd_status() {
  credentials
  local ready total nodes
  nodes="$(kubectl get nodes --no-headers 2>/dev/null | wc -l | tr -d ' ')"
  say "Cluster $CLUSTER ($LOCATION): $nodes node(s) running"
  if [[ "$nodes" == 0 ]]; then
    echo "  Off. Idle cost is the volumes and stored images, a few dollars a month."
    echo "  Turn it on with: $0 up"
    return 0
  fi
  total="$(kubectl get pods -n "$NAMESPACE" --no-headers 2>/dev/null | wc -l | tr -d ' ')"
  ready="$(kubectl get pods -n "$NAMESPACE" --no-headers 2>/dev/null | awk '{split($2,a,"/"); if (a[1]==a[2] && $3=="Running") n++} END {print n+0}')"
  echo "  $ready of $total pods ready in $NAMESPACE"
  echo "  Running cost: about \$$(awk -v n="$nodes" -v h="$HOURLY_PER_NODE" 'BEGIN {printf "%.2f", n*h}') an hour. Turn it off with: $0 down"
}

cmd_up() {
  credentials
  say "Adding $NODES node(s) to $POOL (about 2 minutes)"
  gcloud container clusters resize "$CLUSTER" --node-pool "$POOL" --num-nodes "$NODES" \
    --location "$LOCATION" --project "$(project)" --quiet
  kubectl wait --for=condition=Ready nodes --all --timeout=10m
  # Autopilot-style pauses scale workloads to zero instead; bring any back.
  local zero
  zero="$(kubectl get deploy -n "$NAMESPACE" -o jsonpath='{range .items[?(@.spec.replicas==0)]}{.metadata.name}{"\n"}{end}')"
  if [[ -n "$zero" ]]; then
    say "Scaling $(wc -l <<<"$zero" | tr -d ' ') stopped deployments back to 1"
    xargs -r -I{} kubectl scale deploy/{} -n "$NAMESPACE" --replicas=1 <<<"$zero"
  fi
  zero="$(kubectl get statefulset -n "$NAMESPACE" -o jsonpath='{range .items[?(@.spec.replicas==0)]}{.metadata.name}{"\n"}{end}')"
  [[ -z "$zero" ]] || xargs -r -I{} kubectl scale statefulset/{} -n "$NAMESPACE" --replicas=1 <<<"$zero"
  say "Waiting for MongoDB and Kafka"
  kubectl rollout status statefulset/mongodb -n "$NAMESPACE" --timeout=15m || true
  kubectl wait kafka/cloudhealthoffice-kafka -n kafka --for=condition=Ready --timeout=15m 2>/dev/null || true
  cmd_overlay
  say "Waiting for the services (first start after a pause takes 5 to 10 minutes)"
  local d failed=0
  for d in portal claims-service authorization-service benefit-plan-service provider-service \
           enrollment-import-service fhir-service member-service coverage-service; do
    kubectl rollout status "deploy/$d" -n "$NAMESPACE" --timeout=15m >/dev/null || { echo "  $d is not ready"; failed=1; }
  done
  cmd_status
  [[ $failed == 0 ]] || echo "Some services are not ready yet: kubectl get pods -n $NAMESPACE"
  echo "Next: '$0 connect' in its own terminal, then '$0 seed'."
}

cmd_down() {
  credentials
  say "Removing every node from $POOL. Data stays on the volumes."
  # Draining can outlast gcloud's own wait (the nightly run of Nov 23 gave up
  # while GKE was still removing the node), so start the resize and wait on
  # the operation ourselves, for up to 50 minutes.
  local op status i
  op="$(gcloud container clusters resize "$CLUSTER" --node-pool "$POOL" --num-nodes 0 \
    --location "$LOCATION" --project "$(project)" --quiet --async --format='value(name)')"
  for i in $(seq 1 100); do
    status="$(gcloud container operations describe "$op" --location "$LOCATION" \
      --project "$(project)" --format='value(status)')"
    [[ "$status" == DONE ]] && break
    sleep 30
  done
  if [[ "$status" != DONE ]]; then
    echo "GKE is still removing the node (operation $op). Run '$0 status' later to confirm it is off." >&2
    return 1
  fi
  echo "Off. The VM is no longer billed; '$0 up' brings it back with the same data."
}

# strategic-merge patches: containers, env, volumes and mounts merge by name,
# so rerunning changes nothing and causes no restart. Each env entry clears
# valueFrom: where the shared manifests take a setting from a ConfigMap,
# setting "value" alone would leave both, which the API refuses.
cmd_overlay() {
  need kubectl
  say "Applying the Texas Medicaid demo settings"
  local holidays="" i=0 d
  for d in $(python3 -c "import sys; sys.path.insert(0, '$HERE'); import tx_demo; print(' '.join(tx_demo.DEMO_HOLIDAYS))"); do
    holidays+="{\"name\":\"PriorAuthDeadlines__TexasMedicaid__Holidays__$i\",\"value\":\"$d\",\"valueFrom\":null},"; i=$((i + 1))
  done
  kubectl patch deploy/authorization-service -n "$NAMESPACE" --type strategic -p "{
    \"spec\":{\"template\":{\"spec\":{\"containers\":[{\"name\":\"authorization-service\",\"env\":[
      {\"name\":\"PriorAuthDeadlines__TexasMedicaid__Tenants__0\",\"value\":\"$TENANT\",\"valueFrom\":null},
      ${holidays}
      {\"name\":\"PriorAuthDeadlines__TexasMedicaid__TimeZone\",\"value\":\"America/Chicago\",\"valueFrom\":null}]}]}}}}"
  kubectl patch deploy/claims-service -n "$NAMESPACE" --type strategic -p '{
    "spec":{"template":{"spec":{
      "volumes":[{"name":"cpcds-drops","emptyDir":{"sizeLimit":"1Gi"}}],
      "containers":[{"name":"claims-service",
        "volumeMounts":[{"name":"cpcds-drops","mountPath":"/drops"}],
        "env":[
          {"name":"CpcdsDrop__Enabled","value":"true","valueFrom":null},
          {"name":"CpcdsDrop__Store","value":"Folder","valueFrom":null},
          {"name":"CpcdsDrop__Root","value":"/drops","valueFrom":null},
          {"name":"CpcdsDrop__Interval","value":"00:00:30","valueFrom":null},
          {"name":"CpcdsDrop__QuietPeriod","value":"00:00:15","valueFrom":null},
          {"name":"Adjudication__Shadow__Enabled","value":"true","valueFrom":null},
          {"name":"Services__EnrollmentImportService","value":"http://enrollment-import-service","valueFrom":null},
          {"name":"Services__DocumentService","value":"http://document-service","valueFrom":null}]}]}}}}'
  kubectl patch deploy/benefit-plan-service -n "$NAMESPACE" --type strategic -p '{
    "spec":{"template":{"spec":{"containers":[{"name":"benefit-plan-service","env":[
      {"name":"ServiceCategoryMapping__AdminWriteEnabled","value":"true","valueFrom":null}]}]}}}}'
  # CRD on fhir-service: the plan's documentation rule for the MRI the prior
  # authorization walkthrough orders, so CRD answers with a documentation-
  # required card and coverage-information naming the DTR form. (No portal
  # link: fhir-service links only an https portal, and this one is reached
  # by port-forward.)
  kubectl patch deploy/fhir-service -n "$NAMESPACE" --type strategic -p '{
    "spec":{"template":{"spec":{"containers":[{"name":"fhir-service","env":[
      {"name":"Cms0057__Crd__DocumentationRequiredCodes__0","value":"70553","valueFrom":null}]}]}}}}'
  for d in authorization-service claims-service benefit-plan-service fhir-service; do
    kubectl rollout status "deploy/$d" -n "$NAMESPACE" --timeout=10m >/dev/null
  done
  echo "  Texas decision clock on for tenant '$TENANT'; CPCDS drop and shadow adjudication on; CRD documentation rule for 70553."
}

FORWARDS=("portal 8080" "claims-service 5001" "benefit-plan-service 5002" "provider-service 5004"
          "authorization-service 5006" "coverage-service 5012" "tenant-service 5013" "fhir-service 5023")

cmd_connect() {
  need kubectl
  local pids=() entry svc port
  trap 'kill "${pids[@]}" 2>/dev/null || true' EXIT INT TERM
  for entry in "${FORWARDS[@]}"; do
    read -r svc port <<<"$entry"
    ( while true; do kubectl port-forward -n "$NAMESPACE" "svc/$svc" "$port:80" >/dev/null 2>&1 || true; sleep 1; done ) &
    pids+=($!)
    echo "  $svc → http://localhost:$port"
  done
  echo "Forwarding. Portal: http://localhost:8080. Ctrl-C to stop."
  wait
}

ports_open() { curl -s -o /dev/null --max-time 3 "http://localhost:5001/health/live"; }

cmd_drop() {
  need kubectl python3
  local dir="${1:-}"
  if [[ -z "$dir" ]]; then
    local out; out="$(mktemp -d)"
    python3 "$HERE/tx_demo.py" generate --out "$out"
    dir="$(find "$out" -mindepth 1 -maxdepth 1 -type d | head -1)"
  fi
  local pod
  pod="$(kubectl get pods -n "$NAMESPACE" -l app=claims-service --field-selector=status.phase=Running \
          -o jsonpath='{.items[0].metadata.name}')"
  [[ -n "$pod" ]] || die "no running claims-service pod"
  say "Dropping $(basename "$dir") into $TENANT/inbound on $pod"
  local inbound="/drops/$TENANT/inbound" f names=()
  kubectl exec -n "$NAMESPACE" "$pod" -- mkdir -p "$inbound"
  for f in "$dir"/*.csv; do
    # Upload under a dot name: the drop never reads dot files.
    kubectl cp "$f" "$NAMESPACE/$pod:$inbound/.$(basename "$f")"
    names+=("$(basename "$f")")
    echo "  $(basename "$f")"
  done
  # Release the whole drop at once, with one timestamp, so the drop's worker
  # sees every file become ready together and loads members before claims.
  kubectl exec -n "$NAMESPACE" "$pod" -- sh -c \
    'cd "$1" && shift && for f; do touch ".$f"; done && for f; do mv ".$f" "$f"; done' sh "$inbound" "${names[@]}"
  echo "  Released. Waiting for the load (the quiet period is 15 seconds):"
  sleep 20
  python3 "$HERE/tx_demo.py" drop-run --expect "${names[@]}"
}

# Opens port-forwards unless they are already open; prints the pid to stop, or nothing.
open_ports() {
  if ! ports_open; then
    cmd_connect >/dev/null 2>&1 &
    local pid=$!
    for _ in $(seq 1 30); do ports_open && break; sleep 2; done
    ports_open || die "could not reach claims-service through a port-forward"
    echo "$pid"
  fi
}

cmd_seed() {
  need python3 curl
  # A deploy since "up" resets the deployments to the shared manifests.
  cmd_overlay
  say "Opening port-forwards for this run"
  local own; own=$(open_ports)
  python3 "$HERE/tx_demo.py" setup
  cmd_drop ""
  python3 "$HERE/tx_demo.py" seed-pa
  [[ -z "$own" ]] || kill "$own" 2>/dev/null || true
  say "Loaded. Follow docs/deploy/TEXAS-MEDICAID-DEMO.md for the click-through."
}

# The prior-authorization requests only: closes the earlier ones and opens
# today's set, received minutes ago, so no Texas clock has run out. The
# poststabilization request runs out about 40 minutes later: run this just
# before a showing or a recording.
cmd_clocks() {
  need python3 curl
  cmd_overlay
  local own; own=$(open_ports)
  python3 "$HERE/tx_demo.py" seed-pa
  [[ -z "$own" ]] || kill "$own" 2>/dev/null || true
}

case "${1:-}" in
  up) cmd_up ;;
  down) cmd_down ;;
  status) cmd_status ;;
  overlay) cmd_overlay ;;
  connect) cmd_connect ;;
  drop) cmd_drop "${2:-}" ;;
  seed) cmd_seed ;;
  clocks) cmd_clocks ;;
  *) sed -n '2,33p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac

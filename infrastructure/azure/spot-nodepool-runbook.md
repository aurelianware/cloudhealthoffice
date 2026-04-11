# AKS Spot User Nodepool — Setup Runbook

This runbook adds a Spot user nodepool to `cho-aks` so KEDA-driven workloads
(claims-examiner-service, api-docs, future scale-to-zero candidates) can fully
release nodes when idle. Without this, pod-level scale-to-zero saves nothing
because the system pool stays at min=2 nodes 24/7.

## One-time: add the nodepool

```bash
az aks nodepool add \
  --resource-group rg-cloudhealthoffice-prod \
  --cluster-name cho-aks \
  --name userspot \
  --mode User \
  --priority Spot \
  --eviction-policy Delete \
  --spot-max-price -1 \
  --enable-cluster-autoscaler \
  --node-count 1 \
  --min-count 0 \
  --max-count 1 \
  --node-vm-size Standard_D2s_v4 \
  --node-taints kubernetes.azure.com/scalesetpriority=spot:NoSchedule \
  --labels workload=spot
```

Notes:
- `--spot-max-price -1` means "pay up to the on-demand price" — never get
  evicted on price, only on capacity reclaim.
- `Standard_D2s_v4` is general-purpose Intel (2 vCPU / 8 GB), significantly
  cheaper than the system pool's confidential-compute `Standard_DC2s_v3`.
  The AMD `_as_` family (e.g. `Standard_D2as_v5`) is blocked in this
  subscription's eastus policy — stick with Intel SKUs from the allowed
  list. Pick a larger SKU if the workload needs more CPU/memory.
- `--max-count 1` is constrained by the subscription's LowPriorityCores
  quota, which is 3 vCPUs in eastus at time of writing. One D2s_v4 node =
  2 vCPUs, fits under the limit. `--node-count 1` is required alongside
  `--min-count`/`--max-count` — the default `--node-count 3` would exceed
  the max. Request a quota increase at
  https://aka.ms/ProdportalCRP/#blade/Microsoft_Azure_Capacity/UsageAndQuota.ReactView
  (ask for `Low Priority vCPUs` in eastus, 16 is a reasonable ceiling).
  Once approved, bump with `az aks nodepool update -n userspot --max-count N`.
- Min-count 0 is what makes savings real: when KEDA scales the pod to 0,
  the cluster autoscaler will drain and remove the spot node within
  ~10 minutes (`scaleDownUnneededTime` from `az aks show`).
- The taint forces deployments to opt in via toleration — nothing accidentally
  lands on a Spot node and gets evicted mid-run.

## Required deployment changes

Every workload that should run on this pool needs both a tolerati on and a
nodeSelector. Add to the pod spec:

```yaml
spec:
  template:
    spec:
      nodeSelector:
        workload: spot
      tolerations:
        - key: kubernetes.azure.com/scalesetpriority
          operator: Equal
          value: spot
          effect: NoSchedule
```

Workloads to migrate first (lowest cold-start risk):
1. `claims-examiner-service` — pure background Kafka consumer, no synchronous
   callers. The KEDA Kafka ScaledObject in
   `infrastructure/k8s/claims-examiner-service-scaledobject.yaml` already
   targets it; once on the spot pool, scale-to-zero actually releases the node.
2. `api-docs` — public-facing but non-critical, behind Cloudflare. The KEDA
   HTTP add-on holds incoming requests during cold-start, so users see
   latency on the first request after idle but no errors.

Stateful workloads (Mongo, Kafka itself if self-hosted, anything with a PVC
that can't tolerate node loss) should stay on the system pool.

## Eviction-readiness checklist

Before moving a workload to Spot, confirm:
- [ ] PodDisruptionBudget set (or workload tolerates a single replica restart)
- [ ] No long-running in-memory state that loses progress on pod death
- [ ] Health probes correctly fail closed during shutdown so the load balancer
      drains before the container exits
- [ ] If the pod holds Kafka offsets, commits happen frequently enough that
      replay-from-last-commit on a new pod is acceptable

## Rollback

```bash
az aks nodepool delete \
  --resource-group rg-cloudhealthoffice-prod \
  --cluster-name cho-aks \
  --name userspot
```

This drains gracefully if the autoscaler can move pods elsewhere. Pods with a
hard `nodeSelector: workload=spot` will go Pending — remove the selector or
delete the workload first.

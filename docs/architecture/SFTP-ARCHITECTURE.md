# SFTP Integration Architecture

## Component Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         Cloud Health Office                              │
│                    SFTP Clearinghouse Integration                        │
└─────────────────────────────────────────────────────────────────────────┘

┌──────────────────┐        ┌──────────────────┐        ┌─────────────────┐
│  Clearinghouse   │        │  Kubernetes      │        │  Argo Workflows │
│  (Availity,      │◄──────►│  SFTP Server     │◄──────►│  on AKS         │
│  Change HC, etc) │  SSH   │  (atmoz/sftp)    │  API   │  (DAG Steps)    │
└──────────────────┘        └──────────────────┘        └─────────────────┘
                                     │
                                     │ PersistentVolume
                                     ▼
                            ┌──────────────────┐
                            │  Azure Disk      │
                            │  (10GB)          │
                            └──────────────────┘
```

## Data Flow

### Outbound (Payer → Clearinghouse)

```
┌─────────────┐    ┌──────────────┐    ┌─────────────┐    ┌──────────────┐
│ Service Bus │───►│ Argo DAG     │───►│ SFTP        │───►│ Clearinghouse│
│ (edi-278)   │    │ Step         │    │ Upload      │    │              │
└─────────────┘    └──────────────┘    └─────────────┘    └──────────────┘
                          │
                          ▼ generate X12
                   ┌──────────────┐
                   │ C# X12       │
                   │ Service      │
                   │ (EDI gen)    │
                   └──────────────┘

File Path: /home/argoworkflow/upload/278/PA_2024020412345.x12
```

### Inbound (Clearinghouse → Payer)

```
┌──────────────┐    ┌─────────────┐    ┌──────────────┐    ┌─────────────┐
│ Clearinghouse│───►│ SFTP        │───►│ Argo DAG     │───►│ Service Bus │
│              │    │ Download    │    │ Step         │    │ (edi-277)   │
└──────────────┘    └─────────────┘    └──────────────┘    └─────────────┘
                                               │
                                               ▼ parse X12
                                        ┌──────────────┐
                                        │ C# X12       │
                                        │ Service      │
                                        └──────────────┘

File Path: /home/argoworkflow/download/277/STATUS_*.x12
```

## Kubernetes Resource Topology

```
Namespace: cho-sftp
│
├── Deployment: sftp-server (1 replica)
│   └── Pod: sftp-server-xxxxxxxxx-xxxxx
│       └── Container: atmoz/sftp:latest
│           ├── Port: 22 (SFTP)
│           ├── Volume: sftp-data → /home
│           ├── Volume: ssh-host-keys → /etc/ssh
│           └── Volume: sftp-config → /etc/sftp
│
├── Service: sftp-service (LoadBalancer)
│   ├── External IP: 52.168.45.123 (example)
│   └── Port: 22:32022/TCP
│
├── PersistentVolumeClaim: sftp-data (10Gi)
│   └── StorageClass: default
│
├── Secret: sftp-users
│   └── users.conf (base64)
│       ├── argoworkflow:password:1000:100:upload
│       └── clearinghouse:password:1001:101:edi
│
├── Secret: ssh-host-keys
│   ├── ssh_host_ed25519_key
│   ├── ssh_host_ed25519_key.pub
│   ├── ssh_host_rsa_key
│   └── ssh_host_rsa_key.pub
│
├── ConfigMap: sshd-config
│   └── sshd_config
│
├── Job: generate-ssh-keys (Completed)
│   └── Generates persistent host keys
│
└── ServiceAccount: ssh-key-generator
    └── Role: create secrets
```

## Directory Structure

```
/home/
│
├── argoworkflow/ (UID: 1000)
│   ├── upload/              # Outbound to clearinghouses
│   │   ├── 275/             # Prior auth attachments
│   │   │   └── PA_20240204_12345.x12
│   │   ├── 278/             # Review requests
│   │   │   └── REQ_20240204_67890.x12
│   │   └── 837/             # Professional claims
│   │       └── CLM_20240204_11111.x12
│   │
│   └── download/            # Inbound from clearinghouses
│       ├── 277/             # Claim status responses
│       │   └── STATUS_20240204_22222.x12
│       └── 835/             # Remittance advice
│           └── ERA_20240204_33333.x12
│
└── clearinghouse/ (UID: 1001)
    └── edi/                 # Bi-directional
        ├── inbound/
        └── outbound/
```

## Network Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         Internet / Public Network                        │
└─────────────────────────────────────────────────────────────────────────┘
                                     │
                                     │ Port 22 (SSH/SFTP)
                                     ▼
                          ┌─────────────────────┐
                          │  Azure Load         │
                          │  Balancer           │
                          │  (Kubernetes        │
                          │   Service)          │
                          └─────────────────────┘
                                     │
                         ┌───────────┼───────────┐
                         ▼           ▼           ▼
                   ┌─────────┐ ┌─────────┐ ┌─────────┐
                   │ Worker  │ │ Worker  │ │ Worker  │
                   │ Node 1  │ │ Node 2  │ │ Node 3  │
                   └─────────┘ └─────────┘ └─────────┘
                         │
                         │ Pod scheduled on one node
                         ▼
                   ┌─────────────────────────┐
                   │ SFTP Pod                │
                   │ ┌─────────────────────┐ │
                   │ │ atmoz/sftp          │ │
                   │ │ - Port 22           │ │
                   │ │ - PersistentVolume  │ │
                   │ └─────────────────────┘ │
                   └─────────────────────────┘
                         │
                         │ Azure Disk (Persistent)
                         ▼
                   ┌─────────────────────────┐
                   │ Azure Managed Disk      │
                   │ (10GB, Standard_LRS)    │
                   └─────────────────────────┘
```

## Argo Workflows SFTP Connection

```
┌────────────────────────────────────────────────────────────────────────┐
│  AKS Cluster: cho-aks / Namespace: argo                                │
├────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │  Kubernetes Secret: cho-sftp-credentials                         │ │
│  │                                                                  │ │
│  │  Data:                                                           │ │
│  │    hostName: 52.168.45.123                                       │ │
│  │    portNumber: 22                                                │ │
│  │    userName: argoworkflow                                        │ │
│  │    password: <from Azure Key Vault via CSI driver>               │ │
│  │    sshHostKeyFingerprint: SHA256:... (prod only)                 │ │
│  └──────────────────────────────────────────────────────────────────┘ │
│                            │                                            │
│                            │ mounted by                                 │
│                            ▼                                            │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │  Argo Workflow: 278-review-request                               │ │
│  │  (infrastructure/argo-workflows/)                                │ │
│  │                                                                  │ │
│  │  Trigger: Service Bus Topic (edi-278) via Argo Events            │ │
│  │     ↓                                                            │ │
│  │  DAG Step: Generate X12 (C# service)                             │ │
│  │     ↓                                                            │ │
│  │  DAG Step: SFTP Upload file                                      │ │
│  │     Path: /upload/278/{{workflow.uid}}.x12                       │ │
│  │     Credentials: cho-sftp-credentials secret                     │ │
│  └──────────────────────────────────────────────────────────────────┘ │
│                                                                         │
└────────────────────────────────────────────────────────────────────────┘
```

## Security Layers

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Layer 1: Network Security                                               │
├─────────────────────────────────────────────────────────────────────────┤
│ ▪ LoadBalancer source IP filtering                                      │
│ ▪ Kubernetes NetworkPolicy (optional)                                   │
│ ▪ Azure NSG rules (if using internal LB + VNet)                         │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│ Layer 2: SSH Authentication                                             │
├─────────────────────────────────────────────────────────────────────────┤
│ ▪ Password-based auth (username + password)                             │
│ ▪ Key-based auth (public/private key pairs) - optional                  │
│ ▪ SSH host key verification (fingerprint pinning)                       │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│ Layer 3: File System Isolation                                          │
├─────────────────────────────────────────────────────────────────────────┤
│ ▪ Chrooted directories per user                                         │
│ ▪ UID/GID separation (argoworkflow=1000, clearinghouse=1001)            │
│ ▪ Linux file permissions (700 directories, 600 files)                   │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│ Layer 4: Secrets Management                                             │
├─────────────────────────────────────────────────────────────────────────┤
│ ▪ Kubernetes Secrets (base64 encoded)                                   │
│ ▪ Azure Key Vault (SFTP passwords)                                      │
│ ▪ SSH host keys in persistent Secret (not regenerated on restart)       │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│ Layer 5: Storage Encryption                                             │
├─────────────────────────────────────────────────────────────────────────┤
│ ▪ Azure Disk encryption-at-rest (AES-256)                               │
│ ▪ PersistentVolume encrypted by default                                 │
│ ▪ Optional: Customer-managed keys (BYOK)                                │
└─────────────────────────────────────────────────────────────────────────┘
```

## Monitoring & Observability

```
┌─────────────────────────────────────────────────────────────────────────┐
│ Kubernetes Metrics (kubectl)                                            │
├─────────────────────────────────────────────────────────────────────────┤
│ ▪ kubectl get pods -n cho-sftp                                          │
│ ▪ kubectl logs -n cho-sftp -l app=sftp-server -f                        │
│ ▪ kubectl top pod -n cho-sftp                                           │
│ ▪ kubectl exec -n cho-sftp deployment/sftp-server -- df -h              │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│ Prometheus / Grafana (Argo Workflows)                                   │
├─────────────────────────────────────────────────────────────────────────┤
│ argo_workflows_count{status="Succeeded"}                                │
│ argo_workflows_count{status="Failed"}                                   │
│ Custom metrics: sftp_upload_success_total, sftp_upload_failed_total      │
│ Grafana dashboard: SFTP Transfer Metrics                                │
└─────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────┐
│ SFTP Server Logs (inside pod)                                           │
├─────────────────────────────────────────────────────────────────────────┤
│ ▪ /var/log/auth.log - SSH authentication attempts                       │
│ ▪ /var/log/sftp/ - File transfer logs                                   │
│ ▪ stdout/stderr - Container logs (kubectl logs)                         │
└─────────────────────────────────────────────────────────────────────────┘
```

## Disaster Recovery

```
┌──────────────────┐
│ Backup Strategy  │
├──────────────────┴────────────────────────────────────────────────┐
│                                                                    │
│  Automated Snapshots:                                             │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │ Azure Disk Snapshot Policy                                   │ │
│  │ ▪ Daily snapshots at 02:00 UTC                               │ │
│  │ ▪ Retention: 7 days                                          │ │
│  │ ▪ Cost: ~$0.05/GB/month                                      │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  Manual Backups:                                                  │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │ kubectl cp cho-sftp/sftp-pod:/home ./backup-$(date +%F)      │ │
│  │ tar -czf sftp-backup.tar.gz ./backup-*                       │ │
│  │ az storage blob upload ...                                   │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  Restore Procedure:                                               │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │ 1. Create PV from snapshot                                   │ │
│  │ 2. Update PVC to use restored volume                         │ │
│  │ 3. kubectl rollout restart deployment/sftp-server            │ │
│  │ 4. Verify data integrity                                     │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘

┌──────────────────┐
│ Failover Plan    │
├──────────────────┴────────────────────────────────────────────────┐
│                                                                    │
│  Pod Failure (automatic):                                         │
│  ▪ Kubernetes restarts pod on same/different node                │
│  ▪ PersistentVolume reattaches to new pod                         │
│  ▪ RTO: ~30 seconds                                               │
│                                                                    │
│  Node Failure (automatic):                                        │
│  ▪ Pod rescheduled to healthy node                                │
│  ▪ PersistentVolume migrates with pod                             │
│  ▪ RTO: ~2 minutes                                                │
│                                                                    │
│  Region Failure (manual):                                         │
│  ▪ Deploy to secondary region                                     │
│  ▪ Restore from geo-replicated snapshot                           │
│  ▪ Update DNS / LoadBalancer IP in Argo workflow config           │
│  ▪ RTO: ~15 minutes (assuming automation)                         │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

## Related Documentation

- **[SFTP Integration Guide](./SFTP-INTEGRATION-GUIDE.md)** - Full setup and configuration
- **[SFTP Quick Start](./SFTP-QUICKSTART.md)** - 5-minute deployment reference
- **[Kubernetes Deployment](../k8s/sftp-server-deployment.yaml)** - Complete manifest
- **[Deployment Scripts](../scripts/deploy-sftp-server.sh)** - Automated setup
- **[Connection Config](../scripts/configure-sftp-connection.sh)** - Argo Workflows SFTP integration

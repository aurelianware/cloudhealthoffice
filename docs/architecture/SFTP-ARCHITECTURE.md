# SFTP Integration Architecture

## Component Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         Cloud Health Office                              │
│                    SFTP Clearinghouse Integration                        │
└─────────────────────────────────────────────────────────────────────────┘

┌──────────────────┐        ┌──────────────────┐        ┌─────────────────┐
│  Clearinghouse   │        │  Kubernetes      │        │  Azure Logic    │
│  (Availity,      │◄──────►│  SFTP Server     │◄──────►│  Apps           │
│  Change HC, etc) │  SSH   │  (atmoz/sftp)    │  API   │  (Workflows)    │
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
│ Service Bus │───►│ Logic App    │───►│ SFTP        │───►│ Clearinghouse│
│ (edi-278)   │    │ Workflow     │    │ Upload      │    │              │
└─────────────┘    └──────────────┘    └─────────────┘    └──────────────┘
                          │
                          ▼ encode X12
                   ┌──────────────┐
                   │ Integration  │
                   │ Account      │
                   │ (X12 Schema) │
                   └──────────────┘

File Path: /home/logicapp/upload/278/PA_2024020412345.x12
```

### Inbound (Clearinghouse → Payer)

```
┌──────────────┐    ┌─────────────┐    ┌──────────────┐    ┌─────────────┐
│ Clearinghouse│───►│ SFTP        │───►│ Logic App    │───►│ Service Bus │
│              │    │ Download    │    │ Polling      │    │ (edi-277)   │
└──────────────┘    └─────────────┘    └──────────────┘    └─────────────┘
                                               │
                                               ▼ decode X12
                                        ┌──────────────┐
                                        │ Integration  │
                                        │ Account      │
                                        └──────────────┘

File Path: /home/logicapp/download/277/STATUS_*.x12
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
│       ├── logicapp:password:1000:100:upload
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
├── logicapp/ (UID: 1000)
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

## Azure Logic Apps Connection

```
┌────────────────────────────────────────────────────────────────────────┐
│  Azure Resource Group: rg-hipaa-logic-apps                             │
├────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │  API Connection: cho-sftp                                        │ │
│  │  Type: Microsoft.Web/connections                                 │ │
│  │  Connector: sftpwithssh                                          │ │
│  │                                                                  │ │
│  │  Parameters:                                                     │ │
│  │    hostName: 52.168.45.123                                       │ │
│  │    portNumber: 22                                                │ │
│  │    userName: logicapp                                            │ │
│  │    password: <from Key Vault>                                    │ │
│  │    acceptAnySshHostKey: true (dev) / false (prod)                │ │
│  │    sshHostKeyFingerprint: SHA256:... (prod only)                 │ │
│  └──────────────────────────────────────────────────────────────────┘ │
│                            │                                            │
│                            │ referenced by                              │
│                            ▼                                            │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │  Logic App Workflow: 278-review-request                          │ │
│  │                                                                  │ │
│  │  Trigger: Service Bus Topic (edi-278)                            │ │
│  │     ↓                                                            │ │
│  │  Action: X12 Encode (Integration Account)                        │ │
│  │     ↓                                                            │ │
│  │  Action: SFTP - Upload file                                      │ │
│  │     Path: /upload/278/@{workflow().run.id}.x12                   │ │
│  │     Connection: cho-sftp                                         │ │
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
│ ▪ UID/GID separation (logicapp=1000, clearinghouse=1001)                │
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
│ Azure Monitor (Logic Apps)                                              │
├─────────────────────────────────────────────────────────────────────────┤
│ customEvents                                                             │
│ | where name in ("SFTP_Upload_Success", "SFTP_Upload_Failed")           │
│ | summarize Count=count() by name, bin(timestamp, 1h)                   │
│ | render timechart                                                       │
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
│  ▪ Update DNS / LoadBalancer IP in Logic Apps                     │
│  ▪ RTO: ~15 minutes (assuming automation)                         │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

## Related Documentation

- **[SFTP Integration Guide](./SFTP-INTEGRATION-GUIDE.md)** - Full setup and configuration
- **[SFTP Quick Start](./SFTP-QUICKSTART.md)** - 5-minute deployment reference
- **[Kubernetes Deployment](../k8s/sftp-server-deployment.yaml)** - Complete manifest
- **[Deployment Scripts](../scripts/deploy-sftp-server.sh)** - Automated setup
- **[Connection Config](../scripts/configure-sftp-connection.sh)** - Logic Apps integration

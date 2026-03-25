# SFTP Integration Setup Guide

## Overview

Cloud Health Office now includes a Kubernetes-hosted SFTP server for EDI file exchange with clearinghouses (Availity, Change Healthcare, Optum, etc.).

## Architecture

```
Clearinghouse → SFTP Server (K8s) → Argo Workflows (AKS) → Service Bus → Processing Pipeline
              ↑
              └── atmoz/sftp container with persistent storage
```

## Quick Start (5 minutes)

### 1. Deploy SFTP to Kubernetes

```bash
./scripts/deploy-sftp-server.sh
```

This creates:
- **Namespace**: `cho-sftp`
- **Storage**: 10GB PersistentVolumeClaim
- **Service**: LoadBalancer on port 22
- **Users**: `logicapp` and `clearinghouse`

### 2. Get SFTP Server IP

```bash
kubectl get svc sftp-service -n cho-sftp
```

Example output:
```
NAME            TYPE           EXTERNAL-IP      PORT(S)        AGE
sftp-service    LoadBalancer   52.168.45.123    22:32022/TCP   2m
```

### 3. Configure Argo Workflows SFTP Connection

```bash
./scripts/configure-sftp-connection.sh
```

This will:
- Auto-detect SFTP IP from Kubernetes
- Prompt for Azure resource details
- Create/update the `sftpwithssh` API connection
- Output the connection details for Argo Workflows

### 4. Update Infrastructure Parameters

Edit `infra/main.parameters.json`:

```json
{
  "sftpHost": {
    "value": "52.168.45.123"  // Use your LoadBalancer IP
  },
  "sftpUsername": {
    "value": "logicapp"
  },
  "sftpPassword": {
    "reference": {
      "keyVault": {
        "id": "/subscriptions/.../Microsoft.KeyVault/vaults/cho-secrets"
      },
      "secretName": "sftp-logicapp-password"
    }
  }
}
```

**Important**: Store password in Key Vault, not in parameters file!

```bash
# Store SFTP password in Key Vault
az keyvault secret set \
  --vault-name cho-secrets \
  --name sftp-logicapp-password \
  --value "your-secure-password"
```

### 5. Test Connection

```bash
# Test from local machine
sftp logicapp@52.168.45.123
# Password: changeme123 (default)

# Test from Azure
az resource invoke-action \
  --resource-group rg-hipaa-aks \
  --resource-type Microsoft.Web/connections \
  --name cho-sftp \
  --action testConnection \
  --api-version 2016-06-01
```

## Security Hardening

### Change Default Passwords

**Critical**: Default credentials are for testing only!

```bash
# Edit the secret in Kubernetes
kubectl edit secret sftp-users -n cho-sftp

# Update the users list (base64 encoded):
# Format: username:password:uid:gid:directory

# Generate new password entry:
echo "logicapp:NewSecurePass123:1000:100:upload" | base64

# Restart SFTP pods to apply:
kubectl rollout restart deployment/sftp-server -n cho-sftp
```

### SSH Host Key Fingerprint

For production, pin the SSH host key:

```bash
# Get the fingerprint
kubectl exec -n cho-sftp deployment/sftp-server -- \
  ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub

# Output example:
# 256 SHA256:abc123xyz... root@sftp-server (ED25519)
```

Update `infra/main.bicep`:

```bicep
parameterValues: {
  hostName: sftpHost
  username: sftpUsername
  password: sftpPassword
  acceptAnySshHostKey: false  // Changed from true
  sshHostKeyFingerprint: 'SHA256:abc123xyz...'  // Add this
}
```

### Network Security

**Option 1**: LoadBalancer with source IP filtering

```yaml
# k8s/sftp-server-deployment.yaml
spec:
  loadBalancerSourceRanges:
    - 52.1.2.0/24    # Availity IPs
    - 52.10.20.0/24  # Change Healthcare IPs
    - 52.30.40.0/24  # AKS cluster outbound IPs
```

**Option 2**: Internal LoadBalancer (private IP)

```yaml
# k8s/sftp-server-deployment.yaml
metadata:
  annotations:
    service.beta.kubernetes.io/azure-load-balancer-internal: "true"
```

Then use VNet peering or VPN to connect AKS/Argo Workflows.

## Directory Structure

### User: `logicapp`
```
/home/logicapp/
├── upload/          # Outbound EDI files to clearinghouses
│   ├── 275/        # Prior auth attachments
│   ├── 278/        # Review requests
│   └── 837/        # Professional claims
└── download/        # Inbound from clearinghouses
    ├── 277/        # Claim status responses
    └── 835/        # Remittance advice
```

### User: `clearinghouse`
```
/home/clearinghouse/
└── edi/             # Bi-directional exchange
    ├── inbound/
    └── outbound/
```

## Argo Workflows Integration

### Workflow Example: Upload 275 to Clearinghouse

```json
{
  "type": "ApiConnection",
  "inputs": {
    "host": {
      "connection": {
        "name": "@parameters('$connections')['sftpwithssh']['connectionId']"
      }
    },
    "method": "post",
    "path": "/datasets/default/files",
    "queries": {
      "folderPath": "/upload/275",
      "name": "@{concat('PA_', workflow().run.id, '.x12')}",
      "queryParametersSingleEncoded": true
    },
    "body": "@variables('encodedX12')"
  }
}
```

### Connection Parameters in Argo Workflows

```json
{
  "sftpwithssh": {
    "connectionId": "/subscriptions/.../providers/Microsoft.Web/connections/cho-sftp",
    "connectionName": "cho-sftp",
    "id": "/subscriptions/.../providers/Microsoft.Web/locations/westus2/managedApis/sftpwithssh"
  }
}
```

## Monitoring & Operations

### View Logs

```bash
# SFTP server logs
kubectl logs -n cho-sftp -l app=sftp-server -f

# Recent connections
kubectl exec -n cho-sftp deployment/sftp-server -- \
  tail -f /var/log/auth.log
```

### Check Storage Usage

```bash
kubectl exec -n cho-sftp deployment/sftp-server -- df -h /home
```

### Backup Files

```bash
# Copy all files from SFTP to local
kubectl cp cho-sftp/sftp-server-<pod-name>:/home ./sftp-backup/
```

### Scale for High Availability

```bash
# Not recommended - SFTP is stateful
# Use single replica with persistent storage instead
```

## Troubleshooting

### LoadBalancer IP Pending

**Symptoms**: `kubectl get svc` shows `<pending>` for EXTERNAL-IP

**Causes**:
- Cluster doesn't have LoadBalancer controller
- Cloud provider quota exceeded
- Network policy blocking

**Solutions**:
1. **Use NodePort** (local/dev):
   ```bash
   kubectl patch svc sftp-service -n cho-sftp -p '{"spec":{"type":"NodePort"}}'
   ```

2. **Port Forward** (testing):
   ```bash
   kubectl port-forward -n cho-sftp svc/sftp-service 2222:22
   sftp -P 2222 logicapp@localhost
   ```

3. **Install MetalLB** (on-prem):
   ```bash
   kubectl apply -f https://raw.githubusercontent.com/metallb/metallb/v0.13.12/config/manifests/metallb-native.yaml
   ```

### Connection Refused

**Check firewall rules**:
```bash
kubectl get networkpolicies -n cho-sftp
```

**Verify pod is running**:
```bash
kubectl get pods -n cho-sftp
kubectl describe pod sftp-server-<pod-id> -n cho-sftp
```

### Authentication Failed

**Reset password**:
```bash
kubectl edit secret sftp-users -n cho-sftp
kubectl rollout restart deployment/sftp-server -n cho-sftp
```

**Check SSH keys** (if using key-based auth):
```bash
kubectl exec -n cho-sftp deployment/sftp-server -- \
  cat /home/logicapp/.ssh/authorized_keys
```

## Production Checklist

- [ ] Changed default passwords
- [ ] Stored credentials in Azure Key Vault
- [ ] Configured SSH host key fingerprint
- [ ] Applied LoadBalancer source IP restrictions
- [ ] Set up PVC backup/snapshot policy
- [ ] Configured Azure Monitor alerts for pod restarts
- [ ] Documented clearinghouse IP addresses
- [ ] Tested failover scenario (pod restart)
- [ ] Verified Argo Workflow pods can read/write files
- [ ] Set up file retention/cleanup job

## Next Steps

1. **Deploy**: `./scripts/deploy-sftp-server.sh`
2. **Configure**: `./scripts/configure-sftp-connection.sh`
3. **Secure**: Change passwords, pin SSH keys
4. **Test**: Upload sample EDI file via Argo Workflows
5. **Monitor**: Set up alerts in Azure Monitor

---

**Need Help?**
- Check logs: `kubectl logs -n cho-sftp -l app=sftp-server`
- Test connection: `sftp logicapp@<IP>`
- Validate k8s resources: `kubectl get all -n cho-sftp`
